using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BitCraftOverlay;

/// <summary>
/// The game's own static world terrain image, used to avoid routing gathering trips through
/// water. Downloaded once from the official game asset server and cached locally as a compact
/// walkability bitset - no network needed after the first load.
///
/// File format (reverse-engineered by downloading and inspecting the real file - not from any
/// docs, since none exist): gzip-compressed, 9-byte header, then 4000x4000 pixels at 8
/// bytes/pixel (byte0=R, byte1=G, byte2=B, byte3=alpha [0=off-map void, 200=normal, 255=
/// highlighted], bytes4-7 unused/always zero). The 4000x4000 grid maps onto the game's "small
/// hex" world coordinate space (same space as bitjita's claim locationX/locationZ) - world span
/// confirmed as 38400 (not 23040, an old Alpha-era figure) via relay.bitcraftsync.app's
/// /roads/regions: a 5x5 grid of regions, each 7680 units wide/tall. The in-game HUD shows a
/// coarser "big hex" N/E readout (permanently under the map since Early Access 2) - see
/// MainWindow.xaml.cs's NEtoWorld for the confirmed x3 conversion to this "small hex" space.
/// Water was identified empirically - and got it backwards on the first pass: an earlier
/// version of this comment claimed water was blue-leading and treated the dominant red-leading
/// color as "distant undetailed backdrop" to hide. Direct comparison against the live game (a
/// debug render sent to a player standing at a known coastal spot) showed the OPPOSITE: the
/// red-leading color IS the water - both bright red (open ocean/lakes) and dark maroon (rivers)
/// - and the blue/cyan-leading color is land. Real samples: water ~(69,49,42) (r clearly leads
/// both g and b); land ~(52,75,66)/(74,100,100) (g and b both exceed r). The ratio check below
/// (b &lt; r*0.75, i.e. blue trailing red by a clear margin) is what actually separates real
/// water from a similarly red-leaning but NOT water color (a rose/pink island in region 13 has
/// b/r~0.90-0.92, comfortably above the 0.75 cutoff) - a plain r&gt;g&amp;&amp;r&gt;b majority
/// vote would wrongly catch that island too.
/// </summary>
public static class TerrainMap
{
    private const string SourceUrl = "https://maps.game.bitcraftonline.com/world-maps/TerrainMap.gwm";
    public const int GridSize = 4000;
    // Confirmed via relay.bitcraftsync.app's /roads/regions (real protobuf data, not a guess):
    // the world is a 5x5 grid of regions, each 7680 units wide/tall (origin_x/origin_z step by
    // 7680 between adjacent regions) -> full world span 5*7680 = 38400. The old 23040 figure
    // (from bitcraftmap.com's Alpha-era docs) was wrong for the current Early Access world.
    public const double WorldSpan = 38400.0;

    // X and Z do NOT share one scale - confirmed by finding the real stitch-seams between
    // regions directly in the pixel data (a sharp per-row/per-column color discontinuity, from
    // how the image is assembled out of independently-exported region tiles): vertical seams
    // land exactly every 800px (5 columns, matches GridSize/WorldSpan cleanly); horizontal seams
    // land every ~693px (5 rows would need 800px each to match - they don't). There's also a
    // ~535px band of extra content above the topmost real region that isn't part of the 5x5
    // grid at all (visually confirmed against the live game map - "there's nothing there").
    // ScaleX stays GridSize/WorldSpan; ScaleZ and the Z origin are fitted directly to the
    // measured seam rows instead of assumed equal to ScaleX.
    private const double ScaleX = GridSize / WorldSpan;
    private const double ScaleZ = 693.0 / 7680.0;

