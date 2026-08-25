using System.Runtime.InteropServices;
using System.Text;
using BssManager.Models;

namespace BssManager.Services;

/// <summary>
/// Hides and reveals the RDP windows without touching the sessions behind them.
///
/// Running six alts means six mstsc windows and six taskbar buttons for things
/// you never look at. Hiding the window removes both, and the session carries
/// on as it was.
///
/// The distinction that matters here is hidden versus minimised. Minimising an
/// RDP window makes the client tell the server to stop sending updates, the
/// remote desktop stops composing, and any macro reading pixels goes blind --
/// which is what RemoteDesktop_SuppressWhenMinimized = 2 exists to prevent, and
/// why the app checks for it. Hiding is a different thing entirely: the window
/// is never iconic, so that path is not taken at all. Measured while hidden,
/// the client keeps decoding frames at the same rate as when it is on screen.
/// </summary>
public class SessionWindowService
{
    /// <summary>The class mstsc gives its connection window. Child windows are not this.</summary>
    private const string ClientWindowClass = "TscShellContainerClass";

    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);

    private const uint WM_CLOSE = 0x0010;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, StringBuilder name, int count);

    // ------------------------------------------------------------------ find

    /// <summary>
    /// Every RDP client window currently open, with its title.
    ///
    /// Found by class and title rather than by remembering the process we
    /// started: sessions outlive this app, and a window opened before it was
    /// last restarted has to be findable too.
    /// </summary>
    private static List<(IntPtr Handle, string Title)> ClientWindows()
    {
        var found = new List<(IntPtr, string)>();

        var className = new StringBuilder(256);
        var title = new StringBuilder(512);

        EnumWindows((hwnd, _) =>
        {
            className.Clear();
            GetClassNameW(hwnd, className, className.Capacity);
            if (className.ToString() != ClientWindowClass) return true;

            title.Clear();
            GetWindowTextW(hwnd, title, title.Capacity);
            found.Add((hwnd, title.ToString()));
            return true;
        }, IntPtr.Zero);

        return found;
    }

    /// <summary>
    /// The window showing this alt, if one is open. mstsc puts the address in
    /// the title, and every alt has its own loopback address, so that is what
    /// tells six otherwise identical windows apart.
    /// </summary>
    private static IntPtr FindWindow(AltProfile alt)
    {
        foreach (var (handle, title) in ClientWindows())
        {
            if (title.Contains(alt.LoopbackAddress, StringComparison.Ordinal))
                return handle;
        }

        return IntPtr.Zero;
    }

    // ------------------------------------------------------------ hide/show

    public bool HasWindow(AltProfile alt) => FindWindow(alt) != IntPtr.Zero;

    public bool IsHidden(AltProfile alt)
    {
        var handle = FindWindow(alt);
        return handle != IntPtr.Zero && !IsWindowVisible(handle);
    }

    public bool Hide(AltProfile alt)
    {
        var handle = FindWindow(alt);
        if (handle == IntPtr.Zero) return false;

        ShowWindow(handle, SW_HIDE);
        return true;
    }

    public bool Reveal(AltProfile alt)
    {
        var handle = FindWindow(alt);
        if (handle == IntPtr.Zero) return false;

        ShowWindow(handle, SW_SHOW);
        return true;
    }

    /// <summary>Hides every RDP window that is open. Returns how many it hid.</summary>
    public int HideAll()
    {
        var count = 0;

        foreach (var (handle, _) in ClientWindows())
        {
            if (!IsWindowVisible(handle)) continue;
            ShowWindow(handle, SW_HIDE);
            count++;
        }

        return count;
    }

    /// <summary>
    /// Shows every RDP window, including ones this app did not hide.
    ///
    /// Deliberately not limited to known alts. A hidden window has no taskbar
    /// button and no Alt-Tab entry, so if one is ever stranded -- an alt
    /// removed while hidden, a window from a session this app has forgotten --
    /// there is no way back to it except killing mstsc. This is that way back,
    /// and it is why the app also calls it on the way out.
    /// </summary>
    public int RevealAll()
    {
        var count = 0;

        foreach (var (handle, _) in ClientWindows())
        {
            if (IsWindowVisible(handle)) continue;
            ShowWindow(handle, SW_SHOW);
            count++;
        }

        return count;
    }

    /// <summary>
    /// Closes the client attached to this alt, without touching the session.
    ///
    /// Logging a session off while a client is still attached makes that client
    /// put up "Your Remote Desktop Services session has ended" -- an error about
    /// something the user just asked for. Closing the client first means there
    /// is nothing left to complain.
    ///
    /// A hidden window is closed the same way: it cannot be seen, but it is
    /// still attached and will still raise the dialog when it reappears.
    /// </summary>
    public bool CloseClient(AltProfile alt)
    {
        var handle = FindWindow(alt);
        if (handle == IntPtr.Zero) return false;

        PostMessageW(handle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        return true;
    }

    /// <summary>
    /// Waits for this alt's window to appear, then hides it.
    ///
    /// Launching is not instant and the window cannot be hidden before it
    /// exists. Giving up quietly is right: the session is running either way,
    /// and a visible window is a great deal better than a wrong one hidden.
    /// </summary>
    public async Task<bool> HideWhenReadyAsync(
        AltProfile alt, TimeSpan timeout, CancellationToken token = default)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (Hide(alt))
            {
                Log.Write($"hid the RDP window for {alt.WindowsUsername}");
                return true;
            }

            // Tight poll: the whole point is to hide the window before it is
            // seen, so once it exists a fraction of a second matters. This only
            // spins for the moment between the window appearing and being hidden.
            try { await Task.Delay(60, token); }
            catch (OperationCanceledException) { return false; }
        }

        return false;
    }
}
