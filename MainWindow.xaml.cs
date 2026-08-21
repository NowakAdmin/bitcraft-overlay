using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;

namespace BitCraftOverlay;

public partial class MainWindow : Window
{
    // Matches RouteOverlayWindow.xaml's RouteRoot Background - restored when RouteUseInGameMap
    // is turned back off (that mode swaps it to Brushes.Transparent so the real minimap shows through).
    private static readonly Brush RouteRootNormalBackground = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));

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
    private RouteOverlayWindow? _routeOverlay;
    private readonly ObservableCollection<CalcEntry> _calcHistory;
    private readonly ObservableCollection<StatComparison> _statComparisons;
    private StatSnapshot? _statA;
    private StatSnapshot? _statB;
    private ClaimInfo? _claimInfo;

    // --- Route live-tracking state (see the Route section further down) ---
    // Two separate connections, not one: a bulk Subscribe REPLACES the whole query set on a
    // connection, and the relay appears to deliver a combined Subscribe's tables together, not
    // cheapest-first - confirmed empirically, player position (a single-row query) was arriving
    // ~10s late, in lockstep with a slow resource_state/location_state fetch it had no business
    // waiting on. _routePlayerLive carries only mobile_entity_state (subscribed once, never
    // resubscribed - the player's entity_id doesn't change), so it keeps streaming fast
    // TransactionUpdates regardless of how long a resource resubscribe on _routeLive takes.
    private SpacetimeLiveConnection? _routePlayerLive;
    private SpacetimeLiveConnection? _routeLive;
    private string? _routePlayerEntityId;
    private RouteNode? _routePlayerPos; // live, world coords - null until the first position arrives
    private readonly Dictionary<string, (double X, double Z)> _routeLocationRows = new();
    private readonly HashSet<string> _routeResourceEntityIds = new();
    private ResourceType? _routeActiveResource; // set on the UI thread, read from OnRouteRowsChanged's background thread
    private (double X, double Z)? _routeBoxCenter;
    private (double X, double Z)? _routeLastRenderedPlayerPos;
    private (double X, double Z)? _routePendingRecenter;
    private DateTime _routeLastResubscribe = DateTime.MinValue;
    private bool _routeDirty;
    private DispatcherTimer? _routeRecomputeTimer;

    public MainWindow()
    {
        InitializeComponent();
        Browser.CreationProperties = Settings.CreateWebViewCreationProperties();
        // Route map render targets the panel's actual size (see RecomputeRoute) - a resize needs
        // a fresh render at the new size, not just a stretched-to-fit old one.
        RoutePanel.SizeChanged += (_, _) => _routeDirty = true;
        _calcHistory = new ObservableCollection<CalcEntry>(_settings.SavedCalculations);
        CalcHistoryList.ItemsSource = _calcHistory;
        _statComparisons = new ObservableCollection<StatComparison>(_settings.SavedComparisons);
        StatsComparisonHistoryList.ItemsSource = _statComparisons;
        StatsPlayerNameBox.Text = _settings.StatsPlayerName;
        ClaimNameBox.Text = _settings.ClaimName;
        _claimInfo = _settings.SavedClaimData;
        ShowClaimInfo(); // show whatever was saved from the last Find, if any - no need to re-search on every launch
        SourceInitialized += MainWindow_SourceInitialized;
        Loaded += MainWindow_Loaded;
        Closing += (_, _) => SaveWindowState();
        Closing += (_, _) =>
        {
            if (_routePlayerLive is not null) _ = _routePlayerLive.DisposeAsync();
            if (_routeLive is not null) _ = _routeLive.DisposeAsync();
        };

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
    /// <summary>MainWindow.Settings accessor for RouteOverlayWindow - it needs
    /// RouteClickThrough/RouteOpacity but shouldn't own a second Settings instance.</summary>
    internal Settings RouteSettings => _settings;

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

        // Route's map + setup panel window - see RouteOverlayWindow's own summary for
        // why it's separate from MainWindow. Created and positioned before ShowTab
        // below, since ShowTab shows/hides it and applies its overlay state.
        _routeOverlay = new RouteOverlayWindow(this) { Owner = this };
        _routeOverlay.Left = Left;
        _routeOverlay.Top = Top;
        _routeOverlay.Width = Math.Max(Width - 14, 50); // minus the resize-grip strip's own column
        _routeOverlay.Height = Height;
        _routeOverlay.RoutePlayerNameBox.Text = _settings.RoutePlayerName;
        _routeOverlay.RouteAllowWaterCheck.IsChecked = _settings.RouteAllowWater;
        _routeOverlay.RouteZoomLabel.Text = $"{_settings.RouteZoom * 100:F0}%";
        _routeOverlay.RouteUseInGameMapCheck.IsChecked = _settings.RouteUseInGameMap;
        _routeOverlay.RouteShowExtraNodesCheck.IsChecked = _settings.RouteShowExtraNodes;
        _routeOverlay.RouteRoot.Background = _settings.RouteUseInGameMap ? Brushes.Transparent : RouteRootNormalBackground;
        _routeOverlay.Show();

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
        _header.SetCustomTabVisible(CustomTabShouldBeVisible);
        _header.Show();

        _header.LocationChanged += (_, _) =>
        {
            Left = _header.Left;
            Top = _header.Top + _header.Height;
            _routeOverlay.Left = Left;
            _routeOverlay.Top = Top;
        };
        SizeChanged += (_, _) =>
        {
            _header.Width = Width;
            _routeOverlay.Width = Math.Max(Width - 14, 50);
            _routeOverlay.Height = Height;
        };
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

    // Clicking a tab while minimized should show that tab, not silently switch it in the
    // background - un-minimize first so the click's effect is actually visible.
    internal void EnsureExpanded()
    {
        if (!_minimized) return;
        _minimized = false;
        Show();
        Height = _expandedHeight;
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

        CalcPanel.Visibility = tab == "Calc" ? Visibility.Visible : Visibility.Collapsed;
        StatsPanel.Visibility = tab == "Stats" ? Visibility.Visible : Visibility.Collapsed;
        ClaimPanel.Visibility = tab == "Claim" ? Visibility.Visible : Visibility.Collapsed;
        RoutePanel.Visibility = tab == "Route" ? Visibility.Visible : Visibility.Collapsed;
        Browser.Visibility = tab is "Calc" or "Stats" or "Claim" or "Route" ? Visibility.Collapsed : Visibility.Visible;
        _routeOverlay!.Visibility = tab == "Route" ? Visibility.Visible : Visibility.Collapsed;
        _routeOverlay.ApplyOverlayState(tab == "Route");

        // MainWindow itself must be HIDDEN (not just its RoutePanel content) while on the
        // Route tab - RouteOverlayWindow floats in the exact same screen area, and if
        // MainWindow is merely showing an empty placeholder there, that placeholder (an
        // ordinary opaque window) is what Route's click-through/opacity would reveal -
        // the game/desktop further behind never being reached. Confirmed empirically:
        // the "see-through" effect was blending against this window's own dark
        // background, not the desktop. HeaderWindow and RouteOverlayWindow are separate
        // owned windows, so hiding the owner (this) doesn't hide them.
        Visibility = tab == "Route" ? Visibility.Hidden : Visibility.Visible;
        if (tab is "Calc" or "Stats" or "Claim" or "Route")
        {
            if (tab == "Route") _ = EnsureRouteResourceListLoaded(); // lazy-load the resource catalog the first time the tab is shown
            return;
        }

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

    // --- Stats: bitjita.com player snapshots ----------------------------------

    // Disables the clicked button and swaps its text while an API call is in
    // flight, so impatient clicking can't fire off a pile of duplicate requests.
    private static async Task RunBusy(Button button, string busyText, Func<Task> action)
    {
        button.IsEnabled = false;
        var original = button.Content;
        button.Content = busyText;
        try
        {
            await action();
        }
        finally
        {
            button.IsEnabled = true;
            button.Content = original;
        }
    }

    private async void StatsFindPlayer_Click(object sender, RoutedEventArgs e)
    {
        var name = StatsPlayerNameBox.Text.Trim();
        if (name.Length < 2)
        {
            StatsPlayerFoundLabel.Text = "Type at least 2 characters.";
            return;
        }
        StatsPlayerFoundLabel.Text = "Searching...";
        await RunBusy((Button)sender, "...", async () =>
        {
            try
            {
                var found = await BitjitaApi.FindPlayerAsync(name);
                if (found is null)
                {
                    StatsPlayerFoundLabel.Text = "No player found.";
                    return;
                }
                _settings.StatsPlayerName = name;
                _settings.StatsPlayerEntityId = found.Value.EntityId;
                _settings.Save();
                StatsPlayerFoundLabel.Text = $"Found: {found.Value.Username}";
            }
            catch
            {
                StatsPlayerFoundLabel.Text = "Search failed (no internet?).";
            }
        });
    }

    private async void StatsTakeA_Click(object sender, RoutedEventArgs e) => await StatsTakeSnapshot(isA: true, (Button)sender);
    private async void StatsTakeB_Click(object sender, RoutedEventArgs e) => await StatsTakeSnapshot(isA: false, (Button)sender);

    private async Task StatsTakeSnapshot(bool isA, Button button)
    {
        if (string.IsNullOrWhiteSpace(_settings.StatsPlayerEntityId))
        {
            MessageBox.Show("Find your player first.", "BitCraft Overlay", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        await RunBusy(button, "Loading...", async () =>
        {
            try
            {
                var snap = await BitjitaApi.TakeSnapshotAsync(_settings.StatsPlayerEntityId);
                if (isA) _statA = snap; else _statB = snap;
                UpdateStatsAbLabel();
                UpdateStatsDiff();
            }
            catch
            {
                MessageBox.Show("Couldn't fetch stats (no internet, or the saved player is stale - try Find again).",
                    "BitCraft Overlay", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        });
    }

    private void UpdateStatsAbLabel()
    {
        static string Fmt(StatSnapshot? s) =>
            s is null ? "not taken" : DateTimeOffset.FromUnixTimeSeconds(s.TimestampUnix).LocalDateTime.ToString("HH:mm:ss");
        StatsAbLabel.Text = $"A: {Fmt(_statA)}   B: {Fmt(_statB)}";
    }

    private void UpdateStatsDiff()
    {
        if (_statA is null || _statB is null) return;
        StatsDiffList.ItemsSource = BuildDiffLines(_statA, _statB);
    }

    private static List<string> BuildDiffLines(StatSnapshot a, StatSnapshot b)
    {
        var lines = new List<string>();
        var elapsedSeconds = b.TimestampUnix - a.TimestampUnix;

        foreach (var key in a.SkillXp.Keys.Union(b.SkillXp.Keys))
        {
            var da = a.SkillXp.GetValueOrDefault(key);
            var db = b.SkillXp.GetValueOrDefault(key);
            var delta = db - da;
            if (delta == 0) continue;
            var perHour = elapsedSeconds > 0 ? delta / (double)elapsedSeconds * 3600.0 : 0;
            lines.Add($"{key}: {delta:+#,0;-#,0} xp ({da:#,0} → {db:#,0}) — {perHour:0.#}/h");
        }
        foreach (var key in a.Items.Keys.Union(b.Items.Keys))
        {
            var da = a.Items.GetValueOrDefault(key);
            var db = b.Items.GetValueOrDefault(key);
            if (db != da) lines.Add($"{key}: {db - da:+#,0;-#,0}");
        }
        if (b.PlaceableCount != a.PlaceableCount)
            lines.Add($"Placeables: {b.PlaceableCount - a.PlaceableCount:+#,0;-#,0} ({a.PlaceableCount} → {b.PlaceableCount})");

        if (!a.EquippedTools.SequenceEqual(b.EquippedTools))
        {
            var from = a.EquippedTools.Count > 0 ? string.Join(", ", a.EquippedTools) : "none";
            var to = b.EquippedTools.Count > 0 ? string.Join(", ", b.EquippedTools) : "none";
            lines.Add($"Equipped: {from} → {to}");
        }

        if (lines.Count == 0) lines.Add("No changes between A and B.");
        return lines;
    }

    private void StatsSaveComparison_Click(object sender, RoutedEventArgs e)
    {
        if (_statA is null || _statB is null)
        {
            MessageBox.Show("Take both snapshot A and B first.", "BitCraft Overlay", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var name = string.IsNullOrWhiteSpace(StatsCompareNameBox.Text) ? $"Compare {_statComparisons.Count + 1}" : StatsCompareNameBox.Text.Trim();

        var existing = _statComparisons.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            var result = MessageBox.Show($"A saved comparison named \"{name}\" already exists. Overwrite it?",
                "BitCraft Overlay", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;
            _statComparisons.Remove(existing);
        }

        _statComparisons.Insert(0, new StatComparison { Name = name, A = _statA, B = _statB });
        _settings.SavedComparisons = _statComparisons.ToList();
        _settings.Save();
    }

    private void StatsLoadComparison_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not StatComparison c) return;
        _statA = c.A;
        _statB = c.B;
        StatsCompareNameBox.Text = c.Name;
        UpdateStatsAbLabel();
        UpdateStatsDiff();
    }

    // --- Claim: settlement member/skill/armor lookup ------------------------

    // Explicit per user: Epic=gold, Rare=blue, Legendary=light blue, Mythic=purple, Uncommon=brown.
    // Common wasn't specified - filled in with a neutral light gray.
    private static readonly Dictionary<string, Brush> RarityColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Common"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CFCFCF")),
        ["Uncommon"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#A0723C")),
        ["Rare"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4A90E2")),
        ["Epic"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFD700")),
        ["Legendary"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#66D9E8")),
        ["Mythic"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#B14AED")),
    };

    private static Brush RarityBrush(string rarity) => RarityColors.TryGetValue(rarity, out var b) ? b : Brushes.White;

    // Current sort state per grid - a column's first click sorts descending (highest/Z-first,
    // per user request), a second click on the same column flips it. Each grid gets its own
    // key dictionary so sorting one never touches the other's header text (both have a "Name"
    // column, which would otherwise collide in a single shared dictionary).
    private string? _claimSortKey;
    private bool _claimSortAscending;
    private readonly Dictionary<DataGridColumn, string> _claimMemberColumnKeys = new();

    private string? _armorSortKey;
    private bool _armorSortAscending;
    private readonly Dictionary<DataGridColumn, string> _claimArmorColumnKeys = new();
    private List<ArmorRow> _claimArmorRows = new();

    private string? _toolsSortKey;
    private bool _toolsSortAscending;
    private readonly Dictionary<DataGridColumn, string> _claimToolsColumnKeys = new();
    private List<ToolsRow> _claimToolsRows = new();

    private async void ClaimFind_Click(object sender, RoutedEventArgs e)
    {
        var name = ClaimNameBox.Text.Trim();
        if (name.Length < 2)
        {
            ClaimFoundLabel.Text = "Type at least 2 characters.";
            return;
        }
        ClaimFoundLabel.Text = "Searching...";
        await RunBusy((Button)sender, "Loading...", async () =>
        {
            try
            {
                var found = await ClaimApi.FindClaimAsync(name);
                if (found is null)
                {
                    ClaimFoundLabel.Text = "No claim found.";
                    return;
                }
                _settings.ClaimName = name;
                _claimInfo = await ClaimApi.LoadClaimAsync(found.Value.EntityId, found.Value.Name);
                _settings.SavedClaimData = _claimInfo; // kept until the next Find, so it's there on next launch too
                _settings.Save();

                ShowClaimInfo();
            }
            catch
            {
                ClaimFoundLabel.Text = "Search failed (no internet?).";
            }
        });
    }

    private void ShowClaimInfo()
    {
        if (_claimInfo is null) return;
        ClaimFoundLabel.Text = $"{_claimInfo.Name} - {_claimInfo.Members.Count} members";
        BuildClaimMembersGrid();
        BuildClaimArmorGrid();
        BuildClaimToolsGrid();
    }

    // Columns are built at runtime (Name + one per skill the API reported, + Last seen)
    // rather than declared in XAML, since the skill set comes from the claim's own response.
    private void BuildClaimMembersGrid()
    {
        ClaimMembersGrid.Columns.Clear();
        _claimMemberColumnKeys.Clear();
        _claimSortKey = null;
        if (_claimInfo is null) return;

        AddClaimColumn(ClaimMembersGrid, _claimMemberColumnKeys, "Name", nameof(ClaimMemberInfo.UserName), new DataGridLength(90));

        var tierConverter = new LevelTierBrushConverter();
        foreach (var skill in _claimInfo.SkillNames)
        {
            var column = new DataGridTemplateColumn
            {
                Header = skill,
                HeaderTemplate = BuildSortableHeaderTemplate(),
                Width = new DataGridLength(36),
                CellTemplate = BuildSkillCellTemplate(skill, tierConverter),
            };
            ClaimMembersGrid.Columns.Add(column);
            _claimMemberColumnKeys[column] = skill;
        }

        AddClaimColumn(ClaimMembersGrid, _claimMemberColumnKeys, "Last seen", nameof(ClaimMemberInfo.LastSeenDisplay), new DataGridLength(1, DataGridLengthUnitType.Star));

        ClaimMembersGrid.ItemsSource = _claimInfo.Members;
    }

    private static void AddClaimColumn(DataGrid grid, Dictionary<DataGridColumn, string> columnKeys, string key, string bindingPath, DataGridLength width)
    {
        var column = new DataGridTextColumn
        {
            Header = key,
            HeaderTemplate = BuildSortableHeaderTemplate(),
            Binding = new Binding(bindingPath),
            Width = width,
        };
        grid.Columns.Add(column);
        columnKeys[column] = key;
    }

    // Plain text (not the DataGrid's built-in sort-on-header-click, which turned out not to
    // reliably fire for narrow template columns) - actual click handling is done at the grid
    // level in ClaimMembersGrid_HeaderClicked, which doesn't depend on hitting this exact element.
    private static DataTemplate BuildSortableHeaderTemplate()
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding()); // binds to the column's Header value itself
        text.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Center);
        text.SetValue(FrameworkElement.CursorProperty, Cursors.Hand);
        text.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        return new DataTemplate { VisualTree = text };
    }

    // Handles clicks anywhere in a column header (not just the inner TextBlock) by walking up
    // the visual tree from whatever was actually clicked to the enclosing DataGridColumnHeader -
    // avoids relying on a specific child element's own hit-test area, which for narrow (36px)
    // skill columns wasn't reliably catching clicks even after other fixes.
    private void ClaimMembersGrid_HeaderClicked(object sender, MouseButtonEventArgs e)
    {
        if (_claimInfo is null) return;
        var header = FindAncestor<DataGridColumnHeader>(e.OriginalSource as DependencyObject);
        if (header?.Column is not { } column || !_claimMemberColumnKeys.TryGetValue(column, out var key)) return;

        _claimSortAscending = _claimSortKey == key && !_claimSortAscending; // first click on a column = descending
        _claimSortKey = key;

        IEnumerable<ClaimMemberInfo> sorted = key switch
        {
            "Name" => Order(_claimInfo.Members, m => m.UserName, _claimSortAscending),
            // A blank last-seen (the API just doesn't have it for some members) isn't
            // meaningfully "earliest" - always push those to the end, in both directions,
            // rather than letting them sort to the top on an ascending click.
            "Last seen" => _claimSortAscending
                ? _claimInfo.Members.OrderBy(m => string.IsNullOrEmpty(m.LastLoginRaw)).ThenBy(m => m.LastLoginRaw)
                : _claimInfo.Members.OrderBy(m => string.IsNullOrEmpty(m.LastLoginRaw)).ThenByDescending(m => m.LastLoginRaw),
            _ => Order(_claimInfo.Members, m => m.SkillLevels.GetValueOrDefault(key), _claimSortAscending),
        };
        ClaimMembersGrid.ItemsSource = sorted.ToList();

        var arrow = _claimSortAscending ? " ▲" : " ▼";
        foreach (var (col, colKey) in _claimMemberColumnKeys)
            col.Header = colKey + (colKey == key ? arrow : "");
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node != null)
        {
            if (node is T match) return match;
            node = VisualTreeHelper.GetParent(node);
        }
        return null;
    }

    private static IEnumerable<T> Order<T, TKey>(IEnumerable<T> items, Func<T, TKey> key, bool ascending) =>
        ascending ? items.OrderBy(key) : items.OrderByDescending(key);

    // Cell background = the level's tier color, via LevelTierBrushConverter. Built with
    // FrameworkElementFactory since the binding path (skill name) is only known at runtime.
    private static DataTemplate BuildSkillCellTemplate(string skillName, IValueConverter tierConverter)
    {
        var cell = new FrameworkElementFactory(typeof(Border));
        cell.SetBinding(Border.BackgroundProperty, new Binding($"SkillLevels[{skillName}]") { Converter = tierConverter });
        cell.SetValue(Border.PaddingProperty, new Thickness(2, 1, 2, 1));

        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding($"SkillLevels[{skillName}]"));
        text.SetValue(TextBlock.ForegroundProperty, Brushes.White);
        text.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cell.AppendChild(text);

        return new DataTemplate { VisualTree = cell };
    }

    // Every member, one row per saved armor preset (falls back to a synthetic "preset 1"
    // from their current gear if they never saved one - see ClaimApi.LoadClaimAsync).
    private class ArmorRow
    {
        public string UserName { get; set; } = "";
        public string PresetLabel { get; set; } = "";
        public Dictionary<string, ArmorCell> Slots { get; set; } = new();
    }

    private class ArmorCell
    {
        public string Text { get; set; } = "";
        public Brush Color { get; set; } = Brushes.White;
        public int Tier { get; set; }
    }

    private void BuildClaimArmorGrid()
    {
        ClaimArmorGrid.Columns.Clear();
        _claimArmorColumnKeys.Clear();
        _armorSortKey = null;
        if (_claimInfo is null) return;

        AddClaimColumn(ClaimArmorGrid, _claimArmorColumnKeys, "Name", nameof(ArmorRow.UserName), new DataGridLength(80));
        AddClaimColumn(ClaimArmorGrid, _claimArmorColumnKeys, "Preset", nameof(ArmorRow.PresetLabel), new DataGridLength(60));

        var tierConverter = new ItemTierBrushConverter();
        foreach (var slot in ClaimApi.ArmorColumnOrder)
        {
            var column = new DataGridTemplateColumn
            {
                Header = slot,
                HeaderTemplate = BuildSortableHeaderTemplate(),
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                CellTemplate = BuildArmorCellTemplate(slot, tierConverter),
            };
            ClaimArmorGrid.Columns.Add(column);
            _claimArmorColumnKeys[column] = slot;
        }

        _claimArmorRows = new List<ArmorRow>();
        foreach (var member in _claimInfo.Members)
            foreach (var preset in member.ArmorPresets)
            {
                var row = new ArmorRow
                {
                    UserName = member.UserName,
                    PresetLabel = $"#{preset.Index}{(preset.Active ? " ●" : "")}",
                };
                foreach (var slot in ClaimApi.ArmorColumnOrder)
                    row.Slots[slot] = preset.BySlot.TryGetValue(slot, out var piece)
                        ? new ArmorCell { Text = $"{piece.ItemName} (T{piece.Tier})", Color = RarityBrush(piece.RarityStr), Tier = piece.Tier }
                        : new ArmorCell { Text = "—", Color = Brushes.Gray };
                _claimArmorRows.Add(row);
            }
        ClaimArmorGrid.ItemsSource = _claimArmorRows;
    }

    private static DataTemplate BuildArmorCellTemplate(string slot, IValueConverter tierConverter)
    {
        var cell = new FrameworkElementFactory(typeof(Border));
        cell.SetBinding(Border.BackgroundProperty, new Binding($"Slots[{slot}].Tier") { Converter = tierConverter });
        cell.SetValue(Border.PaddingProperty, new Thickness(2, 1, 2, 1));

        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding($"Slots[{slot}].Text"));
        text.SetBinding(TextBlock.ForegroundProperty, new Binding($"Slots[{slot}].Color"));
        text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        cell.AppendChild(text);

        return new DataTemplate { VisualTree = cell };
    }

    private void ClaimArmorGrid_HeaderClicked(object sender, MouseButtonEventArgs e)
    {
        var header = FindAncestor<DataGridColumnHeader>(e.OriginalSource as DependencyObject);
        if (header?.Column is not { } column || !_claimArmorColumnKeys.TryGetValue(column, out var key)) return;

        _armorSortAscending = _armorSortKey == key && !_armorSortAscending; // first click on a column = descending
        _armorSortKey = key;

        IEnumerable<ArmorRow> sorted = key switch
        {
            "Name" => Order(_claimArmorRows, r => r.UserName, _armorSortAscending),
            "Preset" => Order(_claimArmorRows, r => r.PresetLabel, _armorSortAscending),
            _ => Order(_claimArmorRows, r => r.Slots.TryGetValue(key, out var c) ? c.Text : "", _armorSortAscending),
        };
        ClaimArmorGrid.ItemsSource = sorted.ToList();

        var arrow = _armorSortAscending ? " ▲" : " ▼";
        foreach (var (col, colKey) in _claimArmorColumnKeys)
            col.Header = colKey + (colKey == key ? arrow : "");
    }

    // One row per member (not per gear type). Each skill cell stacks 3 lines: Tool (from the
    // Toolbelt - primary, drives both the cell's tier background and the sort order),
    // Instrument, and Charm (from the "*_instrument"/"*_charm" equipment slots - smaller,
    // non-sortable "attached" lines). Only skills confirmed to have Toolbelt tools get a column
    // (ClaimApi.ToolSkillNames).
    private class ToolsCell
    {
        public string ToolText { get; set; } = "—";
        public Brush ToolColor { get; set; } = Brushes.Gray;
        public int ToolTier { get; set; }
        public string InstrumentText { get; set; } = "";
        public Brush InstrumentColor { get; set; } = Brushes.Gray;
        public string CharmText { get; set; } = "";
        public Brush CharmColor { get; set; } = Brushes.Gray;
    }

    private class ToolsRow
    {
        public string UserName { get; set; } = "";
        public Dictionary<string, ToolsCell> BySkill { get; set; } = new();
    }

    private void BuildClaimToolsGrid()
    {
        ClaimToolsGrid.Columns.Clear();
        _claimToolsColumnKeys.Clear();
        _toolsSortKey = null;
        if (_claimInfo is null) return;

        AddClaimColumn(ClaimToolsGrid, _claimToolsColumnKeys, "Name", nameof(ToolsRow.UserName), new DataGridLength(90));

        var tierConverter = new ItemTierBrushConverter();
        var toolSkills = _claimInfo.SkillNames.Where(s => ClaimApi.ToolSkillNames.Contains(s));
        foreach (var skill in toolSkills)
        {
            var column = new DataGridTemplateColumn
            {
                Header = skill,
                HeaderTemplate = BuildSortableHeaderTemplate(),
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                CellTemplate = BuildToolsCellTemplate(skill, tierConverter),
            };
            ClaimToolsGrid.Columns.Add(column);
            _claimToolsColumnKeys[column] = skill;
        }

        _claimToolsRows = _claimInfo.Members.Select(member =>
        {
            var row = new ToolsRow { UserName = member.UserName };
            foreach (var skill in ClaimApi.ToolSkillNames)
            {
                var gear = member.GearBySkill.GetValueOrDefault(skill);
                row.BySkill[skill] = new ToolsCell
                {
                    ToolText = gear?.Tool is { } tool ? $"{tool.ItemName} (T{tool.Tier})" : "—",
                    ToolColor = gear?.Tool is { } t ? RarityBrush(t.RarityStr) : Brushes.Gray,
                    ToolTier = gear?.Tool?.Tier ?? 0,
                    InstrumentText = gear?.Instrument is { } instrument ? instrument.ItemName : "",
                    InstrumentColor = gear?.Instrument is { } i ? RarityBrush(i.RarityStr) : Brushes.Gray,
                    CharmText = gear?.Charm is { } charm ? charm.ItemName : "",
                    CharmColor = gear?.Charm is { } c ? RarityBrush(c.RarityStr) : Brushes.Gray,
                };
            }
            return row;
        }).ToList();
        ClaimToolsGrid.ItemsSource = _claimToolsRows;
    }

    private static DataTemplate BuildToolsCellTemplate(string skill, IValueConverter tierConverter)
    {
        var cell = new FrameworkElementFactory(typeof(Border));
        cell.SetBinding(Border.BackgroundProperty, new Binding($"BySkill[{skill}].ToolTier") { Converter = tierConverter });
        cell.SetValue(Border.PaddingProperty, new Thickness(2, 1, 2, 1));

        var stack = new FrameworkElementFactory(typeof(StackPanel));
        cell.AppendChild(stack);

        var toolText = new FrameworkElementFactory(typeof(TextBlock));
        toolText.SetBinding(TextBlock.TextProperty, new Binding($"BySkill[{skill}].ToolText"));
        toolText.SetBinding(TextBlock.ForegroundProperty, new Binding($"BySkill[{skill}].ToolColor"));
        toolText.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        stack.AppendChild(toolText);

        stack.AppendChild(BuildAttachedLine(skill, "Instrument"));
        stack.AppendChild(BuildAttachedLine(skill, "Charm"));

        return new DataTemplate { VisualTree = cell };
    }

    // A small, non-sortable "attached" line (Instrument or Charm) - collapsed via a DataTrigger
    // rather than left as an empty line, so skills with no charm/instrument don't get a gap.
    private static FrameworkElementFactory BuildAttachedLine(string skill, string field)
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding($"BySkill[{skill}].{field}Text"));
        text.SetBinding(TextBlock.ForegroundProperty, new Binding($"BySkill[{skill}].{field}Color"));
        text.SetValue(TextBlock.FontSizeProperty, 8.0);
        text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        var style = new Style(typeof(TextBlock));
        style.Triggers.Add(new DataTrigger
        {
            Binding = new Binding($"BySkill[{skill}].{field}Text"),
            Value = "",
            Setters = { new Setter(UIElement.VisibilityProperty, Visibility.Collapsed) },
        });
        text.SetValue(FrameworkElement.StyleProperty, style);
        return text;
    }

    private void ClaimToolsGrid_HeaderClicked(object sender, MouseButtonEventArgs e)
    {
        var header = FindAncestor<DataGridColumnHeader>(e.OriginalSource as DependencyObject);
        if (header?.Column is not { } column || !_claimToolsColumnKeys.TryGetValue(column, out var key)) return;

        _toolsSortAscending = _toolsSortKey == key && !_toolsSortAscending; // first click on a column = descending
        _toolsSortKey = key;

        // Charm/instrument ride along with the tool as display-only detail - sorting only
        // ever looks at the tool's own text, per user request.
        IEnumerable<ToolsRow> sorted = key switch
        {
            "Name" => Order(_claimToolsRows, r => r.UserName, _toolsSortAscending),
            _ => Order(_claimToolsRows, r => r.BySkill.TryGetValue(key, out var c) ? c.ToolText : "", _toolsSortAscending),
        };
        ClaimToolsGrid.ItemsSource = sorted.ToList();

        var arrow = _toolsSortAscending ? " ▲" : " ▼";
        foreach (var (col, colKey) in _claimToolsColumnKeys)
            col.Header = colKey + (colKey == key ? arrow : "");
    }

    private void ClaimSubTab_Click(object sender, RoutedEventArgs e)
    {
        ClaimMembersGrid.Visibility = sender == ClaimSubTabMembers ? Visibility.Visible : Visibility.Collapsed;
        ClaimArmorGrid.Visibility = sender == ClaimSubTabArmor ? Visibility.Visible : Visibility.Collapsed;
        ClaimToolsGrid.Visibility = sender == ClaimSubTabTools ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>"Custom" needs both the HiddenTabs toggle AND a configured URL - an
    /// enabled-but-unconfigured tab would just show a blank browser.</summary>
    private bool CustomTabShouldBeVisible =>
        !string.IsNullOrWhiteSpace(_settings.CustomTabUrl) && !_settings.HiddenTabs.Contains("Custom");

    private string DefaultUrlFor(string tab) => tab switch
    {
        "Bitjita" => BitjitaUrl,
        "Brico" => BricoUrl,
        "Mapa" => MapUrl,
        "Custom" => _settings.CustomTabUrl,
        _ => string.IsNullOrWhiteSpace(_settings.BitcraftSyncShareCode)
            ? BitcraftSyncBase
            : $"{BitcraftSyncBase}/s/{_settings.BitcraftSyncShareCode}",
    };

    /// <summary>Re-navigates the currently visible browser tab back to its configured default
    /// URL, discarding wherever an in-page link may have led - handy for the "Custom" tab
    /// especially (a claim's own page might link off-site), but works the same for any browser
    /// tab. No-op on a native tab (Calc/Stats/Claim/Route) - there's no URL to reset there.</summary>
    internal void ReloadCurrentTabToDefault()
    {
        if (Browser.Visibility != Visibility.Visible) return;
        var url = DefaultUrlFor(_currentTab);
        if (string.IsNullOrWhiteSpace(url)) return;
        _settings.LastTabUrls[_currentTab] = url;
        Browser.Source = new Uri(url);
    }

    // --- Route: gathering route planner --------------------------------------

    private bool _routeResourceListLoaded;
    // world units - a common resource (e.g. plain "Bush") can have tens of thousands of matches
    // even in a modest area, and location_state itself (every entity with a position, not just
    // resources) runs into the millions across a wide box - confirmed empirically this box was
    // 2500 before and pulled 3.1M location_state rows + 68,808 matched Bush nodes, which then
    // hung the UI computing a route over all of them. Was 400 (167,529 location_state rows for
    // Bush, ~10s to arrive) - shrunk further since that's still slow. Note this only helps
    // location_state's cost, not resource_state's (187,897 rows for Bush): that one is NOT
    // geographically filterable in SQL at all, it's the whole region regardless of box size.
    private const double RouteBoxHalfSize = 200;
    private const int RouteMaxNodes = 40; // hard cap fed into pathfinding - ponytail: raise if a small box still overwhelms a dense resource
    private static readonly TimeSpan RouteResubscribeCooldown = TimeSpan.FromSeconds(5);
    // Mapless mode (RouteUseInGameMap) skips the terrain crop draw entirely, so a redraw is
    // cheap enough to run noticeably faster - full map mode keeps the original, heavier interval.
    private TimeSpan RouteRecomputeInterval => TimeSpan.FromSeconds(_settings.RouteUseInGameMap ? 0.5 : 1.5);
    private const double RouteRedrawMinMove = 15; // world units - below this, a position update doesn't trigger a full map redraw
    private const double RouteZoomMin = 0.4, RouteZoomMax = 3.0, RouteZoomStep = 0.25;

    private async Task EnsureRouteResourceListLoaded()
    {
        if (_routeResourceListLoaded) return;
        try
        {
            var types = await RouteApi.GetResourceTypesAsync();
            _routeOverlay!.RouteResourceCombo.ItemsSource = types;
            _routeOverlay.RouteResourceCombo.SelectedItem = types.FirstOrDefault(t => t.Id == _settings.RouteLastResourceId) ?? types.FirstOrDefault();
            _routeResourceListLoaded = true;
        }
        catch
        {
            _routeOverlay!.RouteStatusLabel.Text = "Couldn't load the resource list (no internet?).";
        }
    }

    // internal, not private: called from RouteOverlayWindow's forwarding handlers (see its
    // own summary for why the Route XAML now lives in a separate window).
    internal async void RouteFindPlayer_Click(object sender, RoutedEventArgs e)
    {
        var name = _routeOverlay!.RoutePlayerNameBox.Text.Trim();
        if (name.Length < 2)
        {
            _routeOverlay.RoutePlayerFoundLabel.Text = "Type at least 2 characters.";
            return;
        }
        _routeOverlay.RoutePlayerResultsList.Visibility = Visibility.Collapsed;
        _routeOverlay.RoutePlayerFoundLabel.Text = "Searching...";
        await RunBusy((Button)sender, "...", async () =>
        {
            try
            {
                var matches = await BitjitaApi.SearchPlayersAsync(name);
                if (matches.Count == 0)
                {
                    _routeOverlay.RoutePlayerFoundLabel.Text = "No player found.";
                    return;
                }
                _settings.RoutePlayerName = name;
                _settings.Save();

                var exact = matches.Count == 1
                    ? matches[0]
                    : matches.FirstOrDefault(m => string.Equals(m.Username, name, StringComparison.OrdinalIgnoreCase));
                if (exact is not null)
                {
                    _routeOverlay.RoutePlayerFoundLabel.Text = "";
                    await SelectRoutePlayerAsync(exact.EntityId, exact.Username);
                }
                else
                {
                    _routeOverlay.RoutePlayerResultsList.ItemsSource = matches;
                    _routeOverlay.RoutePlayerResultsList.Visibility = Visibility.Visible;
                    _routeOverlay.RoutePlayerFoundLabel.Text = $"{matches.Count} matches - pick one.";
                }
            }
            catch
            {
                _routeOverlay.RoutePlayerFoundLabel.Text = "Search failed (no internet?).";
            }
        });
    }

    internal async void RoutePlayerResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_routeOverlay!.RoutePlayerResultsList.SelectedItem is not PlayerSearchResult picked) return;
        _routeOverlay.RoutePlayerResultsList.Visibility = Visibility.Collapsed;
        await SelectRoutePlayerAsync(picked.EntityId, picked.Username);
    }

    /// <summary>Opens (or replaces) the persistent live-tracking connection for the chosen
    /// player: resolves which region they're live in, connects, and starts a subscription for
    /// their position - no manual N/E entry, this app now reads it straight from the relay (see
    /// SpacetimeLiveConnection.cs / bitcraftoverlay-route-spacetimedb memory).</summary>
    private async Task SelectRoutePlayerAsync(string entityId, string username)
    {
        await DisconnectRouteLiveAsync();

        _settings.RoutePlayerEntityId = entityId;
        _settings.Save();
        _routeOverlay!.RoutePlayerFoundLabel.Text = $"{username} - locating...";
        _routeOverlay.RouteMapImage.Source = null;
        _routeOverlay.RouteMapPlaceholder.Visibility = Visibility.Visible;

        try
        {
            var pos = await SpacetimeClient.FindPlayerPositionAsync(entityId, _settings.RoutePlayerRegion);
            if (pos is null)
            {
                _routeOverlay.RoutePlayerFoundLabel.Text = $"{username} - not online live right now.";
                return;
            }
            _settings.RoutePlayerRegion = pos.Value.Region;
            _settings.Save();

            await TerrainMap.EnsureLoadedAsync();

            _routePlayerEntityId = entityId;
            _routePlayerPos = new RouteNode(username, pos.Value.WorldX, pos.Value.WorldZ);
            _routeBoxCenter = (pos.Value.WorldX, pos.Value.WorldZ);

            void WireCommonEvents(SpacetimeLiveConnection conn)
            {
                conn.RowsChanged += OnRouteRowsChanged;
                conn.Disconnected += ex => Dispatcher.Invoke(() =>
                    _routeOverlay.RoutePlayerFoundLabel.Text = $"{username} - live connection lost ({ex.Message}).");
                conn.QueryFailed += msg => Dispatcher.Invoke(() => _routeOverlay.RouteStatusLabel.Text = $"Query failed: {msg}");
            }

            _routePlayerLive = new SpacetimeLiveConnection();
            WireCommonEvents(_routePlayerLive);
            await _routePlayerLive.ConnectAsync(pos.Value.Region);
            await _routePlayerLive.SubscribeAsync(new[] { $"SELECT * FROM mobile_entity_state WHERE entity_id = {entityId}" });

            _routeLive = new SpacetimeLiveConnection();
            WireCommonEvents(_routeLive);
            await _routeLive.ConnectAsync(pos.Value.Region);

            _routeRecomputeTimer = new DispatcherTimer { Interval = RouteRecomputeInterval };
            _routeRecomputeTimer.Tick += (_, _) =>
            {
                if (_routePendingRecenter is { } pending && DateTime.UtcNow - _routeLastResubscribe >= RouteResubscribeCooldown)
                {
                    _routeBoxCenter = pending;
                    _routePendingRecenter = null;
                    _routeLastResubscribe = DateTime.UtcNow;
                    _ = ResubscribeRouteQueriesAsync();
                }
                if (_routeDirty) { _routeDirty = false; RecomputeRoute(); }
            };
            _routeRecomputeTimer.Start();

            await ResubscribeRouteQueriesAsync();
            _routeOverlay.RoutePlayerFoundLabel.Text = $"{username} - live (region {pos.Value.Region}).";
            _routeDirty = true;
        }
        catch (Exception ex)
        {
            _routeOverlay.RoutePlayerFoundLabel.Text = $"{username} - live tracking failed: {ex.Message}";
        }
    }

    internal async void RouteResourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_routeOverlay!.RouteResourceCombo.SelectedItem is ResourceType resource)
        {
            _settings.RouteLastResourceId = resource.Id;
            _settings.Save();
        }
        if (_routeLive is not null) await ResubscribeRouteQueriesAsync();
    }

    internal void RouteAllowWaterCheck_Changed(object sender, RoutedEventArgs e)
    {
        _settings.RouteAllowWater = _routeOverlay!.RouteAllowWaterCheck.IsChecked == true;
        _settings.Save();
        _routeDirty = true;
    }

    internal void RouteUseInGameMapCheck_Changed(object sender, RoutedEventArgs e)
    {
        _settings.RouteUseInGameMap = _routeOverlay!.RouteUseInGameMapCheck.IsChecked == true;
        _settings.Save();
        _routeOverlay.RouteRoot.Background = _settings.RouteUseInGameMap ? Brushes.Transparent : RouteRootNormalBackground;
        if (_routeRecomputeTimer is not null) _routeRecomputeTimer.Interval = RouteRecomputeInterval;
        _routeDirty = true;
    }

    internal void RouteShowExtraNodesCheck_Changed(object sender, RoutedEventArgs e)
    {
        _settings.RouteShowExtraNodes = _routeOverlay!.RouteShowExtraNodesCheck.IsChecked == true;
        _settings.Save();
        _routeDirty = true;
    }

    internal void RouteZoomIn_Click(object sender, RoutedEventArgs e) => AdjustRouteZoom(-RouteZoomStep);
    internal void RouteZoomOut_Click(object sender, RoutedEventArgs e) => AdjustRouteZoom(RouteZoomStep);

    private void AdjustRouteZoom(double delta)
    {
        _settings.RouteZoom = Math.Clamp(_settings.RouteZoom + delta, RouteZoomMin, RouteZoomMax);
        _routeOverlay!.RouteZoomLabel.Text = $"{_settings.RouteZoom * 100:F0}%";
        _settings.Save();
        _routeDirty = true;
    }

    /// <summary>Sends the desired resource/creature query set for the box around the player -
    /// player position itself lives on the separate _routePlayerLive connection now (see its
    /// field doc comment for why) and is never part of this. A bulk Subscribe REPLACES the whole
    /// set on a connection each call, so every query still wanted has to be resent together, not
    /// just the one that changed - see SpacetimeLiveConnection's docs.</summary>
    private async Task ResubscribeRouteQueriesAsync()
    {
        if (_routeLive is null) return;

        _routeLocationRows.Clear();
        _routeResourceEntityIds.Clear();
        // OnRouteRowsChanged runs on the connection's background thread, where touching a WPF
        // control (RouteResourceCombo.SelectedItem) would throw - stash the selection here on
        // the UI thread instead, for that handler to read.
        _routeActiveResource = _routeOverlay!.RouteResourceCombo.SelectedItem as ResourceType;

        var queries = new List<string>();
        if (_routeActiveResource is { Kind: ResourceKind.Resource } resource && _routeBoxCenter is { } center)
        {
            // Integer-truncate the box bounds and format invariantly - a raw interpolated
            // double would render with a comma on e.g. pl-PL (System.Globalization current
            // culture), producing invalid SQL that silently breaks the whole combined
            // Subscribe call, not just this query.
            var (cx, cz) = center;
            int xLo = (int)(cx - RouteBoxHalfSize), xHi = (int)(cx + RouteBoxHalfSize);
            int zLo = (int)(cz - RouteBoxHalfSize), zHi = (int)(cz + RouteBoxHalfSize);
            queries.Add($"SELECT * FROM resource_state WHERE resource_id = {resource.Id}");
            queries.Add($"SELECT * FROM location_state WHERE x > {xLo} AND x < {xHi} AND z > {zLo} AND z < {zHi}");
        }
        else if (_routeActiveResource is { Kind: ResourceKind.Creature })
        {
            // Creatures live in enemy_mob_monitor_state (entity_id, enemy_type, herd_entity_id,
            // herd_location{x,z,dimension}) - no join needed, position is right there. Can't
            // filter server-side by species (the SQL subset rejects equality on the enemy_type
            // sum-type column - confirmed empirically, "literal cannot be parsed"/"not in
            // scope"), but the whole table is only ~11k rows region-wide, so fetch it all and
            // filter by enemy_type client-side in OnRouteRowsChanged instead.
            queries.Add("SELECT * FROM enemy_mob_monitor_state");
        }
        if (queries.Count == 0) return; // no resource picked yet - nothing to subscribe to
        await _routeLive.SubscribeAsync(queries.ToArray());
    }

    /// <summary>Applies one insert/delete batch to the live caches. Runs on the connection's
    /// background thread - everything here just updates plain dictionaries/fields and flips
    /// _routeDirty; the actual UI/map update happens on the next recompute timer tick on the UI
    /// thread (see RecomputeRoute), so no Dispatcher marshaling is needed in here.</summary>
    private void OnRouteRowsChanged(string table, List<JsonElement> inserts, List<JsonElement> deletes)
    {
        JsonElement F(JsonElement row, string field) => SpacetimeLiveConnection.GetField(row, table, field);
        var meaningfulChange = table != "mobile_entity_state"; // node appear/disappear always redraws

        switch (table)
        {
            case "mobile_entity_state":
                foreach (var row in inserts)
                {
                    var x = F(row, "location_x").GetDouble() / 1000.0;
                    var z = F(row, "location_z").GetDouble() / 1000.0;
                    _routePlayerPos = _routePlayerPos is { } prev ? prev with { X = x, Z = z } : new RouteNode("You", x, z);
                    // SpacetimeDB pushes a position update on every single game-side movement
                    // tick while walking - redrawing the whole map that often looked like it was
                    // constantly re-rendering for no visible reason. Only actually redraw once
                    // the player has moved a meaningfully-visible amount since the last redraw.
                    if (_routeLastRenderedPlayerPos is not { } last || Math.Abs(x - last.X) > RouteRedrawMinMove || Math.Abs(z - last.Z) > RouteRedrawMinMove)
                    {
                        _routeLastRenderedPlayerPos = (x, z);
                        meaningfulChange = true;
                    }
                    // Recenter the search box once the player has wandered halfway to its edge -
                    // just records the intent here; the recompute timer actually acts on it,
                    // rate-limited (see RouteResubscribeCooldown), so a player walking
                    // continuously can't trigger a fresh resource_state/location_state fetch
                    // (expensive - real ones have run 40+ seconds for a common resource) on
                    // every single step.
                    if (_routeBoxCenter is not { } c || Math.Abs(x - c.X) > RouteBoxHalfSize / 2 || Math.Abs(z - c.Z) > RouteBoxHalfSize / 2)
                        _routePendingRecenter = (x, z);
                }
                if (deletes.Count > 0 && inserts.Count == 0) { _routePlayerPos = null; meaningfulChange = true; } // player went offline / left the region
                break;

            // Deletes are applied before inserts in both cases below: an in-place row update
            // arrives as a delete(old)+insert(new) pair for the same entity_id in one batch, and
            // the insert must win - doing it the other way round would erase the fresh value.
            case "resource_state":
                foreach (var row in deletes) _routeResourceEntityIds.Remove(F(row, "entity_id").ToString()!);
                foreach (var row in inserts) _routeResourceEntityIds.Add(F(row, "entity_id").ToString()!);
                break;

            case "location_state":
                foreach (var row in deletes) _routeLocationRows.Remove(F(row, "entity_id").ToString()!);
                foreach (var row in inserts)
                    _routeLocationRows[F(row, "entity_id").ToString()!] = (F(row, "x").GetDouble(), F(row, "z").GetDouble());
                break;

            // Creatures: entity_id/position both live right here, no location_state join needed
            // - see the comment in ResubscribeRouteQueriesAsync. enemy_type is a sum type
            // ([tagIndex, payload]) and always array-encoded regardless of context (confirmed
            // empirically, unlike Products) - reading index 0 directly is safe.
            case "enemy_mob_monitor_state":
                if (_routeActiveResource is not { Kind: ResourceKind.Creature } creature) break;
                foreach (var row in deletes)
                {
                    var id = F(row, "entity_id").ToString()!;
                    _routeResourceEntityIds.Remove(id);
                    _routeLocationRows.Remove(id);
                }
                foreach (var row in inserts)
                {
                    if (F(row, "enemy_type")[0].GetInt32() != creature.Id) continue;
                    var id = F(row, "entity_id").ToString()!;
                    var herdLoc = F(row, "herd_location");
                    var x = SpacetimeLiveConnection.GetNestedField(herdLoc, SpacetimeLiveConnection.HerdLocationColumns, "x").GetDouble();
                    var z = SpacetimeLiveConnection.GetNestedField(herdLoc, SpacetimeLiveConnection.HerdLocationColumns, "z").GetDouble();
                    _routeResourceEntityIds.Add(id);
                    _routeLocationRows[id] = (x, z);
                }
                break;
        }
        if (meaningfulChange) _routeDirty = true;
    }

    /// <summary>Redraws the map from whatever's currently in the live caches. Runs on the UI
    /// thread (called from the DispatcherTimer tick), so it can touch UI elements directly.</summary>
    private void RecomputeRoute()
    {
        if (_routePlayerPos is not { } player)
        {
            _routeOverlay!.RouteMapImage.Source = null;
            _routeOverlay.RouteMapPlaceholder.Visibility = Visibility.Visible;
            return;
        }

        var resource = _routeOverlay!.RouteResourceCombo.SelectedItem as ResourceType;
        // Cap to the nearest RouteMaxNodes within a bounded radius before clustering/pathfinding.
        // Resource nodes are already geographically boxed server-side (see
        // ResubscribeRouteQueriesAsync), but creatures (enemy_mob_monitor_state) are NOT - the
        // SQL subset can't filter that table server-side at all, so its "nearest 50 of ~1000
        // region-wide" can span thousands of units and cross water. That blew up 2-opt: each
        // water-crossing pair falls back to a full A* search (up to 300k nodes), and 2-opt calls
        // distance() tens of thousands of times - confirmed empirically, this hung the UI for
        // ~20s on a real Sagi Bird test. The radius cutoff below applies to both kinds, so a
        // resource somehow returning distant matches gets the same protection.
        const double maxNodeDistance = RouteBoxHalfSize * 1.5;
        var nearby = resource is null
            ? new List<(double X, double Z)>()
            : _routeResourceEntityIds.Where(_routeLocationRows.ContainsKey)
                .Select(id => _routeLocationRows[id])
                .Select(p => (p, distSq: (p.X - player.X) * (p.X - player.X) + (p.Z - player.Z) * (p.Z - player.Z)))
                .Where(t => t.distSq <= maxNodeDistance * maxNodeDistance)
                .OrderBy(t => t.distSq)
                .Select(t => t.p)
                .ToList();
        // Only the nearest RouteMaxNodes actually get pathfound (2-opt is O(n^2) - see the radius
        // comment above), but the rest of `nearby` isn't wasted: shown as plain dots on the map
        // below, since we already fetched the data anyway.
        var nodeCoords = RoutePlanner.WithClusterSizes(
            nearby.Take(RouteMaxNodes).Select(p => new RouteNode(resource?.Name ?? "", p.X, p.Z)).ToList(), radius: 30);

        try
        {
            // Water avoidance is a checkbox, not automatic - fishing/water-based gathering needs
            // to walk straight through it, so the Euclidean distance is used as-is in that case
            // instead of routing around.
            var avoidWater = _routeOverlay!.RouteAllowWaterCheck.IsChecked != true;
            Func<RouteNode, RouteNode, double> distance = avoidWater
                ? (a, b) => TerrainMap.PathDistance(a.X, a.Z, b.X, b.Z)
                : (a, b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

            List<RouteNode> route = nodeCoords.Count == 0
                ? new List<RouteNode> { player }
                : RoutePlanner.BuildRoute(player, nodeCoords, null, distance, preferHotspots: true);

            // A node can be the tour's next-in-order choice by (penalized) distance while still
            // having no actual walked path to it from wherever the route currently stands (e.g.
            // a separate island) - PathDistance pads that case in the ordering math, but the
            // node can still end up in the sequence. Rather than truncating the WHOLE rest of
            // the route at the first such node (which threw away later stops that genuinely were
            // reachable), skip just that one node - stay at the current stop and try the next
            // node in tour order instead. Skipped nodes go back to being plain dots (extraNodes).
            var extras = nearby.Skip(RouteMaxNodes).ToList();
            if (avoidWater && route.Count > 1)
            {
                var kept = new List<RouteNode> { route[0] };
                foreach (var next in route.Skip(1))
                {
                    if (TerrainMap.GetWalkPath(kept[^1].X, kept[^1].Z, next.X, next.Z) is not null)
                        kept.Add(next);
                    else
                        extras.Add((next.X, next.Z));
                }
                route = kept;
            }

            // Render at the panel's actual current size (not a fixed square) - this app usually
            // runs as a small, non-square floating window (as small as ~200x200), and a fixed
            // square render only filled that without black letterbox bars by coincidence.
            var outputWidth = (int)Math.Max(RoutePanel.ActualWidth, 50);
            var outputHeight = (int)Math.Max(RoutePanel.ActualHeight, 50);
            var image = RouteMapRenderer.Render(route, hasEnd: false, avoidWater, _settings.RouteShowExtraNodes ? extras : null, outputWidth, outputHeight, _settings.RouteZoom, _settings.RouteUseInGameMap);
            _routeOverlay.RouteMapImage.Source = image;
            _routeOverlay.RouteMapPlaceholder.Visibility = image is null ? Visibility.Visible : Visibility.Collapsed;
            _routeOverlay.RouteStatusLabel.Text = resource is null
                ? "Live position - pick a resource to see nodes and a route."
                : $"Live - {nearby.Count} {resource.Name} node(s) nearby ({route.Count - 1} routed).";
        }
        catch (Exception ex)
        {
            _routeOverlay.RouteStatusLabel.Text = $"Couldn't render the route: {ex.Message}";
        }
    }

    private async Task DisconnectRouteLiveAsync()
    {
        _routeRecomputeTimer?.Stop();
        _routeRecomputeTimer = null;
        if (_routePlayerLive is not null)
        {
            await _routePlayerLive.DisposeAsync();
            _routePlayerLive = null;
        }
        if (_routeLive is not null)
        {
            await _routeLive.DisposeAsync();
            _routeLive = null;
        }
        _routePlayerEntityId = null;
        _routePlayerPos = null;
        _routeBoxCenter = null;
        _routeLastRenderedPlayerPos = null;
        _routePendingRecenter = null;
        _routeLastResubscribe = DateTime.MinValue;
        _routeLocationRows.Clear();
        _routeResourceEntityIds.Clear();
    }

    internal void RouteToggleSetup_Click(object sender, RoutedEventArgs e)
    {
        var collapse = _routeOverlay!.RouteSetupBody.Visibility == Visibility.Visible;
        _routeOverlay.RouteSetupBody.Visibility = collapse ? Visibility.Collapsed : Visibility.Visible;
        _routeOverlay.RouteToggleSetupButton.Content = collapse ? "+" : "–";
    }

    // --- Settings -----------------------------------------------------------

    internal async void OpenSettings(Window owner)
    {
        var dialog = new SettingsWindow(_settings.BitcraftSyncShareCode, _settings.CustomTabUrl, _settings.HiddenTabs, _settings.UseIconTabs, _settings.RouteClickThrough, _settings.RouteOpacity) { Owner = owner };
        if (dialog.ShowDialog() == true)
        {
            // Route's live tracking (two persistent WebSocket connections + a recompute timer)
            // keeps running in the background regardless of which tab is currently showing, by
            // design - that's what makes it "live" across tab switches. But if the tab gets
            // turned off in Settings entirely, there's no reason for any of that to keep running
            // - tear it down rather than leave it working for a tab the user can no longer reach.
            var routeJustHidden = !_settings.HiddenTabs.Contains("Route") && dialog.HiddenTabs.Contains("Route");

            _settings.BitcraftSyncShareCode = dialog.ShareCode;
            var customUrlChanged = _settings.CustomTabUrl != dialog.CustomUrl;
            _settings.CustomTabUrl = dialog.CustomUrl;
            _settings.HiddenTabs = dialog.HiddenTabs;
            _settings.UseIconTabs = dialog.UseIconTabs;
            _settings.RouteClickThrough = dialog.RouteClickThrough;
            _settings.RouteOpacity = dialog.RouteOpacity;
            _routeOverlay?.ApplyOverlayState(_currentTab == "Route"); // live-update immediately if already on the Route tab
            if (routeJustHidden)
            {
                await DisconnectRouteLiveAsync();
                TerrainMap.Unload();
                _routeOverlay!.RouteMapImage.Source = null;
                // Dropping references makes the terrain bitmap/live-tracking dictionaries
                // eligible for collection, but .NET doesn't necessarily reclaim that memory (or
                // hand it back to the OS) right away on its own schedule - forcing a collection
                // here is a deliberate one-off (the user just explicitly turned off a heavy
                // feature), not something to do on a hot path.
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
            _settings.LastTabUrls.Remove("BitcraftSync");
            if (customUrlChanged) _settings.LastTabUrls.Remove("Custom"); // a freshly-saved URL should take effect immediately, not whatever was last loaded there
            _dirty = true;
            _settings.Save();
            _header?.ApplyHiddenTabs(_settings.HiddenTabs);
            _header?.ApplyDisplayMode(_settings.UseIconTabs);

            _header?.SetCustomTabVisible(CustomTabShouldBeVisible);

            var visibleTabs = new[] { "BitcraftSync", "Bitjita", "Brico", "Mapa", "Calc", "Stats", "Claim", "Route" }
                .Concat(CustomTabShouldBeVisible ? new[] { "Custom" } : Array.Empty<string>());
            if (_settings.HiddenTabs.Contains(_currentTab) || (_currentTab == "Custom" && !CustomTabShouldBeVisible))
            {
                var firstVisible = visibleTabs.FirstOrDefault(t => !_settings.HiddenTabs.Contains(t));
                if (firstVisible != null) ShowTab(firstVisible);
            }
            else if (_settings.LastTab == "BitcraftSync" || (_currentTab == "Custom" && customUrlChanged))
            {
                ShowTab(_currentTab); // re-navigate to pick up the freshly-saved share code / custom URL
            }
        }
    }
}