    // v3: v2 fixed orientation but used a harsh linear brightness multiply that clipped into an
    // "overexposed" look and left water an off-blue/muddy hue - v3 uses a gamma curve for land
    // and an explicit clean-blue recolor for detected water. Versioned filename so anyone who
    // already ran v1/v2 gets a fresh decode instead of reusing the old-looking cache.
    // v6: fixed the real bug - X and Z use different pixel scales (confirmed via the image's own
    // region stitch-seams), not the single shared scale every earlier version assumed. Also
    // restores the gamma/blue-water display styling (v5 was raw passthrough, kept only for
    // comparing against reference screenshots while debugging the above).
    // v10: dropped the gamma stretch on land - raw passthrough reads closer to what the asset
    // actually contains, the stretch was our own stylization choice, not a correction.
    // v11: water/land were backwards - the red-leading color is water (ocean/lakes/rivers), the
    // blue/cyan-leading color is land, confirmed against a real player's live position. Fixed by
    // reusing the old (correct, just mislabeled) "background" ratio check as the real isWater
    // test instead of painting it black.
    // v12: darkened the water display color - v11's was too pale next to land.
    // v13: loosened the isWater ratio thresholds (a thin real channel was walked straight
    // through in testing - most likely a faded/anti-aliased edge the tighter cutoff missed) and
    // pushed the water display color more saturated/blue.
    private static readonly string CacheFile = Path.Combine(Settings.AppDataRoot, "terrain.v13.bits");
    private static readonly string ImageCacheFile = Path.Combine(Settings.AppDataRoot, "terrain.v13.png");
    private static readonly HttpClient Http = new();

    static TerrainMap() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("BitCraftOverlay");

    // One bit per grid cell (16,000,000 cells -> 2,000,000 bytes) - small enough to keep fully
    // resident, avoids re-touching the much larger raw pixel data on every lookup.
    private static byte[]? _walkableBits;

    // The decoded color image, for the Route tab's map render - built once (from the cache file
    // if present, otherwise alongside the walkability bits) and kept in memory as a frozen
    // (thread-safe, immutable) bitmap for the lifetime of the process.
    private static BitmapSource? _mapBitmap;
    public static BitmapSource? MapBitmap => _mapBitmap;

    /// <summary>Drops the in-memory copy (the bitmap alone is ~48MB, GridSize^2*3 bytes) - for
    /// when the Route tab gets turned off in Settings entirely, so a feature the user can no
    /// longer reach doesn't keep holding onto it. EnsureLoadedAsync reloads from the on-disk
    /// cache (fast) the next time the tab is used again, no re-download needed.</summary>
    public static void Unload()
    {
        _walkableBits = null;
        _mapBitmap = null;
    }

