namespace BitCraftOverlay;

/// <summary>One stop in a route: a gathering node, the player's start position, or a claim
/// return point. ClusterSize &gt; 1 marks a node sitting in a dense pocket of other nodes (see
/// RoutePlanner.WithClusterSizes) - used to prefer "hot spot" areas over solo nodes.</summary>
public record RouteNode(string Label, double X, double Z, int ClusterSize = 1);

/// <summary>
/// Pure route-ordering algorithm - no network/file I/O, so it's fully testable against
/// hand-written coordinates. Nearest-neighbor construction (optionally hotspot-weighted) plus a
/// 2-opt improvement pass. Distance is injected so the same code orders a route with plain
/// Euclidean distance (no terrain data needed) or with TerrainMap.PathDistance (water-aware)
/// without any change here.
/// </summary>
public static class RoutePlanner
{
    /// <summary>Tags each node with how many other nodes sit within <paramref name="radius"/> of
    /// it (grid-bucket density count, not a real clustering algorithm - good enough to tell
    /// "dense pocket" from "solo node", upgrade only if solo nodes still get over-visited).</summary>
    public static List<RouteNode> WithClusterSizes(List<RouteNode> nodes, double radius)
    {
        return nodes.Select(n => n with
        {
            ClusterSize = nodes.Count(o => (o.X - n.X) * (o.X - n.X) + (o.Z - n.Z) * (o.Z - n.Z) <= radius * radius),
        }).ToList();
    }

    /// <summary>Builds a visiting order starting at <paramref name="start"/>, covering every
    /// entry in <paramref name="nodes"/>, optionally ending at a fixed <paramref name="end"/>
    /// (e.g. the player's claim). Returns the full stop sequence including start/end.</summary>
    public static List<RouteNode> BuildRoute(
        RouteNode start, List<RouteNode> nodes, RouteNode? end,
        Func<RouteNode, RouteNode, double> distance, bool preferHotspots = false)
    {
        // 2-opt's TourLength recomputes every edge in the tour for every candidate reversal, so
        // the same (a,b) pair gets asked for repeatedly - possibly thousands of times across a
        // full run. That's cheap for plain Euclidean distance, but `distance` here can be
        // TerrainMap.PathDistance, which falls back to a full A* search when the straight line
        // crosses water - and if two nodes are genuinely unreachable by land (e.g. separated
        // islands), A* burns its entire node budget every single call before giving up. Confirmed
        // empirically: this hung the UI for tens of seconds on real water-heavy data. Caching
        // each pair's distance the first time it's asked turns that into "expensive once,"
        // instead of "expensive every time" - a much bigger win here than reducing node counts.
        var cache = new Dictionary<(RouteNode, RouteNode), double>();
        double CachedDistance(RouteNode a, RouteNode b)
        {
            var key = (a, b);
            if (!cache.TryGetValue(key, out var d))
                cache[key] = d = distance(a, b);
            return d;
        }

        // Both phases optimize the SAME objective - if 2-opt minimized raw distance while
        // construction favored hot spots, 2-opt would just undo the hotspot preference (its
        // whole job is finding a shorter tour). Cluster size is a property of the node being
        // entered, so weighting b's side of each edge keeps a plain sum-of-edges 2-opt coherent.
        double Effective(RouteNode a, RouteNode b) => preferHotspots ? CachedDistance(a, b) / (1 + b.ClusterSize) : CachedDistance(a, b);

        var order = NearestNeighbor(start, nodes, Effective);
        order = TwoOpt(start, order, end, Effective);

        var route = new List<RouteNode> { start };
        route.AddRange(order);
        if (end != null) route.Add(end);
        return route;
    }

    private static List<RouteNode> NearestNeighbor(RouteNode start, List<RouteNode> nodes, Func<RouteNode, RouteNode, double> distance)
    {
        var remaining = new List<RouteNode>(nodes);
        var order = new List<RouteNode>(remaining.Count);
        var current = start;

        while (remaining.Count > 0)
        {
            var best = 0;
            var bestScore = double.MaxValue;
            for (var i = 0; i < remaining.Count; i++)
            {
                var score = distance(current, remaining[i]);
                if (score < bestScore) { bestScore = score; best = i; }
            }
            current = remaining[best];
            order.Add(current);
            remaining.RemoveAt(best);
        }
        return order;
    }

    // Fixed-iteration-cap 2-opt: typical routes here are tens of nodes, so a full pass over all
    // segment pairs is cheap. `end`, if present, is never moved out of the last slot - only the
    // nodes before it get reordered around it.
    private static List<RouteNode> TwoOpt(RouteNode start, List<RouteNode> order, RouteNode? end, Func<RouteNode, RouteNode, double> distance)
    {
        if (order.Count < 3) return order;

        double TourLength(List<RouteNode> o)
        {
            double total = distance(start, o[0]);
            for (var i = 1; i < o.Count; i++) total += distance(o[i - 1], o[i]);
            if (end != null) total += distance(o[^1], end);
            return total;
        }

        var improved = true;
        var iterations = 0;
        const int maxIterations = 200; // ponytail: fixed cap on outer passes - fine for the tens-of-nodes routes this feature deals with
        while (improved && iterations++ < maxIterations)
        {
            improved = false;
            for (var i = 0; i < order.Count - 1; i++)
            {
                for (var j = i + 1; j < order.Count; j++)
                {
                    var candidate = new List<RouteNode>(order);
                    candidate.Reverse(i, j - i + 1);
                    if (TourLength(candidate) < TourLength(order) - 1e-9)
                    {
                        order = candidate;
                        improved = true;
                    }
                }
            }
        }
        return order;
    }
}
