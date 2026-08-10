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

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "BitCraftOverlay", "settings.json");

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
