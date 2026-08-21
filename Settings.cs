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

    /// <summary>Saved rate (start/stop) calculations, newest first.</summary>
    public List<CalcEntry> SavedCalculations { get; set; } = new();

    /// <summary>bitjita.com player identity for the Stats tab (resolved once via search, then reused).</summary>
    public string StatsPlayerName { get; set; } = "";
    public string StatsPlayerEntityId { get; set; } = "";

    /// <summary>Saved snapshot-A/B comparisons, newest first. Tool power per skill is captured inside each snapshot itself.</summary>
    public List<StatComparison> SavedComparisons { get; set; } = new();

    /// <summary>Last-searched claim name for the Claim tab (just a prefill convenience - always re-searched on Find).</summary>
    public string ClaimName { get; set; } = "";

    /// <summary>Full result of the last Claim tab Find - shown immediately on next launch, replaced only by another Find.</summary>
    public ClaimInfo? SavedClaimData { get; set; }

    /// <summary>bitjita.com player identity for the Route tab (same pattern as StatsPlayerName/EntityId).</summary>
    public string RoutePlayerName { get; set; } = "";
    public string RoutePlayerEntityId { get; set; } = "";

    // Player position is read live and continuously from relay.bitcraftsync.app (a public,
    // no-auth-required SpacetimeDB mirror - see SpacetimeClient.cs/SpacetimeLiveConnection.cs
    // and the bitcraftoverlay-route-spacetimedb memory for why NOT Clockwork Labs' own server,
    // which needs a Developer Token this app doesn't have). No manual position entry anymore.

    /// <summary>Cached BitCraft region (real map-grid number, not the disproven 1-9 guess) the
    /// player was last found live in - tried first on the next live-position fetch since a
    /// player rarely changes world, avoiding a probe across every live region every time.</summary>
    public int? RoutePlayerRegion { get; set; }

    public int RouteLastResourceId { get; set; }
    /// <summary>Off by default (avoid water) - checked for fishing/water-based gathering, where
    /// the route needs to walk straight through water instead of routing around it.</summary>
    public bool RouteAllowWater { get; set; }

    /// <summary>1.0 = auto-fit around the route's own stops (the default framing) - smaller
    /// zooms in, larger zooms out. Applied as a multiplier on top of that auto-fit frame, not an
    /// absolute world-unit span, so it stays sensible whether the route is tightly clustered or
    /// spread out.</summary>
    public double RouteZoom { get; set; } = 1.0;

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