    public static async Task EnsureLoadedAsync()
    {
        if (_walkableBits != null && _mapBitmap != null) return;

        if (_walkableBits is null && File.Exists(CacheFile))
        {
            var cached = await File.ReadAllBytesAsync(CacheFile);
            if (cached.Length == GridSize * GridSize / 8) _walkableBits = cached; // sanity check - stale/corrupt cache falls through to a re-download below
        }
        if (_mapBitmap is null && File.Exists(ImageCacheFile))
        {
            try
            {
                var decoder = new PngBitmapDecoder(new Uri(ImageCacheFile), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames[0];
                if (frame.PixelWidth == GridSize && frame.PixelHeight == GridSize) { frame.Freeze(); _mapBitmap = frame; }
            }
            catch { /* corrupt cache file - falls through to a re-download below */ }
        }
        if (_walkableBits != null && _mapBitmap != null) return;

        using var gzipStream = await Http.GetStreamAsync(SourceUrl);
        using var raw = new MemoryStream();
        using (var gunzip = new GZipStream(gzipStream, CompressionMode.Decompress))
            await gunzip.CopyToAsync(raw);
        var data = raw.ToArray();

        const int header = 9, pixelSize = 8;
        var bits = new byte[GridSize * GridSize / 8];
        var pixels = new byte[GridSize * GridSize * 3]; // Bgr24 for BitmapSource.Create below
        for (var i = 0; i < GridSize * GridSize; i++)
        {
            var offset = header + i * pixelSize;
            if (offset + 3 >= data.Length) break;
            int r = data[offset], g = data[offset + 1], b = data[offset + 2];
            // Water is red-leading (both bright-red open ocean/lakes and dark-maroon rivers),
            // land is blue/cyan-leading - see the class doc comment for how this was confirmed
            // (backwards from an earlier guess). The r>=g*0.80 half keeps a red-leaning but not
            // watery color (a rose-colored island in region 13) out - that island's b/r ratio
            // sits at ~0.90-0.92, comfortably above the 0.80 cutoff. Loosened from the original
            // 0.85/0.75 cutoffs (still a wide margin under the island's 0.90-0.92) - a real thin
            // channel walked straight through in testing, most likely a faded/anti-aliased edge
            // pixel that the tighter threshold missed. Biasing toward over-detecting water is the
            // safe direction here: a false "land" reading sends the route straight through water,
            // a false "water" reading just costs an unnecessary detour.
            var isWater = r >= g * 0.80 && b < r * 0.80;

            // Source rows run Z=max..0 (confirmed against a known-good reference render) - flip
            // to Z=0..max so pixel row order matches world Z order used everywhere else (ToGrid/
            // WorldToPixel). X is already correctly oriented, only Z needs the flip.
            var rawGx = i % GridSize;
            var rawGz = i / GridSize;
            var outIndex = (GridSize - 1 - rawGz) * GridSize + rawGx;

            if (!isWater) bits[outIndex / 8] |= (byte)(1 << (outIndex % 8)); // bit set = walkable

            var (dr, dg, db) = isWater ? WaterDisplayColor(b) : LandDisplayColor(r, g, b);
            var p = outIndex * 3;
            pixels[p] = db;
            pixels[p + 1] = dg;
            pixels[p + 2] = dr;
        }

        Directory.CreateDirectory(Settings.AppDataRoot);
        await File.WriteAllBytesAsync(CacheFile, bits);
        _walkableBits = bits;

        var bitmap = BitmapSource.Create(GridSize, GridSize, 96, 96, PixelFormats.Bgr24, null, pixels, GridSize * 3);
        bitmap.Freeze(); // built off the UI thread - freezing makes it safe to hand to WPF controls later
        _mapBitmap = bitmap;
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var fs = File.Create(ImageCacheFile);
            encoder.Save(fs);
        }
        catch { /* image cache is a nice-to-have (skips the ~1s decode on next launch) - failure to write it isn't fatal */ }
    }

    /// <summary>Grid pixel for a world coordinate - public so the Route tab's map renderer can
    /// crop/place things against the same image TerrainMap decoded.</summary>
    public static (int X, int Z) WorldToPixel(double worldX, double worldZ) => ToGrid(worldX, worldZ);

    // Raw passthrough - no stylization on land anymore, just the asset's own muted colors.
    private static (byte R, byte G, byte B) LandDisplayColor(int r, int g, int b) =>
        ((byte)r, (byte)g, (byte)b);

    private static (byte R, byte G, byte B) WaterDisplayColor(int rawB)
    {
        // Slight variation from the raw blue channel keeps water from looking like one flat
        // sticker, without hunting for the "true" hue of an asset that was never meant to be
        // displayed as-is. Pushed more saturated/blue than the first cut, which read as pale
        // and washed-out against land.
        var shade = (byte)Math.Clamp(140 + (rawB - 100) * 0.6, 110, 210);
        return (5, 40, shade);
    }

    /// <summary>True if a world coordinate is land (or unknown/off-map - fails open rather than
    /// blocking a route on a coordinate outside the loaded grid). False only for confirmed water.</summary>
    public static bool IsWalkable(double worldX, double worldZ)
    {
        if (_walkableBits is null) return true; // not loaded yet - caller should await EnsureLoadedAsync first
        var (gx, gz) = ToGrid(worldX, worldZ);
        if (gx < 0 || gx >= GridSize || gz < 0 || gz >= GridSize) return true;
        var i = gz * GridSize + gx;
        return (_walkableBits[i / 8] & (1 << (i % 8))) != 0;
    }

