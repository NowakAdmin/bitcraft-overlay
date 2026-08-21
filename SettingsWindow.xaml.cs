using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;

namespace BitCraftOverlay;

public partial class SettingsWindow : Window
{
    private const string RepoOwner = "NowakAdmin";
    private const string RepoName = "bitcraft-overlay";

    public string ShareCode { get; private set; } = "";
    public List<string> HiddenTabs { get; private set; } = new();
    public bool UseIconTabs { get; private set; }

    public SettingsWindow(string currentShareCode, List<string> hiddenTabs, bool useIconTabs)
    {
        InitializeComponent();
        ShareCodeBox.Text = currentShareCode;

        UseIconsToggle.IsChecked = useIconTabs;
        ShowBitcraftSync.IsChecked = !hiddenTabs.Contains("BitcraftSync");
        ShowBitjita.IsChecked = !hiddenTabs.Contains("Bitjita");
        ShowBrico.IsChecked = !hiddenTabs.Contains("Brico");
        ShowMapa.IsChecked = !hiddenTabs.Contains("Mapa");
        ShowCalc.IsChecked = !hiddenTabs.Contains("Calc");
        ShowStats.IsChecked = !hiddenTabs.Contains("Stats");
        ShowClaim.IsChecked = !hiddenTabs.Contains("Claim");
        ShowRoute.IsChecked = !hiddenTabs.Contains("Route");
        ShowTwitch.IsChecked = !hiddenTabs.Contains("Twitch");

        VersionLabel.Text = $"Version {CurrentVersion}";
        Loaded += (_, _) => ShareCodeBox.Focus();
    }

    private static string CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    private void Kofi_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("https://ko-fi.com/Z6O024TDK7") { UseShellExecute = true });

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Settings.AppDataRoot); // so Explorer has something to open even on first run
        // ShellExecute on a bare folder path is flaky in some hosting contexts; launching
        // explorer.exe directly with the path as an argument is the reliable way to do this.
        Process.Start("explorer.exe", $"\"{Settings.AppDataRoot}\"");
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("BitCraftOverlay");
            var json = await http.GetStringAsync($"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest");
            using var doc = JsonDocument.Parse(json);
            var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            var releaseUrl = doc.RootElement.TryGetProperty("html_url", out var urlProp)
                ? urlProp.GetString() ?? ReleasesPageUrl
                : ReleasesPageUrl;

            var isNewer = Version.TryParse(tag.TrimStart('v', 'V'), out var remote)
                       && Version.TryParse(CurrentVersion, out var local)
                       && remote > local;

            if (isNewer)
                Process.Start(new ProcessStartInfo(releaseUrl) { UseShellExecute = true });
            else
                MessageBox.Show("You already have the latest version.", "BitCraft Overlay", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch
        {
            MessageBox.Show("Couldn't check for updates (no internet, or no release published yet).",
                "BitCraft Overlay", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private static string ReleasesPageUrl => $"https://github.com/{RepoOwner}/{RepoName}/releases";

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        // Accept either a bare code ("abc123") or a pasted full link - strip the
        // "/s/" prefix and any trailing query/hash/slash if present.
        var text = ShareCodeBox.Text.Trim();
        var marker = "/s/";
        var idx = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        var code = idx >= 0 ? text[(idx + marker.Length)..] : text;
        code = code.Split('?', '#')[0];
        ShareCode = code.Trim('/', ' ');

        UseIconTabs = UseIconsToggle.IsChecked == true;

        HiddenTabs = new List<string>();
        if (ShowBitcraftSync.IsChecked != true) HiddenTabs.Add("BitcraftSync");
        if (ShowBitjita.IsChecked != true) HiddenTabs.Add("Bitjita");
        if (ShowBrico.IsChecked != true) HiddenTabs.Add("Brico");
        if (ShowMapa.IsChecked != true) HiddenTabs.Add("Mapa");
        if (ShowCalc.IsChecked != true) HiddenTabs.Add("Calc");
        if (ShowStats.IsChecked != true) HiddenTabs.Add("Stats");
        if (ShowClaim.IsChecked != true) HiddenTabs.Add("Claim");
        if (ShowRoute.IsChecked != true) HiddenTabs.Add("Route");
        if (ShowTwitch.IsChecked != true) HiddenTabs.Add("Twitch");

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
