using System.Runtime.InteropServices;

namespace BitCraftOverlay;

/// <summary>
/// Win32 P/Invoke: WS_EX_TOOLWINDOW keeps the overlay out of Alt+Tab, and
/// GetWindowRect reads the game window's bounds to position the overlay on
/// top of it at startup. WS_EX_TRANSPARENT + GetCursorPos back
/// RouteOverlayWindow's selective click-through: confirmed empirically that
/// WS_EX_TRANSPARENT makes Windows skip the window entirely during hit-testing
/// (a WM_NCHITTEST hook can't "give back" clickability once that ex-style is
/// set - the message never even reaches the window), so instead the ex-style
/// is toggled live based on cursor position: off while the cursor is over the
/// interactive island (setup panel, resize grip), on everywhere else.
/// </summary>
internal static class NativeMethods
{
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_TRANSPARENT = 0x00000020;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    // MSDN's own SetWindowLong docs: extended-style bits like WS_EX_TRANSPARENT aren't
    // picked up by the window manager until a SetWindowPos with SWP_FRAMECHANGED forces
    // it to re-evaluate the frame - without this, the style change is silently ignored
    // on an already-visible window (confirmed empirically, twice now).
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X, Y;
    }
}
