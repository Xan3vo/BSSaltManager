using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using BssManager.Models;
using BssManager.Native;

namespace BssManager.Services;

/// <summary>
/// Reads live Windows session state and drives mstsc.
///
/// State comes from the Terminal Services API rather than from tracking
/// mstsc.exe processes. Sessions outlive the client that created them, survive
/// app restarts, and can be reconnected from anywhere -- so the session table is
/// the only source of truth that stays honest.
/// </summary>
public class SessionService
{
    private readonly RdpFileService _rdpFiles = new();

    /// <summary>
    /// Whether Roblox is up in a given session.
    ///
    /// Handing a launch URL to a session only proves the session took it. This
    /// is the difference between "the message was delivered" and "the game is
    /// running", and it is the second one anybody cares about.
    /// </summary>
    public bool IsRobloxRunning(int sessionId)
    {
        if (sessionId < 0) return false;

        foreach (var process in Process.GetProcessesByName("RobloxPlayerBeta"))
        {
            using (process)
            {
                try
                {
                    if (process.SessionId == sessionId) return true;
                }
                catch
                {
                    // Exited between being listed and being asked.
                }
            }
        }

        return false;
    }

    public IReadOnlyList<SessionInfo> Enumerate()
    {
        var results = new List<SessionInfo>();
        var server = (IntPtr)NativeMethods.WTS_CURRENT_SERVER_HANDLE;

        if (!NativeMethods.WTSEnumerateSessionsW(server, 0, 1, out var buffer, out var count))
        {
            Log.Write($"WTSEnumerateSessions failed: {Marshal.GetLastWin32Error()}");
            return results;
        }

        try
        {
            var size = Marshal.SizeOf<NativeMethods.WTS_SESSION_INFO>();
            for (int i = 0; i < count; i++)
            {
                var current = IntPtr.Add(buffer, i * size);
                var raw = Marshal.PtrToStructure<NativeMethods.WTS_SESSION_INFO>(current);

                var username = QueryString(server, raw.SessionId, NativeMethods.WTS_INFO_CLASS.WTSUserName);
                if (string.IsNullOrEmpty(username)) continue; // listener / services session

                results.Add(new SessionInfo(
                    raw.SessionId,
                    username,
                    Map(raw.State),
                    raw.pWinStationName ?? ""));
            }
        }
        finally
        {
            NativeMethods.WTSFreeMemory(buffer);
        }

        return results;
    }

    public SessionInfo? FindFor(AltProfile alt) =>
        Enumerate().FirstOrDefault(s =>
            string.Equals(s.Username, alt.WindowsUsername, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Opens (or reattaches to) this alt's session. If a session already exists
    /// in a disconnected state, mstsc reconnects to it rather than creating a
    /// second one -- the macro inside keeps running across the reconnect.
    /// </summary>
    public void Launch(AltProfile alt, bool startHidden = false)
    {
        var rdpPath = _rdpFiles.Write(alt);

        var psi = new ProcessStartInfo
        {
            FileName = "mstsc.exe",
            Arguments = $"\"{rdpPath}\"",
            UseShellExecute = true,
            // Ask for the window to come up hidden. mstsc does not always honour
            // this -- it shows its own window regardless on some builds -- so the
            // caller still hides it once it appears. When it is honoured, there is
            // no flash at all: the window is never shown in the first place.
            WindowStyle = startHidden ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal
        };

        Process.Start(psi);
        Log.Write($"launched {alt.WindowsUsername} -> {alt.LoopbackAddress} ({alt.Width}x{alt.Height}){(startHidden ? " (hidden)" : "")}");
    }

    /// <summary>
    /// Detaches the client but leaves the session running.
    ///
    /// Worth knowing before using this: a disconnected session is not guaranteed
    /// to keep composing its desktop, and a macro that reads pixels can stall
    /// once nothing is attached. Minimising the window (with the minimise
    /// registry fix applied) is the safer way to get it out of your way.
    /// </summary>
    public void Disconnect(AltProfile alt)
    {
        var session = FindFor(alt);
        if (session is null) return;

        if (!NativeMethods.WTSDisconnectSession((IntPtr)NativeMethods.WTS_CURRENT_SERVER_HANDLE, session.SessionId, true))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not disconnect the session.");

        Log.Write($"disconnected {alt.WindowsUsername} (session {session.SessionId})");
    }

    /// <summary>Ends the session completely. Anything running inside it dies.</summary>
    public void LogOff(AltProfile alt)
    {
        var session = FindFor(alt);
        if (session is null) return;

        if (!NativeMethods.WTSLogoffSession((IntPtr)NativeMethods.WTS_CURRENT_SERVER_HANDLE, session.SessionId, true))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not log the session off.");

        Log.Write($"logged off {alt.WindowsUsername} (session {session.SessionId})");
    }

    private static SessionState Map(NativeMethods.WTS_CONNECTSTATE_CLASS state) => state switch
    {
        NativeMethods.WTS_CONNECTSTATE_CLASS.WTSActive => SessionState.Active,
        NativeMethods.WTS_CONNECTSTATE_CLASS.WTSConnected => SessionState.Active,
        NativeMethods.WTS_CONNECTSTATE_CLASS.WTSDisconnected => SessionState.Disconnected,
        _ => SessionState.Other
    };

    private static string QueryString(IntPtr server, int sessionId, NativeMethods.WTS_INFO_CLASS info)
    {
        if (!NativeMethods.WTSQuerySessionInformationW(server, sessionId, info, out var buffer, out _))
            return "";

        try
        {
            return Marshal.PtrToStringUni(buffer) ?? "";
        }
        finally
        {
            NativeMethods.WTSFreeMemory(buffer);
        }
    }
}
