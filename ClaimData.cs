using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;

namespace BitCraftOverlay;

public class ArmorPiece
{
    public string ItemName { get; set; } = "";
    public int Tier { get; set; }
    public string RarityStr { get; set; } = "";
}

/// <summary>One saved (or, if the player never saved any, their current) armor loadout.</summary>
public class PresetArmor
{
    public int Index { get; set; }
    public bool Active { get; set; }
    public Dictionary<string, ArmorPiece> BySlot { get; set; } = new(); // key: "Cap"/"Shirt"/"Gloves"/"Belt"/"Leggings/Shorts"/"Boots"
}

/// <summary>
/// Everything equipped for one skill: the permanent Toolbelt tool (always worn, predates the
/// preset system), plus the newer "*_instrument" and "*_charm" equipment slots.
/// </summary>
public class SkillGear
{
    public ArmorPiece? Tool { get; set; }
    public ArmorPiece? Instrument { get; set; }
    public ArmorPiece? Charm { get; set; }
}

/// <summary>One claim member: their skill levels, per-skill gear, and up to 3 armor presets.</summary>
public class ClaimMemberInfo
{
    public string EntityId { get; set; } = "";
    public string UserName { get; set; } = "";
    public string LastLoginRaw { get; set; } = ""; // raw API timestamp string, formatted for display in the UI
    public Dictionary<string, int> SkillLevels { get; set; } = new(); // skill name -> level, one entry per ClaimInfo.SkillNames (0 if not learned)
    public Dictionary<string, SkillGear> GearBySkill { get; set; } = new(); // skill name -> tool/charm, one entry per ClaimApi.ToolSkillNames (both null if none)
    public List<PresetArmor> ArmorPresets { get; set; } = new(); // up to 3, in-game preset order

    public string LastSeenDisplay => DateTimeOffset.TryParse(LastLoginRaw, out var dt) ? dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : LastLoginRaw;
}

public class ClaimInfo
{
    public string EntityId { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> SkillNames { get; set; } = new(); // every skill the API reported, ordered per SkillDisplayOrder - the member table's columns
    public List<ClaimMemberInfo> Members { get; set; } = new();
}

/// <summary>
/// Thin client for bitjita.com's public claim/settlement API. Same caveat as
/// BitjitaApi: field shapes reverse-engineered from live responses, not the docs.
/// </summary>
public static class ClaimApi
{
    private static readonly HttpClient Http = new();

    static ClaimApi() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("BitCraftOverlay");

    private static readonly Dictionary<string, string> ArmorSlotLabels = new()
    {
        ["head_clothing"] = "Cap",
        ["torso_clothing"] = "Shirt",
        ["hand_clothing"] = "Gloves",
        ["belt_clothing"] = "Belt",
        ["leg_clothing"] = "Leggings/Shorts",
        ["feet_clothing"] = "Boots",
    };

    // Display order for the Armor grid's columns.
    public static readonly string[] ArmorColumnOrder = { "Cap", "Shirt", "Belt", "Leggings/Shorts", "Boots", "Gloves" };

    // Explicit column order requested by the user - gathering/crafting professions
    // (which actually have a "*_tool"/"*_charm" slot) first, then the rest.
    public static readonly string[] SkillDisplayOrder =
    {
        "Carpentry", "Farming", "Fishing", "Foraging", "Forestry", "Hunting", "Leatherworking",
        "Masonry", "Mining", "Scholar", "Smithing", "Tailoring",
        "Construction", "Cooking", "Merchanting", "Sailing", "Slayer", "Taming",
    };

    // Skills confirmed (live) to have their own Toolbelt tool - the other 6 (Construction,
    // Cooking, Merchanting, Sailing, Slayer, Taming) are left out of the Tools tab's columns.
    public static readonly string[] ToolSkillNames =
    {
        "Carpentry", "Farming", "Fishing", "Foraging", "Forestry", "Hunting",
        "Leatherworking", "Masonry", "Mining", "Scholar", "Smithing", "Tailoring",
    };

    public static async Task<(string EntityId, string Name)?> FindClaimAsync(string name)
    {
        var json = await Http.GetStringAsync($"https://bitjita.com/api/claims?q={Uri.EscapeDataString(name)}&limit=5");
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("claims", out var claims)) return null;

