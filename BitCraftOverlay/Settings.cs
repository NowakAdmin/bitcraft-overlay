using System.IO;
using System.Text.Json;

namespace BitCraftOverlay;

/// <summary>Persisted overlay state: window position/size, last tab, bitcraftsync share code.</summary>
public class Settings
{
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double WindowWidth { get; set; } = 420;
    public double WindowHeight { get; set; } = 680;
    public string LastTab { get; set; } = "BitcraftSync";
    public string BitcraftSyncShareCode { get; set; } = "";

    /// <summary>Tab names hidden from the header bar. Empty = all visible (default).</summary>
    public List<string> HiddenTabs { get; set; } = new();

    /// <summary>Show each service's favicon instead of its text label on the tab bar.</summary>
    public bool UseIconTabs { get; set; } = false;

    /// <summary>Last full URL visited per tab (e.g. bitcraftmap.com encodes its view in the URL) - restored on next open instead of the tab's plain default.</summary>
    public Dictionary<string, string> LastTabUrls { get; set; } = new();

    /// <summary>Everything the app saves lives under here, so one folder (and one "show my data" button) covers it all.</summary>
    public static readonly string AppDataRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BitCraftOverlay");

    private static readonly string FilePath = Path.Combine(AppDataRoot, "settings.json");

    /// <summary>
    /// Where WebView2 keeps its browser profile (cache, cookies, Twitch login...).
    /// Without this, WebView2 defaults to a "<exe-name>.WebView2" folder sitting
    /// right next to the .exe - annoying clutter, especially for a portable zip
    /// extracted to the Desktop.
    /// </summary>
    public static readonly string WebView2DataFolder = Path.Combine(AppDataRoot, "WebView2");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch
        {
            // ponytail: corrupt/missing settings file just falls back to defaults, no repair UI
        }
        return new Settings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
