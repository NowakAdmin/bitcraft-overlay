using System.Windows;
using System.Windows.Input;

namespace BitCraftOverlay;

/// <summary>Thin, always-opaque toolbar. All the actual logic lives on the content window - this just forwards clicks.</summary>
public partial class HeaderWindow : Window
{
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
