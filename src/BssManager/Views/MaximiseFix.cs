using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace BssManager.Views;

/// <summary>
/// Keeps a WindowChrome window inside the screen when it is maximised.
///
/// WindowChrome removes the frame Windows would otherwise draw, but Windows
/// still sizes a maximised window as though the frame were there: it covers
/// the work area plus the resize border on every side, so the edges hang off
/// the display and the caption buttons sit partly past the right of the screen.
///
/// The fix is to inset the content by that overhang. What the overhang is
/// cannot be read off SystemParameters -- on a 150% display
/// WindowResizeBorderThickness says 3.33 and the real overhang is 7.33,
/// because the padded border is not counted -- and answering WM_GETMINMAXINFO
/// does not work either, since WindowChrome handles that message first and
/// marks it handled.
///
/// So this measures it instead: where the window actually is against where the
/// monitor's work area actually is. Measured beats predicted, and it stays
/// correct on any monitor at any scaling.
/// </summary>
internal static class MaximiseFix
{
    private const int MONITOR_DEFAULTTONEAREST = 0x00000002;

    /// <summary>
    /// How far the window currently spills past its monitor's work area, in
    /// device-independent units. Zero on every side when it is not maximised.
    /// Call from the window's StateChanged, once the new size has been applied.
    /// </summary>
    public static Thickness Overhang(Window window)
    {
        if (window.WindowState != WindowState.Maximized) return default;

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var win)) return default;

        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return default;

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return default;

        var dpi = VisualTreeHelper.GetDpi(window);
        var work = info.rcWork;

        // Clamped at zero: a window that does not reach an edge needs no inset
        // there, and a negative margin would stretch the content outwards.
        return new Thickness(
            Math.Max(0, (work.left - win.left) / dpi.DpiScaleX),
            Math.Max(0, (work.top - win.top) / dpi.DpiScaleY),
            Math.Max(0, (win.right - work.right) / dpi.DpiScaleX),
            Math.Max(0, (win.bottom - work.bottom) / dpi.DpiScaleY));
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }
}
