using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace BitCraftOverlay;

/// <summary>
/// Client for relay.bitcraftsync.app's public, read-only SpacetimeDB mirror of BitCraft's live
/// game data - a community relay that holds its own Clockwork Labs Developer Token upstream and
/// re-publishes the data with no auth required for read subscriptions (confirmed straight from
/// their own tutorial, https://relay.bitcraftsync.app/tutorial/connect.html, including a
/// runnable v1.json Python example this implementation mirrors exactly). This is NOT a
/// connection to BitCraft's own server, needs no token of ours, and touches nothing on the
/// game client - it's the same kind of public API this app already calls for bitjita.com etc.
/// // ponytail: connect-subscribe-collect-disconnect per call, not a persistent connection -
/// the Route tab only needs one fresh snapshot per "Compute route" click, not live tracking.
/// </summary>
public static class SpacetimeClient
{
    /// <summary>Connects to the relay mirror for the given BitCraft region (the real map-grid
    /// region number, e.g. 14 - see bitcraftoverlay-coordinate-system memory, NOT the wrong 1-9
    /// guess used earlier), subscribes to the given SQL query strings, and returns the rows from
    /// the InitialSubscription snapshot keyed by table name.</summary>
    public static async Task<Dictionary<string, List<JsonElement>>> FetchSnapshotAsync(int region, string[] queryStrings, TimeSpan timeout, CancellationToken ct = default)
    {
        // Database name per relay.bitcraftsync.app/health (authoritative, live) is
        // "bitcraft-live-{region}" - the tutorial page's "relay-mirror-bcN" example is stale.
        var uri = new Uri($"wss://relay.bitcraftsync.app:{3000 + region}/v1/database/bitcraft-live-{region}/subscribe?compression=None");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        using var ws = new ClientWebSocket();
        ws.Options.AddSubProtocol("v1.json.spacetimedb");
        await ws.ConnectAsync(uri, timeoutCts.Token);

        // The relay's greeting (tagged "IdentityToken" on the v1 wire) MUST be read first -
        // sending Subscribe before it arrives gets the connection dropped.
        var greeting = await ReceiveJsonAsync(ws, timeoutCts.Token);
        if (!greeting.TryGetProperty("IdentityToken", out _))
            throw new InvalidOperationException($"Expected IdentityToken greeting, got: {Truncate(greeting)}");

        var subscribe = JsonSerializer.Serialize(new { Subscribe = new { request_id = 1, query_strings = queryStrings } });
        await ws.SendAsync(Encoding.UTF8.GetBytes(subscribe), WebSocketMessageType.Text, true, timeoutCts.Token);

        var applied = await ReceiveJsonAsync(ws, timeoutCts.Token);
        if (!applied.TryGetProperty("InitialSubscription", out var initial))
            throw new InvalidOperationException($"Expected InitialSubscription, got: {Truncate(applied)}");

        var result = new Dictionary<string, List<JsonElement>>();
        foreach (var table in initial.GetProperty("database_update").GetProperty("tables").EnumerateArray())
        {
            var tableName = table.GetProperty("table_name").GetString()!;
            var rows = new List<JsonElement>();
            foreach (var update in table.GetProperty("updates").EnumerateArray())
            {
                if (!update.TryGetProperty("inserts", out var inserts)) continue;
                foreach (var raw in inserts.EnumerateArray())
                {
                    // Each insert is itself a JSON string encoding the row object.
                    var rowJson = raw.ValueKind == JsonValueKind.String ? raw.GetString()! : raw.GetRawText();
                    rows.Add(JsonDocument.Parse(rowJson).RootElement);
                }
            }
            result[tableName] = rows;
        }

        try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); } catch { /* best effort */ }
        return result;
    }

    private static async Task<JsonElement> ReceiveJsonAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException($"Server closed: {ws.CloseStatus} {ws.CloseStatusDescription}");
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        return JsonDocument.Parse(ms.ToArray()).RootElement;
    }

    private static string Truncate(JsonElement e)
    {
        var s = e.ToString();
        return s.Length > 300 ? s[..300] + "…" : s;
    }

    /// <summary>Live BitCraft regions the relay currently mirrors, from its own /health endpoint
    /// - the relay's tutorial says to prefer this over hard-coding region numbers since the
    /// fleet changes over time.</summary>
    public static async Task<List<int>> GetLiveRegionsAsync(CancellationToken ct = default)
    {
        using var http = new HttpClient();
        var json = await http.GetStringAsync("https://relay.bitcraftsync.app/health", ct);
        using var doc = JsonDocument.Parse(json);

        var regions = new List<int>();
        foreach (var src in doc.RootElement.GetProperty("sources").EnumerateObject())
        {
            const string prefix = "bitcraft-live-";
            if (src.Name.StartsWith(prefix) && int.TryParse(src.Name.AsSpan(prefix.Length), out var region))
                regions.Add(region);
        }
        return regions;
    }

    /// <summary>Finds a player's live position by probing regions (the cached region first, if
    /// given) until one returns a mobile_entity_state row for that entity id. Returns null if
    /// the player isn't found live anywhere - offline, or the relay is unreachable - callers
    /// should fall back to manual entry in that case.
    /// // ponytail: location_x/location_z are the small-hex world coordinates from
    /// TerrainMap/RouteMapRenderer at 1000x fixed-point scale - confirmed empirically against a
    /// known real position (see bitcraftoverlay-route-spacetimedb memory), not from documentation.</summary>
    public static async Task<(int Region, double WorldX, double WorldZ, bool IsWalking)?> FindPlayerPositionAsync(
        string entityId, int? cachedRegion, CancellationToken ct = default)
    {
        var regions = await GetLiveRegionsAsync(ct);
        if (cachedRegion is { } cached && regions.Remove(cached))
            regions.Insert(0, cached);

        var queries = new[] { $"SELECT * FROM mobile_entity_state WHERE entity_id = {entityId}" };
        foreach (var region in regions)
        {
            try
            {
                var snapshot = await FetchSnapshotAsync(region, queries, TimeSpan.FromSeconds(5), ct);
                if (snapshot.TryGetValue("mobile_entity_state", out var rows) && rows.Count > 0)
                {
                    var row = rows[0];
                    var x = row.GetProperty("location_x").GetDouble() / 1000.0;
                    var z = row.GetProperty("location_z").GetDouble() / 1000.0;
                    var walking = row.TryGetProperty("is_walking", out var w) && w.GetBoolean();
                    return (region, x, z, walking);
                }
            }
            catch
            {
                // This region is unreachable/timed out - try the next one.
            }
        }
        return null;
    }
}
