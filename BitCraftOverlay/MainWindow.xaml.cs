using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;

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
    private readonly ObservableCollection<CalcEntry> _calcHistory;

    public MainWindow()
    {
        InitializeComponent();
        Browser.CreationProperties = new CoreWebView2CreationProperties { UserDataFolder = Settings.WebView2DataFolder };
        _calcHistory = new ObservableCollection<CalcEntry>(_settings.SavedCalculations);
        CalcHistoryList.ItemsSource = _calcHistory;
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

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
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

        // On a brand-new install, WebView2's first-ever startup (spinning up the
        // Edge runtime, creating the profile folder) can take a few seconds. Setting
        // Source before that finishes just queues one pending navigation - if the
        // user clicks a different tab in the meantime, that overwrites the queued
        // value and the very first tab's click is silently lost. Waiting here first
        // means every tab click after this point hits an already-ready WebView2.
        await Browser.EnsureCoreWebView2Async();

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

        if (tab == "Calc")
        {
            Browser.Visibility = Visibility.Collapsed;
            CalcPanel.Visibility = Visibility.Visible;
            return;
        }

        CalcPanel.Visibility = Visibility.Collapsed;
        Browser.Visibility = Visibility.Visible;
        Browser.Source = new Uri(_settings.LastTabUrls.TryGetValue(tab, out var savedUrl) && !string.IsNullOrWhiteSpace(savedUrl)
            ? savedUrl
            : DefaultUrlFor(tab));
    }

    // --- Calc: start/stop rate tool -------------------------------------------

    private void CalcStartNow_Click(object sender, RoutedEventArgs e) =>
        CalcStartTimeBox.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

    private void CalcStopNow_Click(object sender, RoutedEventArgs e) =>
        CalcStopTimeBox.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

    private void CalcField_Changed(object sender, TextChangedEventArgs e) =>
        CalcRateLabel.Text = TryParseCalcForm(out var entry) ? $"{entry.RateDisplay}" : "—";

    private bool TryParseCalcForm(out CalcEntry entry)
    {
        entry = new CalcEntry();
        if (!DateTime.TryParse(CalcStartTimeBox.Text, out var startTime)) return false;
        if (!DateTime.TryParse(CalcStopTimeBox.Text, out var stopTime)) return false;
        if (!double.TryParse(CalcStartValueBox.Text, out var startValue)) return false;
        if (!double.TryParse(CalcStopValueBox.Text, out var stopValue)) return false;

        entry.StartUnix = new DateTimeOffset(startTime).ToUnixTimeSeconds();
        entry.StopUnix = new DateTimeOffset(stopTime).ToUnixTimeSeconds();
        entry.StartValue = startValue;
        entry.StopValue = stopValue;
        return entry.StopUnix > entry.StartUnix;
    }

    private void CalcSave_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseCalcForm(out var entry))
        {
            MessageBox.Show("Fill in a valid start time, stop time, and both values first (stop must be after start).",
                "BitCraft Overlay", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        entry.Name = string.IsNullOrWhiteSpace(CalcNameBox.Text) ? $"Calc {_calcHistory.Count + 1}" : CalcNameBox.Text.Trim();

        var existing = _calcHistory.FirstOrDefault(c => string.Equals(c.Name, entry.Name, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            var result = MessageBox.Show($"A saved calculation named \"{entry.Name}\" already exists. Overwrite it?",
                "BitCraft Overlay", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;
            _calcHistory.Remove(existing);
        }

        _calcHistory.Insert(0, entry); // newest first
        _settings.SavedCalculations = _calcHistory.ToList();
        _settings.Save();
    }

    private void CalcDelete_Click(object sender, RoutedEventArgs e)
    {
        if (CalcHistoryList.SelectedItem is not CalcEntry entry) return;
        _calcHistory.Remove(entry);
        _settings.SavedCalculations = _calcHistory.ToList();
        _settings.Save();
    }

    private void CalcHistory_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CalcHistoryList.SelectedItem is not CalcEntry entry) return;

        CalcStartTimeBox.Text = DateTimeOffset.FromUnixTimeSeconds(entry.StartUnix).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
        CalcStopTimeBox.Text = DateTimeOffset.FromUnixTimeSeconds(entry.StopUnix).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
        CalcStartValueBox.Text = entry.StartValue.ToString();
        CalcStopValueBox.Text = entry.StopValue.ToString();
        CalcNameBox.Text = entry.Name;
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
                var firstVisible = new[] { "BitcraftSync", "Bitjita", "Brico", "Mapa", "Calc" }.FirstOrDefault(t => !_settings.HiddenTabs.Contains(t));
                if (firstVisible != null) ShowTab(firstVisible);
            }
            else if (_settings.LastTab == "BitcraftSync")
            {
                ShowTab("BitcraftSync");
            }
        }
    }
}