        JsonElement? best = null;
        foreach (var c in claims.EnumerateArray())
        {
            if (string.Equals(c.GetProperty("name").GetString(), name, StringComparison.OrdinalIgnoreCase))
            {
                best = c;
                break;
            }
            best ??= c;
        }
        if (best is null) return null;
        return (best.Value.GetProperty("entityId").GetString()!, best.Value.GetProperty("name").GetString()!);
    }

    public static async Task<ClaimInfo> LoadClaimAsync(string entityId, string claimName)
    {
        var info = new ClaimInfo { EntityId = entityId, Name = claimName };

        var lastLoginByPlayer = new Dictionary<string, string>();
        var membersJson = await Http.GetStringAsync($"https://bitjita.com/api/claims/{entityId}/members");
        using (var doc = JsonDocument.Parse(membersJson))
            if (doc.RootElement.TryGetProperty("members", out var members))
                foreach (var m in members.EnumerateArray())
                    lastLoginByPlayer[m.GetProperty("playerEntityId").GetString()!] =
                        m.TryGetProperty("lastLoginTimestamp", out var t) ? t.GetString() ?? "" : "";

        var skillNames = new Dictionary<string, string>();
        var citizens = new List<(string EntityId, string UserName, Dictionary<string, int> Skills)>();
        var citizensJson = await Http.GetStringAsync($"https://bitjita.com/api/claims/{entityId}/citizens");
        using (var doc = JsonDocument.Parse(citizensJson))
        {
            var root = doc.RootElement;
            if (root.TryGetProperty("skillNames", out var sn))
                foreach (var kv in sn.EnumerateObject())
                    skillNames[kv.Name] = kv.Value.GetString() ?? kv.Name;

            if (root.TryGetProperty("citizens", out var cits))
                foreach (var c in cits.EnumerateArray())
                {
                    var skills = new Dictionary<string, int>();
                    if (c.TryGetProperty("skills", out var sk))
                        foreach (var kv in sk.EnumerateObject())
                            skills[kv.Name] = kv.Value.GetInt32();
                    citizens.Add((c.GetProperty("entityId").GetString()!, c.GetProperty("userName").GetString() ?? "", skills));
                }
        }

        info.SkillNames = skillNames.Values.Distinct()
            .OrderBy(s => { var i = Array.IndexOf(SkillDisplayOrder, s); return i < 0 ? int.MaxValue : i; })
            .ThenBy(s => s, StringComparer.OrdinalIgnoreCase) // any skill the game adds later just tacks on alphabetically
            .ToList();

        // Toolbelt items only give a bare itemId - resolved against the item catalog
        // (/api/items/{id}), cached across the whole claim since many members carry the same
        // tool. toolStats.skillName on the resolved item says which skill it's for. Caches the
        // in-flight Task (not just the result) so concurrent members requesting the same
        // not-yet-cached item share one HTTP call instead of firing duplicates.
        var toolItemCache = new ConcurrentDictionary<string, Task<(ArmorPiece Piece, string Skill)?>>();
        Task<(ArmorPiece Piece, string Skill)?> ResolveToolItem(string itemId) =>
            toolItemCache.GetOrAdd(itemId, async id =>
            {
                try
                {
                    var itemJson = await Http.GetStringAsync($"https://bitjita.com/api/items/{id}");
                    using var doc = JsonDocument.Parse(itemJson);
                    if (doc.RootElement.TryGetProperty("toolStats", out var toolStats) && toolStats.ValueKind == JsonValueKind.Object
                        && toolStats.TryGetProperty("skillName", out var skillNameProp))
                    {
                        var itemEl = doc.RootElement.GetProperty("item");
                        var piece = new ArmorPiece
                        {
                            ItemName = itemEl.TryGetProperty("name", out var n) ? n.GetString() ?? "?" : "?",
                            Tier = itemEl.TryGetProperty("tier", out var t) ? t.GetInt32() : 0,
                            RarityStr = itemEl.TryGetProperty("rarityStr", out var r) ? r.GetString() ?? "" : "",
                        };
                        return (piece, skillNameProp.GetString() ?? "");
                    }
                }
                catch
                {
                    // not a recognized tool (weapon, generic item, etc.) - null result, cached anyway so it's not re-fetched
                }
                return ((ArmorPiece Piece, string Skill)?)null;
            });

        // /equipment: what's currently worn/in-hand, plus the "*_instrument"/"*_charm" gear.
        async Task<(Dictionary<string, ArmorPiece> CurrentArmor, Dictionary<string, ArmorPiece> InstrumentBySkill, Dictionary<string, ArmorPiece> CharmBySkill)>
            FetchEquipmentAsync(string id)
        {
            var currentArmor = new Dictionary<string, ArmorPiece>();
            var instrumentBySkill = new Dictionary<string, ArmorPiece>();
            var charmBySkill = new Dictionary<string, ArmorPiece>();
            try
            {
                var equipJson = await Http.GetStringAsync($"https://bitjita.com/api/players/{id}/equipment");
                using var doc = JsonDocument.Parse(equipJson);
                if (doc.RootElement.TryGetProperty("equipment", out var equipment))
                    foreach (var slot in equipment.EnumerateArray())
                    {
                        if (!slot.TryGetProperty("primary", out var primaryProp)) continue;
                        var primary = primaryProp.GetString() ?? "";
                        if (!slot.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object) continue;

                        // main_hand/off_hand isn't captured: not tied to any one skill, and
                        // testing showed it's basically always empty (reflects whatever's
                        // literally in-hand at the moment, not a saved loadout).
                        var suffix = primary.EndsWith("_instrument") ? "_instrument" : primary.EndsWith("_charm") ? "_charm" : null;
                        if (suffix != null)
                        {
                            var slotSkill = primary[..^suffix.Length];
                            var skillName = slotSkill.Length > 0 ? char.ToUpper(slotSkill[0]) + slotSkill[1..] : slotSkill;
                            (suffix == "_instrument" ? instrumentBySkill : charmBySkill)[skillName] = BuildArmorPiece(item);
                        }
                        else if (ArmorSlotLabels.TryGetValue(primary, out var label))
                        {
                            currentArmor[label] = BuildArmorPiece(item);
                        }
                    }
            }
            catch
            {
                // one member's equipment failing to load shouldn't blank the whole claim - just skip their tools/armor
            }
            return (currentArmor, instrumentBySkill, charmBySkill);
        }

        // /equipment/presets: the two presets unlocked after the game added the preset system
        // (index 1, 2), each flagged with whether it's the active one.
        async Task<List<PresetArmor>> FetchPresetsAsync(string id)
        {
            var result = new List<PresetArmor>();
            try
            {
                var presetsJson = await Http.GetStringAsync($"https://bitjita.com/api/players/{id}/equipment/presets");
                using var doc = JsonDocument.Parse(presetsJson);
                if (doc.RootElement.TryGetProperty("presets", out var presets))
                    foreach (var p in presets.EnumerateArray())
                    {
                        var preset = new PresetArmor
                        {
                            Index = p.TryGetProperty("index", out var idx) ? idx.GetInt32() : result.Count + 1,
                            Active = p.TryGetProperty("active", out var act) && act.GetBoolean(),
                        };
                        if (p.TryGetProperty("equipmentSlots", out var slots))
                            foreach (var slot in slots.EnumerateArray())
                                if (slot.TryGetProperty("primary", out var pp) && ArmorSlotLabels.TryGetValue(pp.GetString() ?? "", out var label)
                                    && slot.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object)
                                    preset.BySlot[label] = BuildArmorPiece(item);
                        result.Add(preset);
                    }
            }
            catch
            {
                // ignore - every player still gets preset #0 from FetchEquipmentAsync's currentArmor
            }
            return result;
        }

        // /inventories: the Toolbelt - up to 16 permanently-equipped tools, a separate, older
        // system than *_instrument/*_charm. Bare itemIds only, resolved via ResolveToolItem.
        async Task<List<(string Skill, ArmorPiece Piece)>> FetchToolbeltAsync(string id)
        {
            var result = new List<(string Skill, ArmorPiece Piece)>();
            try
            {
                var invJson = await Http.GetStringAsync($"https://bitjita.com/api/players/{id}/inventories");
                using var doc = JsonDocument.Parse(invJson);
                if (doc.RootElement.TryGetProperty("inventories", out var invs))
                    foreach (var inv in invs.EnumerateArray())
                    {
                        if (!inv.TryGetProperty("inventoryName", out var invName) || invName.GetString() != "Toolbelt") continue;
                        if (inv.TryGetProperty("pockets", out var pockets))
                            foreach (var pocket in pockets.EnumerateArray())
                            {
                                if (!pocket.TryGetProperty("contents", out var contents) || contents.ValueKind != JsonValueKind.Object) continue;
                                if (!contents.TryGetProperty("itemId", out var idProp)) continue;
                                var resolved = await ResolveToolItem(idProp.GetInt64().ToString());
                                if (resolved is { } r) result.Add((r.Skill, r.Piece));
                            }
                        break; // only one Toolbelt per player
                    }
            }
            catch
            {
                // one member's toolbelt failing to load just leaves their Tool column blank
            }
            return result;
        }

        // Up to 3 HTTP calls per member (equipment + presets + inventories, now run concurrently
        // per member too) plus a shared, cached item-catalog lookup per unique tool. Run with
        // bounded concurrency - fully sequential was measured at 800+ seconds for a 20-member
        // claim, which is unusable.
        using var concurrencyLimit = new SemaphoreSlim(20);
        var memberTasks = citizens.Select(async citizen =>
        {
            await concurrencyLimit.WaitAsync();
            try
            {
                return await BuildMemberAsync(citizen.EntityId, citizen.UserName, citizen.Skills);
            }
            finally
            {
                concurrencyLimit.Release();
            }
        });
        info.Members.AddRange(await Task.WhenAll(memberTasks));

        return info;

        async Task<ClaimMemberInfo> BuildMemberAsync(string id, string userName, Dictionary<string, int> skills)
        {
            var member = new ClaimMemberInfo
            {
                EntityId = id,
                UserName = userName,
                LastLoginRaw = lastLoginByPlayer.GetValueOrDefault(id, ""),
                // Every column gets an entry (0/empty for skills the member hasn't touched) so
                // the grid's per-cell indexer bindings never hit a missing dictionary key.
                SkillLevels = info.SkillNames.ToDictionary(n => n, n => 0),
                GearBySkill = ToolSkillNames.ToDictionary(n => n, _ => new SkillGear()),
            };
            foreach (var kv in skills)
                if (skillNames.TryGetValue(kv.Key, out var name))
                    member.SkillLevels[name] = kv.Value;

            // The 3 fetches below are independent (different endpoints, no shared state) - run
            // concurrently and merge after, rather than one-at-a-time per member, which was the
            // other big chunk of the original 800+ second load time.
            var equipmentTask = FetchEquipmentAsync(id);
            var presetsTask = FetchPresetsAsync(id);
            var toolbeltTask = FetchToolbeltAsync(id);
            await Task.WhenAll(equipmentTask, presetsTask, toolbeltTask);

            var (currentArmor, instrumentBySkill, charmBySkill) = equipmentTask.Result;
            foreach (var (skill, piece) in instrumentBySkill)
                if (member.GearBySkill.TryGetValue(skill, out var gear)) gear.Instrument = piece;
            foreach (var (skill, piece) in charmBySkill)
                if (member.GearBySkill.TryGetValue(skill, out var gear)) gear.Charm = piece;

            member.ArmorPresets = presetsTask.Result;

            foreach (var (skill, piece) in toolbeltTask.Result)
                if (member.GearBySkill.TryGetValue(skill, out var gear)) gear.Tool = piece;

            // Preset #0 always exists (it's just whatever /equipment reports) - shown whenever
            // it has at least one item, regardless of whether #1/#2 also exist. /equipment/presets
            // doesn't carry an "active" flag for it, so it's inferred: active only if neither of
            // the unlocked presets claims to be (exactly one preset should ever be active).
            if (currentArmor.Count > 0)
                member.ArmorPresets.Add(new PresetArmor { Index = 0, Active = !member.ArmorPresets.Any(p => p.Active), BySlot = currentArmor });
            member.ArmorPresets = member.ArmorPresets.OrderBy(p => p.Index).ToList(); // API array order isn't guaranteed to match preset index

            return member;
        }
    }

    private static ArmorPiece BuildArmorPiece(JsonElement item) => new()
    {
        ItemName = item.TryGetProperty("name", out var n) ? n.GetString() ?? "?" : "?",
        Tier = item.TryGetProperty("tier", out var t) ? t.GetInt32() : 0,
        RarityStr = item.TryGetProperty("rarityString", out var r) ? r.GetString() ?? "" : "",
    };
}
