using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace BitCraftOverlay;

public partial class MainWindow : Window
{
    private const string BitcraftSyncBase = "https://bitcraftsync.app";
    private const string BitjitaUrl = "https://bitjita.com/market";
    private const string BricoUrl = "https://brico.app";
    private const string MapUrl = "https://bitcraftmap.com/";

    private string _currentTab = "BitcraftSync";

    private readonly Settings _settings = Settings.Load();
    private bool _resizing;
    private Point _resizeStartMouse;
    private double _resizeStartWidth;
    private double _resizeStartHeight;
    private bool _minimized;
    private double _expandedHeight;
    private bool _dirty;
    private nint _hwnd;
    private HeaderWindow? _header;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += MainWindow_SourceInitialized;
        Loaded += MainWindow_Loaded;
        Closing += (_, _) => SaveWindowState();

        // Remember whatever URL each tab ends up at (map/market state is encoded
        // in the URL itself), so reopening the app lands back where you left off.
        Browser.SourceChanged += (_, _) =>
        {
            if (Browser.Source is { Scheme: "http" or "https" } uri)
            {
                _settings.LastTabUrls[_currentTab] = uri.AbsoluteUri;
                _dirty = true;
            }
        };

        // A clean Closing event doesn't always fire (crash, taskkill, log off), so
        // flush to disk periodically too - at most once per 5s, only if something changed.
        var saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        saveTimer.Tick += (_, _) =>
        {
            if (!_dirty) return;
            SaveWindowState();
            _dirty = false;
        };
        saveTimer.Start();
    }

    // --- Alt+Tab visibility -----------------------------------------------------

    // No more WS_EX_NOACTIVATE: it blocked all keyboard focus, so typing never
    // worked anywhere in the overlay (settings, search boxes on bitjita/brico...).
    // Testing showed BitCraft doesn't minimize when it loses focus, so the whole
    // reason for that lockout doesn't apply - the window now activates normally.
    // WS_EX_TOOLWINDOW hides it from Alt+Tab.
    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE, exStyle | NativeMethods.WS_EX_TOOLWINDOW);
    }

    // --- Startup positioning / restore --------------------------------------

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Width = _settings.WindowWidth;
        Height = _settings.WindowHeight;

        if (_settings.WindowLeft is double left && _settings.WindowTop is double top)
        {
            Left = left;
            Top = top;
        }
        else
        {
            PositionOverGameWindow();
        }

        ShowTab(_settings.LastTab);

        // The header is a separate, always-opaque window docked directly above this
        // one (see SetNoActivate notes history - transparency can only apply to a
        // whole native window, so the exempt toolbar has to live in its own window).
        _header = new HeaderWindow(this) { Owner = this };
        _header.Left = Left;
        _header.Width = Width;
        _header.Top = Top - _header.Height;
        _header.ApplyHiddenTabs(_settings.HiddenTabs);
        _header.ApplyDisplayMode(_settings.UseIconTabs);
        _header.Show();

        _header.LocationChanged += (_, _) =>
        {
            Left = _header.Left;
            Top = _header.Top + _header.Height;
        };
        SizeChanged += (_, _) => _header.Width = Width;
    }

    private void PositionOverGameWindow()
    {
        var game = Process.GetProcessesByName("BitCraft").FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero);
        if (game != null && NativeMethods.GetWindowRect(game.MainWindowHandle, out var rect))
        {
            // Dock near the top-right corner of the game window.
            Left = rect.Left + rect.Width - Width - 20;
            Top = rect.Top + 20;
            return;
        }

        // Fallback: center of the primary screen.
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = (SystemParameters.PrimaryScreenHeight - Height) / 2;
    }

    private void SaveWindowState()
    {
        _settings.WindowLeft = Left;
        _settings.WindowTop = Top;
        _settings.WindowWidth = Width;
        // Don't persist the collapsed height if closed while minimized.
        _settings.WindowHeight = _minimized ? _expandedHeight : Height;
        _settings.Save();
    }

    // --- Minimize: hide the content window, the header stays put ---------------

    internal void ToggleMinimized()
    {
        _minimized = !_minimized;
        if (_minimized)
        {
            _expandedHeight = Height;
            Hide();
        }
        else
        {
            Show();
            Height = _expandedHeight;
        }
    }

    // In case the resize grip got dragged somewhere silly (off-screen, tiny sliver).
    internal void ResetSize()
    {
        Width = 420;
        if (_minimized) _expandedHeight = 680;
        else Height = 680;
    }

    // --- Bottom-right resize grip: width + height together (manual capture, same --
    // pattern used before NOACTIVATE was dropped - kept because it's simple and works) --

    private void ResizeGrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _resizing = true;
        _resizeStartMouse = PointToScreen(e.GetPosition(this));
        _resizeStartWidth = Width;
        _resizeStartHeight = Height;
        ((UIElement)sender).CaptureMouse();
    }

    private void ResizeGrip_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_resizing) return;
        var current = PointToScreen(e.GetPosition(this));
        Width = Math.Max(260, _resizeStartWidth + (current.X - _resizeStartMouse.X));
        Height = Math.Max(200, _resizeStartHeight + (current.Y - _resizeStartMouse.Y));
    }

    private void ResizeGrip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _resizing = false;
        ((UIElement)sender).ReleaseMouseCapture();
    }

    // --- Tabs ----------------------------------------------------------------

    internal void ShowTab(string tab)
    {
        _currentTab = tab;
        _settings.LastTab = tab;
        Browser.Source = new Uri(_settings.LastTabUrls.TryGetValue(tab, out var savedUrl) && !string.IsNullOrWhiteSpace(savedUrl)
            ? savedUrl
            : DefaultUrlFor(tab));
    }

    private string DefaultUrlFor(string tab) => tab switch
    {
        "Bitjita" => BitjitaUrl,
        "Brico" => BricoUrl,
        "Mapa" => MapUrl,
        _ => string.IsNullOrWhiteSpace(_settings.BitcraftSyncShareCode)
            ? BitcraftSyncBase
            : $"{BitcraftSyncBase}/s/{_settings.BitcraftSyncShareCode}",
    };

    // --- Settings -----------------------------------------------------------

    internal void OpenSettings(Window owner)
    {
        var dialog = new SettingsWindow(_settings.BitcraftSyncShareCode, _settings.HiddenTabs, _settings.UseIconTabs) { Owner = owner };
        if (dialog.ShowDialog() == true)
        {
            _settings.BitcraftSyncShareCode = dialog.ShareCode;
            _settings.HiddenTabs = dialog.HiddenTabs;
            _settings.UseIconTabs = dialog.UseIconTabs;
            _settings.LastTabUrls.Remove("BitcraftSync");
            _dirty = true;
            _settings.Save();
            _header?.ApplyHiddenTabs(_settings.HiddenTabs);
            _header?.ApplyDisplayMode(_settings.UseIconTabs);

            if (_settings.HiddenTabs.Contains(_currentTab))
            {
                var firstVisible = new[] { "BitcraftSync", "Bitjita", "Brico", "Mapa" }.FirstOrDefault(t => !_settings.HiddenTabs.Contains(t));
                if (firstVisible != null) ShowTab(firstVisible);
            }
            else if (_settings.LastTab == "BitcraftSync")
            {
                ShowTab("BitcraftSync");
            }
        }
    }
}
