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

    /// <summary>A player-configured URL for a "Custom" browser tab - e.g. a claim's own Google
    /// Sheet or website. Visibility is controlled the same way as every other tab, via
    /// HiddenTabs ("Custom") - MainWindow only actually shows the tab button when this is also
    /// non-empty, so an enabled-but-unconfigured tab never appears blank.</summary>
    public string CustomTabUrl { get; set; } = "";

    /// <summary>Tab names hidden from the header bar. Empty = all visible (default).</summary>
    public List<string> HiddenTabs { get; set; } = new();

    /// <summary>Show each service's favicon instead of its text label on the tab bar. Default
    /// on - icons take much less horizontal space, and a small overlay window with a full row
    /// of text-label tabs can fill the whole header bar, leaving nowhere empty left to drag.</summary>
    public bool UseIconTabs { get; set; } = true;

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

    /// <summary>Route tab overlay mode: while showing Route, the whole overlay window
    /// (not the topbar - that's a separate always-opaque window) becomes click-through,
    /// so mouse input falls to BitCraft underneath instead of hitting the overlay. Off
    /// by default - a click-through window can't be interacted with normally, so this
    /// is an explicit opt-in, not something a Route visit should silently trigger.</summary>
    public bool RouteClickThrough { get; set; }

    /// <summary>Route tab overlay opacity, 0.0-1.0 (same convention as RouteZoom).
    /// Applied as native window alpha only while the Route tab is showing; every other
    /// tab, and the topbar, stay fully opaque regardless of this value.</summary>
    public double RouteOpacity { get; set; } = 1.0;

    /// <summary>When on, the Route map renders ONLY the pathfinding line + resource node
    /// dots (no terrain background, no player marker) against a genuinely transparent
    /// window background, and frames the view centered on the player's own position
    /// instead of auto-fitting the route's bounding box - meant to be positioned over
    /// BitCraft's own in-game minimap (which already keeps the player centered), adding
    /// route guidance on top of it rather than replacing it with our own synthetic map.
    /// Pair with RouteClickThrough so clicks still reach the game's minimap underneath.</summary>
    public bool RouteUseInGameMap { get; set; }

    /// <summary>Whether to draw the dim, unnumbered dots for every nearby matching resource
    /// beyond what RouteMaxNodes actually routes through - on by default, but useful to turn
    /// off when a dense resource area makes those extra dots too cluttered to read.</summary>
    public bool RouteShowExtraNodes { get; set; } = true;

    /// <summary>Everything the app saves lives under here, so one folder (and one "show my data"
    /// button) covers it all. Next to the .exe (in a "Data" subfolder), not %LocalAppData% - the
    /// app is meant to be portable (unzip anywhere, including a USB stick or a synced folder,
    /// and take its settings/cache with it). The tradeoff: a release zip must never include an
    /// already-populated Data folder from local dev/testing - see the publish notes wherever
    /// this app gets packaged.</summary>
    public static readonly string AppDataRoot = Path.Combine(AppContext.BaseDirectory, "Data");

    private static readonly string FilePath = Path.Combine(AppDataRoot, "settings.json");

    /// <summary>Where WebView2 keeps its browser profile (cache, cookies, Twitch login...).
    /// Without this, WebView2 defaults to a "<exe-name>.WebView2" folder sitting right next to
    /// the .exe - this just gives that clutter one clearly-named home alongside everything else
    /// this app saves, instead of a second loose folder next to the binaries.</summary>
    public static readonly string WebView2DataFolder = Path.Combine(AppDataRoot, "WebView2");

    /// <summary>Shared WebView2 startup config for every control in the app (MainWindow's tabs
    /// AND TwitchWindow - both must use identical properties, since they share one browser
    /// environment via the same UserDataFolder, and the FIRST control to initialize is the one
    /// whose settings actually take effect for the whole shared environment).
    /// - renderer-process-limit caps how many Chromium renderer processes exist at once,
    ///   forcing reuse instead of a fresh one per site visited.
    /// - disable-gpu: the GPU process runs for the whole browser environment regardless of
    ///   which tab is active (confirmed - it doesn't tear down/restart per navigation), and only
    ///   bitcraftmap.com's interactive WebGL map actually benefits from it; every other tab
    ///   (BitcraftSync, Bitjita, Brico, Twitch, a Custom URL) is plain content. Trade-off: the
    ///   map may render/pan more slowly without hardware acceleration - if that's noticeably
    ///   worse, this is the first thing to revert.
    /// NOT disabling the storage service (IndexedDB/localStorage/cookies) despite it also
    /// showing up as its own process - that's what keeps Twitch (and any other tab) logged in
    /// between launches, the whole reason this app uses a persistent UserDataFolder at all.</summary>
    public static Microsoft.Web.WebView2.Wpf.CoreWebView2CreationProperties CreateWebViewCreationProperties() => new()
    {
        UserDataFolder = WebView2DataFolder,
        AdditionalBrowserArguments = "--renderer-process-limit=4 --disable-gpu",
    };

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
