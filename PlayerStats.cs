using System.Collections.Concurrent;
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

/// <summary>One row of a BitjitaApi.SearchPlayersAsync result - a plain record (not a tuple) so
/// it binds cleanly to a WPF ListBox's DisplayMemberPath.</summary>
public record PlayerSearchResult(string EntityId, string Username);

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

    /// <summary>Every player matching the search text, for a pick-one-from-a-list UI (unlike
    /// FindPlayerAsync, which collapses to a single best guess).</summary>
    public static async Task<List<PlayerSearchResult>> SearchPlayersAsync(string name)
    {
        var json = await Http.GetStringAsync($"https://bitjita.com/api/players?q={Uri.EscapeDataString(name)}");
        using var doc = JsonDocument.Parse(json);
        var result = new List<PlayerSearchResult>();
        if (!doc.RootElement.TryGetProperty("players", out var players)) return result;
        foreach (var p in players.EnumerateArray())
            result.Add(new PlayerSearchResult(p.GetProperty("entityId").GetString()!, p.GetProperty("username").GetString()!));
        return result;
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

        // /inventories only gives bare itemId+quantity refs, no name - resolved below against
        // the item/cargo catalog (same technique as the Claim tab's Toolbelt resolution).
        var rawCounts = new Dictionary<string, long>(); // itemId -> total quantity
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
                        rawCounts[id] = rawCounts.GetValueOrDefault(id) + qty;
                    }
                }
        }

        // One catalog lookup per unique item (cached), tried as a plain item first and a
        // cargo second since the two live under separate endpoints.
        var itemNameCache = new ConcurrentDictionary<string, Task<string>>();
        Task<string> ResolveItemName(string itemId) => itemNameCache.GetOrAdd(itemId, async id =>
        {
            foreach (var url in new[] { $"https://bitjita.com/api/items/{id}", $"https://bitjita.com/api/cargo/{id}" })
            {
                try
                {
                    var json = await Http.GetStringAsync(url);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    var name = root.TryGetProperty("item", out var itemEl) && itemEl.TryGetProperty("name", out var n1) ? n1.GetString()
                        : root.TryGetProperty("cargo", out var cargoEl) && cargoEl.TryGetProperty("name", out var n2) ? n2.GetString()
                        : root.TryGetProperty("name", out var n3) ? n3.GetString()
                        : null;
                    if (name != null) return name;
                }
                catch
                {
                    // not found at this endpoint - try the next one, or fall back to the id below
                }
            }
            return $"Item {id}";
        });

        using (var concurrencyLimit = new SemaphoreSlim(30))
        {
            var resolveTasks = rawCounts.Keys.Select(async id =>
            {
                await concurrencyLimit.WaitAsync();
                try { return (Id: id, Name: await ResolveItemName(id)); }
                finally { concurrencyLimit.Release(); }
            });
            foreach (var (id, name) in await Task.WhenAll(resolveTasks))
                snapshot.Items[name] = snapshot.Items.GetValueOrDefault(name) + rawCounts[id];
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
