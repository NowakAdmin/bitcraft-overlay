using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace BitCraftOverlay;

/// <summary>Thin, always-opaque toolbar. All the actual logic lives on the content window - this just forwards clicks.</summary>
public partial class HeaderWindow : Window
{
    private static readonly Dictionary<string, string> TabLabels = new()
    {
        ["BitcraftSync"] = "BitcraftSync",
        ["Bitjita"] = "Bitjita",
        ["Brico"] = "Brico",
        ["Mapa"] = "Map",
    };

    private static readonly Dictionary<string, string> TabIconPaths = new()
    {
        ["BitcraftSync"] = "pack://application:,,,/Assets/icons/bitcraftsync.ico",
        ["Bitjita"] = "pack://application:,,,/Assets/icons/bitjita.ico",
        ["Brico"] = "pack://application:,,,/Assets/icons/brico.ico",
        ["Mapa"] = "pack://application:,,,/Assets/icons/bitcraftmap.png",
    };

    private readonly MainWindow _content;
    private TwitchWindow? _twitchWindow;
    private bool _dragging;
    private bool _dragCandidate;
    private Point _dragStartMouse;
    private Point _dragStartWindow;
    private const double DragThreshold = 4; // pixels of movement before a press-on-a-button counts as a drag, not a click

    public HeaderWindow(MainWindow content)
    {
        InitializeComponent();
        _content = content;
    }

