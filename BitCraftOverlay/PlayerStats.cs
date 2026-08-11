using System.Net.Http;
using System.Text.Json;

namespace BitCraftOverlay;

/// <summary>
/// A full player-state capture at one point in time: XP per skill, item quantities
/// across every container the player owns, placeable count, and the power of
/// whatever tool is currently equipped for each skill (so a skill's XP gain and
/// the gear it was earned with are tied together in one snapshot).
/// </summary>
public class StatSnapshot
{
    public long TimestampUnix { get; set; }
    public Dictionary<string, long> SkillXp { get; set; } = new();
    public Dictionary<string, long> Items { get; set; } = new();
    public int PlaceableCount { get; set; }
    public Dictionary<string, int> ToolPowerBySkill { get; set; } = new();
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
        var skillNames = new Dictionary<string, string>(); // skillId -> name, also used to resolve tool power by skill

        var playerJson = await Http.GetStringAsync($"https://bitjita.com/api/players/{entityId}");
        using (var doc = JsonDocument.Parse(playerJson))
        {
            var player = doc.RootElement.GetProperty("player");
            snapshot.PlaceableCount = player.TryGetProperty("placeableCount", out var pc) ? pc.GetInt32() : 0;

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

        // The item catalog (name + tool stats per item id) only comes back from the
        // inventories endpoint - fetch it once and reuse it to resolve both the
        // player's items and, by cross-referencing equipped item ids, tool power per skill.
        var itemNames = new Dictionary<string, string>();
        var toolPowerByItemId = new Dictionary<string, (int Power, string SkillId)>();

        var invJson = await Http.GetStringAsync($"https://bitjita.com/api/players/{entityId}/inventories");
        using (var doc = JsonDocument.Parse(invJson))
        {
            var root = doc.RootElement;
            foreach (var dictName in new[] { "items", "cargos" })
            {
                if (!root.TryGetProperty(dictName, out var d)) continue;
                foreach (var kv in d.EnumerateObject())
                {
                    itemNames[kv.Name] = kv.Value.TryGetProperty("name", out var n) ? n.GetString() ?? kv.Name : kv.Name;
                    if (kv.Value.TryGetProperty("toolPower", out var tp) && kv.Value.TryGetProperty("toolSkillId", out var tsid))
                        toolPowerByItemId[kv.Name] = (tp.GetInt32(), tsid.GetInt32().ToString());
                }
            }

            if (root.TryGetProperty("inventories", out var invs))
                foreach (var inv in invs.EnumerateArray())
                {
                    if (!inv.TryGetProperty("pockets", out var pockets)) continue;
                    foreach (var pocket in pockets.EnumerateArray())
                    {
                        if (!pocket.TryGetProperty("contents", out var contents) || contents.ValueKind != JsonValueKind.Object) continue;
                        if (!contents.TryGetProperty("itemId", out var idProp)) continue;
                        var id = idProp.GetInt64().ToString();
                        var qty = contents.TryGetProperty("quantity", out var q) ? q.GetInt64() : 0;
                        var name = itemNames.TryGetValue(id, out var n) ? n : $"Item {id}";
                        snapshot.Items[name] = snapshot.Items.GetValueOrDefault(name) + qty;
                    }
                }
        }

        var equipJson = await Http.GetStringAsync($"https://bitjita.com/api/players/{entityId}/equipment");
        using (var doc = JsonDocument.Parse(equipJson))
        {
            if (doc.RootElement.TryGetProperty("equipment", out var equipment))
                foreach (var slot in equipment.EnumerateArray())
                {
                    if (!slot.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object) continue;
                    if (!item.TryGetProperty("id", out var idProp)) continue;
                    var id = idProp.GetInt64().ToString();
                    if (!toolPowerByItemId.TryGetValue(id, out var tool)) continue;
                    var skillName = skillNames.TryGetValue(tool.SkillId, out var n) ? n : $"Skill {tool.SkillId}";
                    snapshot.ToolPowerBySkill[skillName] = tool.Power;
                }
        }

        return snapshot;
    }
}
