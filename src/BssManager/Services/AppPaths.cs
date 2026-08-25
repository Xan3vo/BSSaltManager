using System.Diagnostics;
using System.IO;

namespace BssManager.Services;

public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BssManager");

    /// <summary>
    /// Local, not roaming: a browser profile is tens of megabytes of cache and
    /// has no business following the user to another machine.
    /// </summary>
    public static string LocalRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BssManager");

    public static string ConfigFile => Path.Combine(Root, "config.json");
    public static string RdpFolder => Path.Combine(Root, "rdp");
    public static string LogFile => Path.Combine(Root, "bssmanager.log");

    /// <summary>Throwaway browser profiles, one per Roblox sign-in.</summary>
    public static string LoginProfilesFolder => Path.Combine(LocalRoot, "login-profiles");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(RdpFolder);
    }

    /// <summary>
    /// Deletes one sign-in profile once the browser has let go of it.
    ///
    /// Closing the window does not close the browser: WebView2 keeps its
    /// process alive for a while afterwards, holding the profile open. Deleting
    /// immediately therefore fails every time, so this waits for that process
    /// to exit first. Call it off the UI thread.
    /// </summary>
    public static void DeleteLoginProfileWhenFree(string folder, uint browserProcessId)
    {
        if (browserProcessId != 0)
        {
            try
            {
                using var browser = Process.GetProcessById((int)browserProcessId);
                browser.WaitForExit(30_000);
            }
            catch
            {
                // Already gone, which is the outcome we were waiting for.
            }
        }

        // Handles can outlive the process briefly.
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                if (!Directory.Exists(folder)) return;
                Directory.Delete(folder, recursive: true);
                return;
            }
            catch
            {
                Thread.Sleep(500);
            }
        }

        Log.Write("login profile still locked; leaving it for the next sweep");
    }

    /// <summary>Clears profiles left behind by earlier sign-ins.</summary>
    public static void SweepLoginProfiles()
    {
        try
        {
            Directory.CreateDirectory(LoginProfilesFolder);

            foreach (var folder in Directory.GetDirectories(LoginProfilesFolder))
            {
                try { Directory.Delete(folder, recursive: true); }
                catch { /* still in use; next sweep gets it */ }
            }
        }
        catch (Exception ex)
        {
            Log.Write($"could not sweep login profiles: {ex.Message}");
        }
    }
}
