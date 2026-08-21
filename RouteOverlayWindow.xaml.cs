using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace BitCraftOverlay;

/// <summary>Route tab's map + setup panel, in its own window (same pattern as
/// HeaderWindow: a separate hwnd docked over MainWindow). All the actual route state,
/// live tracking, and rendering logic stays in MainWindow - this is just its floating
/// visual, plus the click-through/opacity native window handling that MainWindow
/// itself can't safely carry (it hosts WebView2, which is incompatible with
/// AllowsTransparency="True" - confirmed empirically as a blank white window).</summary>
public partial class RouteOverlayWindow : Window
{
    private readonly MainWindow _content;
    private nint _hwnd;
    private bool _clickThroughActive;
    private bool _transparentNow;
    private DispatcherTimer? _clickThroughPoller;

    public RouteOverlayWindow(MainWindow content)
    {
        InitializeComponent();
        _content = content;
        SourceInitialized += (_, _) => _hwnd = new WindowInteropHelper(this).Handle;
    }

    private static Rect Bounds(FrameworkElement element) => new(element.PointToScreen(new Point(0, 0)), element.RenderSize);

    /// <summary>Selective click-through: WS_EX_TRANSPARENT makes Windows skip this window
    /// entirely during hit-testing - confirmed empirically that once set, a WM_NCHITTEST
    /// hook can't "give back" clickability for any region (the message simply never
    /// reaches the window while the ex-style is on). So instead of a static style +
    /// hit-test override, this polls the cursor position every 50ms and toggles the
    /// ex-style live: off (normal, clickable) while the cursor is over the setup panel
    /// or resize grip, on (click-through) everywhere else. 50ms is imperceptible for
    /// mouse-then-click timing but keeps this off the UI thread's hot path.</summary>
    private void ClickThroughPoller_Tick(object? sender, EventArgs e)
    {
        if (!NativeMethods.GetCursorPos(out var cursor)) return;
        var point = new Point(cursor.X, cursor.Y);
        var overIsland = (RouteSetupPanel.Visibility == Visibility.Visible && Bounds(RouteSetupPanel).Contains(point))
            || Bounds(RouteResizeGrip).Contains(point);
        SetTransparent(!overIsland);
    }

    private void SetTransparent(bool transparent)
    {
        if (transparent == _transparentNow || _hwnd == 0) return;
        _transparentNow = transparent;

        var exStyle = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
        exStyle = transparent ? exStyle | NativeMethods.WS_EX_TRANSPARENT : exStyle & ~NativeMethods.WS_EX_TRANSPARENT;
        NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE, exStyle);
        // Without this, the ex-style change above is silently ignored on an already-shown window.
        NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
            NativeMethods.SWP_FRAMECHANGED | NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>Applies Settings.RouteClickThrough/RouteOpacity - called whenever the
    /// Route tab is shown/hidden or either setting changes. <paramref name="showing"/> is
    /// whether the Route tab is the one currently visible; when it's not, the overlay
    /// state is always off regardless of the settings.
    ///
    /// Opacity is just WPF's own Window.Opacity property (this window already has
    /// AllowsTransparency="True", the prerequisite) rather than the native
    /// SetLayeredWindowAttributes call MainWindow's first attempt used - AllowsTransparency
    /// windows are internally pushed to the screen via UpdateLayeredWindow on every WPF
    /// repaint, which overwrites whatever a one-time SetLayeredWindowAttributes call sets,
    /// so the native call is a no-op here (confirmed empirically: style bit stuck, but no
    /// visible dimming).
    ///
    /// Set via a zero-duration BeginAnimation, not a plain assignment - confirmed
    /// empirically that a direct `Opacity = x` on an AllowsTransparency window updates
    /// the CLR property but doesn't reliably push a new composited (UpdateLayeredWindow)
    /// frame to the actual screen; BeginAnimation forces WPF's compositor to render a
    /// fresh frame regardless.</summary>
    internal void ApplyOverlayState(bool showing)
    {
        var settings = _content.RouteSettings;
        _clickThroughActive = showing && settings.RouteClickThrough;
        var target = showing ? Math.Clamp(settings.RouteOpacity, 0.0, 1.0) : 1.0;
        BeginAnimation(OpacityProperty, new DoubleAnimation(target, TimeSpan.Zero));

        if (_clickThroughActive)
        {
            _clickThroughPoller ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _clickThroughPoller.Tick -= ClickThroughPoller_Tick; // avoid double-subscribing on repeat calls
            _clickThroughPoller.Tick += ClickThroughPoller_Tick;
            _clickThroughPoller.Start();
        }
        else
        {
            _clickThroughPoller?.Stop();
            SetTransparent(false); // always leave the window normal/clickable when click-through is off
        }
    }

    // Forwarding handlers - the actual logic (route state, live tracking, rendering)
    // stays in MainWindow; this window is just its floating visual, same relationship
    // as HeaderWindow.Tab_Click forwarding into MainWindow.ShowTab.
    private void RouteToggleSetup_Click(object sender, RoutedEventArgs e) => _content.RouteToggleSetup_Click(sender, e);
    private void RouteFindPlayer_Click(object sender, RoutedEventArgs e) => _content.RouteFindPlayer_Click(sender, e);
    private void RoutePlayerResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e) => _content.RoutePlayerResultsList_SelectionChanged(sender, e);
    private void RouteResourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => _content.RouteResourceCombo_SelectionChanged(sender, e);
    private void RouteAllowWaterCheck_Changed(object sender, RoutedEventArgs e) => _content.RouteAllowWaterCheck_Changed(sender, e);
    private void RouteUseInGameMapCheck_Changed(object sender, RoutedEventArgs e) => _content.RouteUseInGameMapCheck_Changed(sender, e);
    private void RouteShowExtraNodesCheck_Changed(object sender, RoutedEventArgs e) => _content.RouteShowExtraNodesCheck_Changed(sender, e);
    private void RouteZoomOut_Click(object sender, RoutedEventArgs e) => _content.RouteZoomOut_Click(sender, e);
    private void RouteZoomIn_Click(object sender, RoutedEventArgs e) => _content.RouteZoomIn_Click(sender, e);

    // Resize grip: drags MainWindow's own Width/Height directly (the single source of
    // truth persisted to Settings) - this window's own Width/Height then just follow
    // along via MainWindow's existing SizeChanged hook. Local drag state, not shared
    // with MainWindow's identical grip (that one is unreachable while this window is
    // showing, since MainWindow itself is hidden - see MainWindow.ShowTab).
    private bool _resizing;
    private Point _resizeStartMouse;
    private double _resizeStartWidth, _resizeStartHeight;

    private void ResizeGrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _resizing = true;
        _resizeStartMouse = PointToScreen(e.GetPosition(this));
        _resizeStartWidth = _content.Width;
        _resizeStartHeight = _content.Height;
        ((UIElement)sender).CaptureMouse();
    }

    private void ResizeGrip_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_resizing) return;
        var current = PointToScreen(e.GetPosition(this));
        _content.Width = Math.Max(260, _resizeStartWidth + (current.X - _resizeStartMouse.X));
        _content.Height = Math.Max(200, _resizeStartHeight + (current.Y - _resizeStartMouse.Y));
    }

    private void ResizeGrip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _resizing = false;
        ((UIElement)sender).ReleaseMouseCapture();
    }
}
