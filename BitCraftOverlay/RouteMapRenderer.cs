using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BitCraftOverlay;

/// <summary>
/// Renders a computed route onto a crop of the terrain map: one flat bitmap (background image +
/// route line + numbered stop markers), auto-framed to the route's own bounding box. A static
/// render, not an interactive pan/zoom control - simplest thing that lets the user actually see
/// the route, see TerrainMap.cs for the underlying map data.
/// </summary>
public static class RouteMapRenderer
{
    private static readonly Brush StartBrush = Brushes.LimeGreen;
    private static readonly Brush EndBrush = Brushes.OrangeRed;
    private static readonly Brush NodeBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xD5, 0x4A));
    // Render() now renders at the actual on-screen resolution (see its own doc comment) instead
    // of a fixed size WPF then stretches, so these are true screen-pixel sizes, not inflated to
    // survive a shrink - a window as small as ~200x200 is still legible at these.
    private static readonly Pen RoutePen = new(Brushes.White, 3) { LineJoin = PenLineJoin.Round };
    private static readonly Pen MarkerOutline = new(Brushes.Black, 1);
    private static readonly Brush ExtraNodeBrush = new SolidColorBrush(Color.FromArgb(150, 0xFF, 0xD5, 0x4A));

    /// <param name="avoidWater">Must match whatever RoutePlanner.BuildRoute's distance function
    /// used - draws each segment's actual walked path (TerrainMap.GetWalkPath) when true,
    /// straight lines when false. Passing true here while the route itself was built with plain
    /// Euclidean distance (or vice versa) would draw a path that doesn't match how the stops were
    /// actually ordered.</param>
    /// <param name="extraNodes">Every matching node in view, not just the ones the route was
    /// actually planned through (RouteMaxNodes caps that list for pathfinding performance, but
    /// there's no reason not to just show the rest too - the data's already fetched). Drawn as
    /// small, dim, unnumbered dots underneath the real route markers.</param>
    /// <param name="outputWidth">/<param name="outputHeight">The actual on-screen size the
    /// caller is about to display this at (e.g. RouteMapImage.ActualWidth/Height) - rendering at
    /// that exact resolution, rather than a fixed size WPF then stretches, means markers/text
    /// come out crisp and correctly sized in every window shape instead of getting blurred or
    /// shrunk by a second scaling pass. The crop is widened or heightened (never cropped
    /// tighter) to match this aspect ratio too, so Stretch="Uniform" never needs to letterbox -
    /// this app is usually run as a small, arbitrarily-shaped floating window (confirmed as
    /// small as ~200x200), not the square 700x700 this used to assume.</param>
    /// <param name="zoom">Multiplier on the auto-fit frame around the route's own stops - 1.0
    /// is the default framing, smaller zooms in, larger zooms out. Applied in pixel space,
    /// before the aspect-ratio correction, so it composes cleanly with a non-square window.</param>
    public static BitmapSource? Render(List<RouteNode> route, bool hasEnd, bool avoidWater,
        IReadOnlyList<(double X, double Z)>? extraNodes, int outputWidth, int outputHeight, double zoom = 1.0)
    {
        var map = TerrainMap.MapBitmap;
        if (map is null || route.Count == 0) return null;
        outputWidth = Math.Max(1, outputWidth);
        outputHeight = Math.Max(1, outputHeight);

        var minX = route.Min(n => n.X);
        var maxX = route.Max(n => n.X);
        var minZ = route.Min(n => n.Z);
        var maxZ = route.Max(n => n.Z);
        var pad = Math.Max(Math.Max(maxX - minX, maxZ - minZ) * 0.15, 150);
        minX -= pad; maxX += pad; minZ -= pad; maxZ += pad;
        minX = Math.Clamp(minX, 0, TerrainMap.WorldSpan);
        minZ = Math.Clamp(minZ, 0, TerrainMap.WorldSpan);
        maxX = Math.Clamp(maxX, 0, TerrainMap.WorldSpan);
        maxZ = Math.Clamp(maxZ, 0, TerrainMap.WorldSpan);

        // Z inverts between world and pixel space (higher Z = smaller pixel row, north-up), so
        // the corner from maxZ can land at a SMALLER pixel row than the corner from minZ - take
        // the actual min/max of the converted pixel coordinates, not of the world corners.
        var (cornerAx, cornerAz) = TerrainMap.WorldToPixel(minX, minZ);
        var (cornerBx, cornerBz) = TerrainMap.WorldToPixel(maxX, maxZ);
        var rawPx0 = Math.Min(cornerAx, cornerBx);
        var rawPz0 = Math.Min(cornerAz, cornerBz);
        var rawPw = Math.Max(1, Math.Abs(cornerBx - cornerAx));
        var rawPh = Math.Max(1, Math.Abs(cornerBz - cornerAz));

        // Zoom scales the frame around its own center, before the aspect-ratio correction below
        // (which only ever grows the shorter side to fill the window shape, never shrinks) - so
        // zooming in/out affects both dimensions evenly regardless of window shape.
        zoom = Math.Clamp(zoom, 0.1, 20);
        rawPx0 -= (int)(rawPw * (zoom - 1) / 2);
        rawPz0 -= (int)(rawPh * (zoom - 1) / 2);
        rawPw = Math.Max(1, (int)(rawPw * zoom));
        rawPh = Math.Max(1, (int)(rawPh * zoom));

        // Expand (never shrink) whichever pixel dimension is short of the target aspect ratio -
        // this is done in PIXEL space, not world space, sidestepping TerrainMap's X/Z having
        // different world-to-pixel scales (ScaleX != ScaleZ - see its own doc comment).
        var targetAspect = outputWidth / (double)outputHeight;
        int pw = rawPw, ph = rawPh;
        if (rawPw / (double)rawPh > targetAspect) ph = (int)(rawPw / targetAspect);
        else pw = (int)(rawPh * targetAspect);

        var pcx = rawPx0 + rawPw / 2;
        var pcz = rawPz0 + rawPh / 2;
        var px0 = Math.Clamp(pcx - pw / 2, 0, TerrainMap.GridSize - 1);
        var pz0 = Math.Clamp(pcz - ph / 2, 0, TerrainMap.GridSize - 1);
        pw = Math.Max(1, Math.Min(pw, TerrainMap.GridSize - px0));
        ph = Math.Max(1, Math.Min(ph, TerrainMap.GridSize - pz0));
        var cropped = new CroppedBitmap(map, new Int32Rect(px0, pz0, pw, ph));

        var scaleX = outputWidth / (double)pw;
        var scaleZ = outputHeight / (double)ph;
        Point ToRenderWorld(double wx, double wz)
        {
            var (px, pz) = TerrainMap.WorldToPixel(wx, wz);
            return new Point((px - px0) * scaleX, (pz - pz0) * scaleZ);
        }
        Point ToRender(RouteNode n) => ToRenderWorld(n.X, n.Z);

        var visual = new DrawingVisual();
        // The source is only ~9.6 world units/pixel, and a tight crop around a few nearby nodes
        // magnifies it a lot (often 5-10x) - default bilinear scaling smears that into a blur.
        // Nearest-neighbor keeps hard edges, which reads better for "is this water" at a glance
        // than a smoothed-out gradient would.
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(cropped, new Rect(0, 0, outputWidth, outputHeight));

            for (var i = 1; i < route.Count; i++)
            {
                // Draw the actual walked path when water avoidance is on, not just a straight
                // line to the next stop - PathDistance was already routing around water for the
                // ordering/distance math, but the render used to always draw a straight line
                // regardless, which visibly cut across water even when the real walk didn't.
                // Caller (MainWindow.RecomputeRoute) already truncates the route at the first
                // genuinely-unreachable edge, so GetWalkPath returning null here shouldn't
                // happen in practice - skip the segment (leave a gap) instead of a misleading
                // straight line if it ever does.
                var segment = avoidWater
                    ? TerrainMap.GetWalkPath(route[i - 1].X, route[i - 1].Z, route[i].X, route[i].Z)
                    : new List<(double X, double Z)> { (route[i - 1].X, route[i - 1].Z), (route[i].X, route[i].Z) };
                if (segment is null) continue;
                for (var j = 1; j < segment.Count; j++)
                    dc.DrawLine(RoutePen, ToRenderWorld(segment[j - 1].X, segment[j - 1].Z), ToRenderWorld(segment[j].X, segment[j].Z));
            }

            if (extraNodes != null)
                foreach (var (nx, nz) in extraNodes)
                    dc.DrawEllipse(ExtraNodeBrush, null, ToRenderWorld(nx, nz), 4.0, 4.0);

            // Highest index first, index 0 (start) last - draw order is paint order, so the
            // LAST one drawn ends up on top. Without this, tightly clustered stops showed
            // whichever had the highest number on top instead of the lowest/start.
            for (var i = route.Count - 1; i >= 0; i--)
            {
                var isStart = i == 0;
                var isEnd = hasEnd && i == route.Count - 1;
                var brush = isStart ? StartBrush : isEnd ? EndBrush : NodeBrush;
                var radius = isStart || isEnd ? 9.0 : 7.0;
                var p = ToRender(route[i]);
                dc.DrawEllipse(brush, MarkerOutline, p, radius, radius);

                if (!isStart)
                {
                    var label = isEnd ? "◆" : i.ToString();
                    var text = new FormattedText(label, System.Globalization.CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, Brushes.Black, 1.0);
                    dc.DrawText(text, new Point(p.X - text.Width / 2, p.Y - text.Height / 2));
                }
            }
        }

        var rtb = new RenderTargetBitmap(outputWidth, outputHeight, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }
}