    /// <summary>Straight line, walked in grid steps (Bresenham), checking each cell.</summary>
    public static bool HasLineOfSight(double x1, double z1, double x2, double z2)
    {
        var (gx1, gz1) = ToGrid(x1, z1);
        var (gx2, gz2) = ToGrid(x2, z2);
        int dx = Math.Abs(gx2 - gx1), dz = Math.Abs(gz2 - gz1);
        int sx = gx1 < gx2 ? 1 : -1, sz = gz1 < gz2 ? 1 : -1;
        int err = dx - dz, x = gx1, z = gz1;
        while (true)
        {
            var (wx, wz) = FromGrid(x, z);
            if (!IsWalkable(wx, wz)) return false;
            if (x == gx2 && z == gz2) return true;
            var e2 = 2 * err;
            if (e2 > -dz) { err -= dz; x += sx; }
            if (e2 < dx) { err += dx; z += sz; }
        }
    }

    /// <summary>Walking distance between two world points, avoiding water. Tries a direct line
    /// first (cheap, and true for most short hops); falls back to A* around obstacles, and to a
    /// padded straight-line estimate if A* can't find a path within its search budget (e.g. the
    /// two points are on separated landmasses) - the 1.4x keeps the route planner from treating
    /// an actually-unreachable node as equally good as a reachable one at the same straight-line
    /// distance.</summary>
    // ponytail: no paved-tile speed bonus yet (requested as a future improvement) - the terrain
    // image only encodes water/land, not claim tile paving, which would need a separate live data
    // source (claim tile state, not in TerrainMap.gwm). Add as a per-edge cost multiplier here
    // once that data source exists, rather than reworking the A* grid itself.
    public static double PathDistance(double x1, double z1, double x2, double z2)
    {
        if (HasLineOfSight(x1, z1, x2, z2))
            return Math.Sqrt((x2 - x1) * (x2 - x1) + (z2 - z1) * (z2 - z1));

        var path = AStar.FindPath(x1, z1, x2, z2, IsWalkable, ToGrid, FromGrid, GridSize);
        if (path is null) return Math.Sqrt((x2 - x1) * (x2 - x1) + (z2 - z1) * (z2 - z1)) * 1.4;

        double total = 0;
        for (var i = 1; i < path.Count; i++)
        {
            var (px, pz) = path[i - 1];
            var (qx, qz) = path[i];
            total += Math.Sqrt((qx - px) * (qx - px) + (qz - pz) * (qz - pz));
        }
        return total;
    }

    /// <summary>The actual walked waypoints between two points, avoiding water - a straight
    /// 2-point line for most short hops, or the real A* detour when that line crosses water.
    /// Returns null if A* can't find a path within its budget (e.g. the two points are on
    /// separated landmasses) - there IS no real walked path in that case, so the caller should
    /// NOT fall back to a straight line (that line likely crosses the very water this exists to
    /// avoid - confirmed as a real, visibly-wrong case: a route drew straight through a channel
    /// once the search gave up). Used both for the distance calculation above (PathDistance pads
    /// this case instead) and for drawing the route (RouteMapRenderer skips the segment, leaving
    /// a visible gap instead of a misleading line).</summary>
    public static List<(double X, double Z)>? GetWalkPath(double x1, double z1, double x2, double z2)
    {
        if (HasLineOfSight(x1, z1, x2, z2))
            return new List<(double X, double Z)> { (x1, z1), (x2, z2) };

        return AStar.FindPath(x1, z1, x2, z2, IsWalkable, ToGrid, FromGrid, GridSize);
    }

    // Z is inverted between world space and pixel space: increasing world Z is confirmed North
    // (the game's own region grid numbers increase northward - R6 sits north of R1, etc.), and
    // pixel row 0 is the TOP of a normally-displayed image, which needs to be North. Derived
    // (not guessed) from the real seam rows: fitting world Z=7680/15360/23040/30720 (region row
    // boundaries) against their measured seam pixel rows 3308/2615/1923/1230 gives pixel_row =
    // GridSize - Z*ScaleZ almost exactly (GridSize=4000 fits the fitted intercept, ~4000.5, to
    // within rounding). This also naturally excludes the ~535px phantom band above the real
    // grid: no valid world Z maps into pixel rows 0-535, since Z=38400 (world max) lands at
    // pixel row 4000-38400*ScaleZ=535.
    private static (int X, int Z) ToGrid(double worldX, double worldZ) => ((int)(worldX * ScaleX), (int)(GridSize - worldZ * ScaleZ));
    private static (double X, double Z) FromGrid(int gx, int gz) => (gx / ScaleX, (GridSize - gz) / ScaleZ);
}

