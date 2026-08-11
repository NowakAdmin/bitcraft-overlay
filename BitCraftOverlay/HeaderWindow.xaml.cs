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
    private Point _dragStartMouse;
    private Point _dragStartWindow;

    public HeaderWindow(MainWindow content)
    {
        InitializeComponent();
        _content = content;
    }

    // Manual drag, not DragMove(): DragMove() triggers the OS's real window-move
    // (SC_MOVE), which refuses to drag a window's top edge above screen Y=0. Setting
    // Left/Top directly has no such clamp, so the overlay can go anywhere.
    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        _dragStartMouse = PointToScreen(e.GetPosition(this));
        _dragStartWindow = new Point(Left, Top);
        ((UIElement)sender).CaptureMouse();
    }

    private void Header_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var current = PointToScreen(e.GetPosition(this));
        Left = _dragStartWindow.X + (current.X - _dragStartMouse.X);
        Top = _dragStartWindow.Y + (current.Y - _dragStartMouse.Y);
    }

    private void Header_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        ((UIElement)sender).ReleaseMouseCapture();
    }

    private void Tab_Click(object sender, RoutedEventArgs e) => _content.ShowTab(((FrameworkElement)sender).Name.Replace("Tab", ""));

    internal void ApplyHiddenTabs(List<string> hidden)
    {
        TabBitcraftSync.Visibility = hidden.Contains("BitcraftSync") ? Visibility.Collapsed : Visibility.Visible;
        TabBitjita.Visibility = hidden.Contains("Bitjita") ? Visibility.Collapsed : Visibility.Visible;
        TabBrico.Visibility = hidden.Contains("Brico") ? Visibility.Collapsed : Visibility.Visible;
        TabMapa.Visibility = hidden.Contains("Mapa") ? Visibility.Collapsed : Visibility.Visible;
        TabCalc.Visibility = hidden.Contains("Calc") ? Visibility.Collapsed : Visibility.Visible;
        TabStats.Visibility = hidden.Contains("Stats") ? Visibility.Collapsed : Visibility.Visible;
        TwitchButton.Visibility = hidden.Contains("Twitch") ? Visibility.Collapsed : Visibility.Visible;
    }

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

    private void Minimize_Click(object sender, RoutedEventArgs e) => _content.ToggleMinimized();

    private void ResetSize_Click(object sender, RoutedEventArgs e) => _content.ResetSize();

    private void Settings_Click(object sender, RoutedEventArgs e) => _content.OpenSettings(this);

    private void Close_Click(object sender, RoutedEventArgs e) => _content.Close();
}
