using System.Net.Http;
using System.Text.Json;

namespace BitCraftOverlay;

// Kind distinguishes bitjita's two separate catalogs (resource_desc vs enemy_desc) - both are
// shown in the same picker today, but a future live-data hookup needs to know which SpacetimeDB
// table pair to query (resource_state+location_state vs enemy_state+mobile_entity_state).
public enum ResourceKind { Resource, Creature }

public record ResourceType(int Id, string Name, int Tier, ResourceKind Kind = ResourceKind.Resource);

public record RouteStop(int Order, string Label, double X, double Z, double DistFromPrev, double CumulativeDist);

public record RouteResult(List<RouteStop> Stops, double TotalDistance);

/// <summary>
/// Thin client for the REST-only pieces the Route tab needs from bitjita.com: the resource
/// type catalog and region lookup. Live node/player positions are NOT here - those need the
/// game's own live database (see SpacetimeClient.cs), REST has no position data.
/// </summary>
public static class RouteApi
{
    private static readonly HttpClient Http = new();

    static RouteApi() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("BitCraftOverlay");

    // Same non-gatherable tags nodeindex's own catalog generator excludes (bones, depleted
    // resources, doors, dungeon-only stuff, etc.) - keeps the picker to real field resources.
    private static readonly HashSet<string> BlockedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "Bones", "Depleted Resource", "Door", "Energy Font", "Fruit", "Insects", "Note", "Obstacle",
        "Dungeon Resource", "Dungeon Obstacle", "Enemy Spawner",
    };

    public static async Task<List<ResourceType>> GetResourceTypesAsync()
    {
        var result = new List<ResourceType>();

        var json = await Http.GetStringAsync("https://bitjita.com/api/resources");
        using (var doc = JsonDocument.Parse(json))
        {
            if (doc.RootElement.TryGetProperty("resources", out var resources))
                foreach (var r in resources.EnumerateArray())
                {
                    var tag = r.TryGetProperty("tag", out var t) ? t.GetString() ?? "" : "";
                    if (BlockedTags.Contains(tag)) continue;
                    var name = r.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    if (name.Contains("Interior", StringComparison.OrdinalIgnoreCase)) continue; // indoor-only variants, not field nodes
                    result.Add(new ResourceType(
                        r.GetProperty("id").GetInt32(),
                        name,
                        r.TryGetProperty("tier", out var tier) ? tier.GetInt32() : 0));
                }
        }

        // Huntable animals (Sagi Bird, Nubi Goat, ...) live in a separate catalog from ordinary
        // gathering resources - non-huntable entries here are hostile monsters, not gathering targets.
        var creaturesJson = await Http.GetStringAsync("https://bitjita.com/api/creatures");
        using (var doc = JsonDocument.Parse(creaturesJson))
        {
            if (doc.RootElement.TryGetProperty("creatures", out var creatures))
                foreach (var c in creatures.EnumerateArray())
                {
                    if (!c.TryGetProperty("huntable", out var huntable) || !huntable.GetBoolean()) continue;
                    result.Add(new ResourceType(
                        c.GetProperty("enemyType").GetInt32(),
                        c.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                        c.TryGetProperty("tier", out var tier) ? tier.GetInt32() : 0,
                        ResourceKind.Creature));
                }
        }

        return result.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
