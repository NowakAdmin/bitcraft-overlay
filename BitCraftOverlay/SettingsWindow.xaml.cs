using System.Diagnostics;
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
    public int TransparencyPercent { get; private set; }
    public List<string> HiddenTabs { get; private set; } = new();

    private readonly Action<int> _onTransparencyPreview;
    private readonly int _originalTransparency;

    public SettingsWindow(string currentShareCode, int currentTransparency, List<string> hiddenTabs, Action<int> onTransparencyPreview)
    {
        InitializeComponent();
        ShareCodeBox.Text = currentShareCode;
        _originalTransparency = currentTransparency;
        _onTransparencyPreview = onTransparencyPreview;
        TransparencySlider.Value = currentTransparency; // fires ValueChanged, sets TransparencyPercent + label

        ShowBitcraftSync.IsChecked = !hiddenTabs.Contains("BitcraftSync");
        ShowBitjita.IsChecked = !hiddenTabs.Contains("Bitjita");
        ShowBrico.IsChecked = !hiddenTabs.Contains("Brico");
        ShowMapa.IsChecked = !hiddenTabs.Contains("Mapa");

        VersionLabel.Text = $"Wersja {CurrentVersion}";
        Loaded += (_, _) => ShareCodeBox.Focus();
    }

    private static string CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    private void TransparencySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        TransparencyPercent = (int)e.NewValue;
        TransparencyLabel.Text = $"{TransparencyPercent}%";
        _onTransparencyPreview(TransparencyPercent); // live preview so the user sees what they're setting
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
                MessageBox.Show("Masz już najnowszą wersję.", "BitCraft Overlay", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch
        {
            MessageBox.Show("Nie udało się sprawdzić aktualizacji (brak internetu albo nie ma jeszcze żadnego release'u).",
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

        HiddenTabs = new List<string>();
        if (ShowBitcraftSync.IsChecked != true) HiddenTabs.Add("BitcraftSync");
        if (ShowBitjita.IsChecked != true) HiddenTabs.Add("Bitjita");
        if (ShowBrico.IsChecked != true) HiddenTabs.Add("Brico");
        if (ShowMapa.IsChecked != true) HiddenTabs.Add("Mapa");

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _onTransparencyPreview(_originalTransparency); // undo the live preview
        DialogResult = false;
    }
}