    // Manual drag, not DragMove(): DragMove() triggers the OS's real window-move
    // (SC_MOVE), which refuses to drag a window's top edge above screen Y=0. Setting
    // Left/Top directly has no such clamp, so the overlay can go anywhere.
    //
    // Hooked as Preview* (tunneling) on the outer Grid, not MouseLeftButtonDown/Up (direct
    // routing - never reaches an ancestor at all, so a header with no empty space left
    // between tab buttons had nowhere left to grab). Tunneling reaches here regardless of
    // which button is directly under the cursor. A press doesn't start the drag immediately
    // though - only once the mouse actually moves past DragThreshold - so a plain click on a
    // button still reaches it and fires normally; only pressing THEN dragging takes over the
    // window move instead.
    private void Header_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragCandidate = true;
        _dragStartMouse = PointToScreen(e.GetPosition(this));
        _dragStartWindow = new Point(Left, Top);
    }

    private void Header_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragCandidate || e.LeftButton != MouseButtonState.Pressed) return;
        var current = PointToScreen(e.GetPosition(this));
        if (!_dragging)
        {
            if (Math.Abs(current.X - _dragStartMouse.X) < DragThreshold && Math.Abs(current.Y - _dragStartMouse.Y) < DragThreshold)
                return;
            _dragging = true;
            ((UIElement)sender).CaptureMouse();
        }
        Left = _dragStartWindow.X + (current.X - _dragStartMouse.X);
        Top = _dragStartWindow.Y + (current.Y - _dragStartMouse.Y);
        e.Handled = true; // once actually dragging, don't let the button underneath react too
    }

    private void Header_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging)
        {
            ((UIElement)sender).ReleaseMouseCapture();
            e.Handled = true; // swallow the up too, so the button doesn't fire Click after a real drag
        }
        _dragging = false;
        _dragCandidate = false;
    }

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        _content.EnsureExpanded();
        _content.ShowTab(((FrameworkElement)sender).Name.Replace("Tab", ""));
    }

    internal void ApplyHiddenTabs(List<string> hidden)
    {
        TabBitcraftSync.Visibility = hidden.Contains("BitcraftSync") ? Visibility.Collapsed : Visibility.Visible;
        TabBitjita.Visibility = hidden.Contains("Bitjita") ? Visibility.Collapsed : Visibility.Visible;
        TabBrico.Visibility = hidden.Contains("Brico") ? Visibility.Collapsed : Visibility.Visible;
        TabMapa.Visibility = hidden.Contains("Mapa") ? Visibility.Collapsed : Visibility.Visible;
        TabCalc.Visibility = hidden.Contains("Calc") ? Visibility.Collapsed : Visibility.Visible;
        TabStats.Visibility = hidden.Contains("Stats") ? Visibility.Collapsed : Visibility.Visible;
        TabClaim.Visibility = hidden.Contains("Claim") ? Visibility.Collapsed : Visibility.Visible;
        TabRoute.Visibility = hidden.Contains("Route") ? Visibility.Collapsed : Visibility.Visible;
        TwitchButton.Visibility = hidden.Contains("Twitch") ? Visibility.Collapsed : Visibility.Visible;
        // TabCustom is NOT set here - its visibility also depends on whether a URL is actually
        // configured (Settings.CustomTabUrl), which this method doesn't know about. See
        // SetCustomTabVisible, called separately by MainWindow with that combined condition.
    }

    /// <summary>Combines the "Custom" HiddenTabs toggle with whether a URL is actually
    /// configured - see MainWindow.CustomTabShouldBeVisible.</summary>
    internal void SetCustomTabVisible(bool visible) => TabCustom.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    private const string TwitchTooltip = "Open twitch.tv/bitcraftonline in a separate window (watch the stream, collect drops)";
    private const string TwitchIconPath = "pack://application:,,,/Assets/icons/twitch.ico";

    internal void ApplyDisplayMode(bool useIcons)
    {
        SetTabContent(TabBitcraftSync, "BitcraftSync", useIcons);
        SetTabContent(TabBitjita, "Bitjita", useIcons);
        SetTabContent(TabBrico, "Brico", useIcons);
        SetTabContent(TabMapa, "Mapa", useIcons);

        // No favicon exists for our own native tools, so use an emoji glyph instead of an Image.
        TabCalc.Content = useIcons ? "🧮" : "Calc";
        TabCalc.Padding = useIcons ? new Thickness(7, 0, 7, 0) : new Thickness(8, 0, 8, 0);

        TabStats.Content = useIcons ? "📊" : "Stats";
        TabStats.Padding = useIcons ? new Thickness(7, 0, 7, 0) : new Thickness(8, 0, 8, 0);

        TabClaim.Content = useIcons ? "🏘" : "Claim";
        TabClaim.Padding = useIcons ? new Thickness(7, 0, 7, 0) : new Thickness(8, 0, 8, 0);

        TabRoute.Content = useIcons ? "🧭" : "Route";
        TabRoute.Padding = useIcons ? new Thickness(7, 0, 7, 0) : new Thickness(8, 0, 8, 0);

        // No favicon exists for a player-provided URL either - same emoji-glyph treatment as
        // the native tools above.
        TabCustom.Content = useIcons ? "🔗" : "Custom";
        TabCustom.Padding = useIcons ? new Thickness(7, 0, 7, 0) : new Thickness(8, 0, 8, 0);

        if (useIcons)
        {
            TwitchButton.Content = new Image { Source = new BitmapImage(new Uri(TwitchIconPath)), Width = 16, Height = 16 };
            TwitchButton.Padding = new Thickness(7, 0, 7, 0);
        }
        else
        {
            TwitchButton.Content = "Twitch";
            TwitchButton.Padding = new Thickness(8, 0, 8, 0);
        }
        TwitchButton.ToolTip = TwitchTooltip; // keep the explanatory tooltip in both modes
    }

    private static void SetTabContent(Button button, string tab, bool useIcons)
    {
        if (useIcons)
        {
            button.Content = new Image
            {
                Source = new BitmapImage(new Uri(TabIconPaths[tab])),
                Width = 16, Height = 16,
            };
            button.ToolTip = TabLabels[tab];
            button.Padding = new Thickness(7, 0, 7, 0);
        }
        else
        {
            button.Content = TabLabels[tab];
            button.ToolTip = null;
            button.Padding = new Thickness(8, 0, 8, 0);
        }
    }

    private void Twitch_Click(object sender, RoutedEventArgs e)
    {
        if (_twitchWindow is null)
        {
            _twitchWindow = new TwitchWindow();
            _twitchWindow.Closed += (_, _) => _twitchWindow = null; // closed via its own X - allow a fresh one next click
            _twitchWindow.Show();
        }
        else if (_twitchWindow.IsVisible)
        {
            _twitchWindow.Hide();
        }
        else
        {
            _twitchWindow.Show();
        }
    }

    private void ReloadTab_Click(object sender, RoutedEventArgs e) => _content.ReloadCurrentTabToDefault();

    private void Minimize_Click(object sender, RoutedEventArgs e) => _content.ToggleMinimized();

    private void ResetSize_Click(object sender, RoutedEventArgs e) => _content.ResetSize();

    private void Settings_Click(object sender, RoutedEventArgs e) => _content.OpenSettings(this);

    private void Close_Click(object sender, RoutedEventArgs e) => _content.Close();
}
