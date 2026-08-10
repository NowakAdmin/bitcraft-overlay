using System.Runtime.InteropServices;

namespace BitCraftOverlay;

/// <summary>
/// Win32 P/Invoke: WS_EX_TOOLWINDOW keeps the overlay out of Alt+Tab, and
/// GetWindowRect reads the game window's bounds to position the overlay on
/// top of it at startup.
/// </summary>
internal static class NativeMethods
{
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_LAYERED = 0x00080000;
    public const uint LWA_ALPHA = 0x2;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    // Constant whole-window alpha blend via DWM - unlike WPF's AllowsTransparency
    // (which breaks hosted HWND controls like WebView2), this composites the whole
    // window including its children, so the browser keeps rendering normally.
    [DllImport("user32.dll")]
    public static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint crKey, byte bAlpha, uint dwFlags);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }
}