/// <summary>
/// Small, self-contained grid A* - only used to route short hops around water between nearby
/// gathering nodes, not to path across the whole 4000x4000 world, so a capped node budget keeps
/// worst-case runtime bounded rather than chasing an unreachable target across the map.
/// </summary>
internal static class AStar
{
    // Was 300,000, cut to 20,000 to stop a real UI hang - then cut too far: a real detour (not
    // an unreachable pair) started failing within budget and fell back to a straight line
    // through the water it was supposed to route around. RoutePlanner now caches each unique
    // pair's distance (see RoutePlanner.BuildRoute), which was the actual fix for the hang - a
    // slow search only costs once per pair now, not once per 2-opt candidate that touches it.
    // With that in place, a bigger budget here is safe again for genuinely-unreachable pairs
    // (still fails, just takes longer once) while giving real detours enough room to succeed.
    private const int NodeBudget = 80_000;

    private static readonly (int Dx, int Dz)[] Neighbors =
    {
        (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1),
    };

    public static List<(double X, double Z)>? FindPath(
        double x1, double z1, double x2, double z2,
        Func<double, double, bool> isWalkable,
        Func<double, double, (int X, int Z)> toGrid,
        Func<int, int, (double X, double Z)> fromGrid,
        int gridSize)
    {
        var (sx, sz) = toGrid(x1, z1);
        var (tx, tz) = toGrid(x2, z2);
        int Key(int x, int z) => z * gridSize + x;
        double Heuristic(int x, int z) => Math.Sqrt((tx - x) * (tx - x) + (tz - z) * (tz - z));

        var open = new PriorityQueue<int, double>();
        var gScore = new Dictionary<int, double> { [Key(sx, sz)] = 0 };
        var cameFrom = new Dictionary<int, int>();
        open.Enqueue(Key(sx, sz), Heuristic(sx, sz));

        var visited = 0;
        while (open.Count > 0 && visited++ < NodeBudget)
        {
            var current = open.Dequeue();
            var cx = current % gridSize;
            var cz = current / gridSize;
            if (cx == tx && cz == tz) return Reconstruct(cameFrom, current, gridSize, fromGrid);

            foreach (var (dx, dz) in Neighbors)
            {
                var nx = cx + dx;
                var nz = cz + dz;
                if (nx < 0 || nx >= gridSize || nz < 0 || nz >= gridSize) continue;
                var (wx, wz) = fromGrid(nx, nz);
                if (!isWalkable(wx, wz)) continue;

                var step = dx != 0 && dz != 0 ? 1.41421356 : 1.0;
                var tentative = gScore[current] + step;
                var neighborKey = Key(nx, nz);
                if (gScore.TryGetValue(neighborKey, out var known) && tentative >= known) continue;

                gScore[neighborKey] = tentative;
                cameFrom[neighborKey] = current;
                open.Enqueue(neighborKey, tentative + Heuristic(nx, nz));
            }
        }
        return null; // exhausted the budget without reaching the target - caller falls back to a padded straight-line estimate
    }

    private static List<(double X, double Z)> Reconstruct(Dictionary<int, int> cameFrom, int current, int gridSize, Func<int, int, (double X, double Z)> fromGrid)
    {
        var path = new List<(double X, double Z)>();
        while (true)
        {
            path.Add(fromGrid(current % gridSize, current / gridSize));
            if (!cameFrom.TryGetValue(current, out var prev)) break;
            current = prev;
        }
        path.Reverse();
        return path;
    }
}
