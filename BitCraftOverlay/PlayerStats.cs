using System.Net.Http;
using System.Text.Json;

namespace BitCraftOverlay;

/// <summary>
/// A full player-state capture at one point in time: XP per skill, item quantities
/// across every container the player owns, placeable count, and whatever tool is
/// currently in hand (main_hand/off_hand).
/// </summary>
public class StatSnapshot
{
    public long TimestampUnix { get; set; }
    public Dictionary<string, long> SkillXp { get; set; } = new();
    public Dictionary<string, long> Items { get; set; } = new();
    public int PlaceableCount { get; set; }
    public List<string> EquippedTools { get; set; } = new(); // e.g. "Steel Pickaxe (T7)"
}

/// <summary>Two saved snapshots kept together under a name, so the diff between them can be reloaded later.</summary>
public class StatComparison
{
    public string Name { get; set; } = "";
    public StatSnapshot A { get; set; } = new();
    public StatSnapshot B { get; set; } = new();
}

/// <summary>
/// Thin client for bitjita.com's public player API. The field shapes below were
/// reverse-engineered from live responses (the published docs don't detail them) -
/// ponytail: best-effort against an undocumented API; if item/skill names stop
/// resolving, re-check the live JSON shape for /players, /players/[id],
/// /players/[id]/inventories and /players/[id]/equipment.
/// </summary>
public static class BitjitaApi
{
    private static readonly HttpClient Http = new();

    static BitjitaApi() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("BitCraftOverlay");

    public static async Task<(string EntityId, string Username)?> FindPlayerAsync(string name)
    {
        var json = await Http.GetStringAsync($"https://bitjita.com/api/players?q={Uri.EscapeDataString(name)}");
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("players", out var players)) return null;

        JsonElement? best = null;
        foreach (var p in players.EnumerateArray())
        {
            if (string.Equals(p.GetProperty("username").GetString(), name, StringComparison.OrdinalIgnoreCase))
            {
                best = p;
                break;
            }
            best ??= p;
        }
        if (best is null) return null;
        return (best.Value.GetProperty("entityId").GetString()!, best.Value.GetProperty("username").GetString()!);
    }

    public static async Task<StatSnapshot> TakeSnapshotAsync(string entityId)
    {
        var snapshot = new StatSnapshot { TimestampUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };

        var playerJson = await Http.GetStringAsync($"https://bitjita.com/api/players/{entityId}");
        using (var doc = JsonDocument.Parse(playerJson))
        {
            var player = doc.RootElement.GetProperty("player");
            snapshot.PlaceableCount = player.TryGetProperty("placeableCount", out var pc) ? pc.GetInt32() : 0;

            var skillNames = new Dictionary<string, string>();
            if (player.TryGetProperty("skillMap", out var skillMap))
                foreach (var kv in skillMap.EnumerateObject())
                    skillNames[kv.Name] = kv.Value.TryGetProperty("name", out var n) ? n.GetString() ?? kv.Name : kv.Name;

            if (player.TryGetProperty("experience", out var exp))
                foreach (var e in exp.EnumerateArray())
                {
                    var skillId = e.GetProperty("skill_id").GetInt32().ToString();
                    var qty = e.GetProperty("quantity").GetInt64();
                    var name = skillNames.TryGetValue(skillId, out var n) ? n : $"Skill {skillId}";
                    snapshot.SkillXp[name] = qty;
                }
        }

        // /inventories only gives bare itemId+quantity refs (no name/tier catalog anywhere
        // in the response, despite how this used to be parsed) - so item names fall back to
        // "Item {id}" until there's a cheap way to resolve names for a whole inventory.
        var invJson = await Http.GetStringAsync($"https://bitjita.com/api/players/{entityId}/inventories");
        using (var doc = JsonDocument.Parse(invJson))
        {
            if (doc.RootElement.TryGetProperty("inventories", out var invs))
                foreach (var inv in invs.EnumerateArray())
                {
                    if (!inv.TryGetProperty("pockets", out var pockets)) continue;
                    foreach (var pocket in pockets.EnumerateArray())
                    {
                        if (!pocket.TryGetProperty("contents", out var contents) || contents.ValueKind != JsonValueKind.Object) continue;
                        if (!contents.TryGetProperty("itemId", out var idProp)) continue;
                        var id = idProp.GetInt64().ToString();
                        var qty = contents.TryGetProperty("quantity", out var q) ? q.GetInt64() : 0;
                        var name = $"Item {id}";
                        snapshot.Items[name] = snapshot.Items.GetValueOrDefault(name) + qty;
                    }
                }
        }

        // Unlike /inventories, /equipment embeds the full item (name, tier, ...) inline -
        // no catalog cross-reference needed for whatever's currently in hand.
        var equipJson = await Http.GetStringAsync($"https://bitjita.com/api/players/{entityId}/equipment");
        using (var doc = JsonDocument.Parse(equipJson))
        {
            if (doc.RootElement.TryGetProperty("equipment", out var equipment))
                foreach (var slot in equipment.EnumerateArray())
                {
                    if (!slot.TryGetProperty("primary", out var primaryProp) || primaryProp.GetString() is not ("main_hand" or "off_hand")) continue;
                    if (!slot.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object) continue;

                    var itemName = item.TryGetProperty("name", out var n) ? n.GetString() ?? "?" : "?";
                    var tier = item.TryGetProperty("tier", out var t) ? t.GetInt32() : 0;
                    snapshot.EquippedTools.Add(tier > 0 ? $"{itemName} (T{tier})" : itemName);
                }
        }

        return snapshot;
    }
}
