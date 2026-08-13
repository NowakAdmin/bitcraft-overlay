using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
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
    private readonly ObservableCollection<StatComparison> _statComparisons;
    private StatSnapshot? _statA;
    private StatSnapshot? _statB;
    private ClaimInfo? _claimInfo;

    public MainWindow()
    {
        InitializeComponent();
        Browser.CreationProperties = new CoreWebView2CreationProperties { UserDataFolder = Settings.WebView2DataFolder };
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

        CalcPanel.Visibility = tab == "Calc" ? Visibility.Visible : Visibility.Collapsed;
        StatsPanel.Visibility = tab == "Stats" ? Visibility.Visible : Visibility.Collapsed;
        ClaimPanel.Visibility = tab == "Claim" ? Visibility.Visible : Visibility.Collapsed;
        Browser.Visibility = tab is "Calc" or "Stats" or "Claim" ? Visibility.Collapsed : Visibility.Visible;
        if (tab is "Calc" or "Stats" or "Claim") return;

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
            "Last seen" => Order(_claimInfo.Members, m => m.LastLoginRaw, _claimSortAscending),
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
                var firstVisible = new[] { "BitcraftSync", "Bitjita", "Brico", "Mapa", "Calc", "Stats", "Claim" }.FirstOrDefault(t => !_settings.HiddenTabs.Contains(t));
                if (firstVisible != null) ShowTab(firstVisible);
            }
            else if (_settings.LastTab == "BitcraftSync")
            {
                ShowTab("BitcraftSync");
            }
        }
    }
}
