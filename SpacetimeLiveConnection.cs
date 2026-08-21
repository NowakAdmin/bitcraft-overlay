using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace BitCraftOverlay;

/// <summary>
/// A persistent, subscribed connection to relay.bitcraftsync.app's public SpacetimeDB mirror
/// (see SpacetimeClient.cs for the one-shot version and the connection details/wire format -
/// same host/port/protocol, no auth). Call ConnectAsync once, then SubscribeAsync any time the
/// desired query set changes - each call REPLACES the whole set, that's how SpacetimeDB's v1
/// Subscribe message works (not additive). RowsChanged fires for every insert/delete batch, both
/// for the fresh snapshot right after a Subscribe call and for every live TransactionUpdate
/// afterward - callers don't need to treat the first batch specially.
/// // ponytail: small duplication of SpacetimeClient's receive/row-parsing helpers rather than
/// sharing a base - the one-shot (request→response) and live (event-stream) shapes don't share
/// much plumbing beyond those few lines.
/// Events fire on the background receive-loop thread; UI callers must marshal back to the UI
/// thread themselves (Dispatcher.Invoke).
/// </summary>
public sealed class SpacetimeLiveConnection : IAsyncDisposable
{
    /// <summary>(table name, inserted rows, deleted rows).</summary>
    public event Action<string, List<JsonElement>, List<JsonElement>>? RowsChanged;
    public event Action<Exception>? Disconnected;
    /// <summary>The server rejected a query (bad SQL, unsupported literal, etc.) - this is a
    /// normal TransactionUpdate with status.Failed, not a connection error, so it doesn't touch
    /// Disconnected. Confirmed via a live test: e.g. filtering a SATS sum-type column by a bare
    /// literal fails this way rather than throwing at the WebSocket layer.</summary>
    public event Action<string>? QueryFailed;

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;
    private int _requestId;

    public async Task ConnectAsync(int region, CancellationToken ct = default)
    {
        var uri = new Uri($"wss://relay.bitcraftsync.app:{3000 + region}/v1/database/bitcraft-live-{region}/subscribe?compression=None");
        _ws = new ClientWebSocket();
        _ws.Options.AddSubProtocol("v1.json.spacetimedb");
        await _ws.ConnectAsync(uri, ct);

        // Greeting must be read before subscribing - see bitcraftoverlay-route-spacetimedb memory.
        var greeting = await ReceiveJsonAsync(_ws, ct);
        if (!greeting.TryGetProperty("IdentityToken", out _))
            throw new InvalidOperationException("Expected IdentityToken greeting from relay.");

        _cts = new CancellationTokenSource();
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
    }

    /// <summary>Replaces the entire subscribed query set. The relay answers with a fresh
    /// InitialSubscription (delivered via RowsChanged, same as any later TransactionUpdate) for
    /// exactly this new set - rows from a previous, now-dropped query won't be re-announced as
    /// deleted, so callers should clear their own caches for anything the new set no longer
    /// covers before calling this.</summary>
    public async Task SubscribeAsync(string[] queryStrings, CancellationToken ct = default)
    {
        if (_ws is null) throw new InvalidOperationException("Not connected.");
        var msg = JsonSerializer.Serialize(new { Subscribe = new { request_id = Interlocked.Increment(ref _requestId), query_strings = queryStrings } });
        await _ws.SendAsync(Encoding.UTF8.GetBytes(msg), WebSocketMessageType.Text, true, ct);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var msg = await ReceiveJsonAsync(_ws!, ct);
                if (msg.TryGetProperty("InitialSubscription", out var initial) && initial.TryGetProperty("database_update", out var initialUpdate))
                    Dispatch(initialUpdate);
                else if (msg.TryGetProperty("TransactionUpdate", out var tx) && tx.TryGetProperty("status", out var status))
                {
                    if (status.TryGetProperty("Committed", out var committed))
                        Dispatch(committed);
                    else if (status.TryGetProperty("Failed", out var failedMsg))
                        QueryFailed?.Invoke(failedMsg.GetString() ?? "(no message)");
                }
                // IdentityToken (shouldn't recur), TransactionUpdateLight, other payloads:
                // ignored - this app only needs insert/delete row batches and query failures.
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown via DisposeAsync.
        }
        catch (Exception ex)
        {
            Disconnected?.Invoke(ex);
        }
    }

    private void Dispatch(JsonElement databaseUpdate)
    {
        if (!databaseUpdate.TryGetProperty("tables", out var tables)) return;
        foreach (var table in tables.EnumerateArray())
        {
            var name = table.GetProperty("table_name").GetString()!;
            var inserts = new List<JsonElement>();
            var deletes = new List<JsonElement>();
            foreach (var upd in table.GetProperty("updates").EnumerateArray())
            {
                if (upd.TryGetProperty("inserts", out var ins))
                    foreach (var raw in ins.EnumerateArray()) inserts.Add(ParseRow(raw));
                if (upd.TryGetProperty("deletes", out var del))
                    foreach (var raw in del.EnumerateArray()) deletes.Add(ParseRow(raw));
            }
            if (inserts.Count > 0 || deletes.Count > 0)
                RowsChanged?.Invoke(name, inserts, deletes);
        }
    }

    private static JsonElement ParseRow(JsonElement raw) =>
        JsonDocument.Parse(raw.ValueKind == JsonValueKind.String ? raw.GetString()! : raw.GetRawText()).RootElement;

    /// <summary>Known column order per table - used only to read array-shaped TransactionUpdate
    /// rows. InitialSubscription always encodes rows as named-field JSON objects, but a live
    /// TransactionUpdate encodes each row as a positional JSON array in table-schema column
    /// order instead - confirmed empirically via a live capture (not documented anywhere this
    /// session found). Add a table here the first time a caller needs one of its live fields.</summary>
    public static readonly Dictionary<string, string[]> TableColumns = new()
    {
        ["mobile_entity_state"] = new[] { "entity_id", "chunk_index", "timestamp", "location_x", "location_z", "destination_x", "destination_z", "dimension", "is_walking", "_pad1", "_pad2", "_pad3" },
        ["resource_state"] = new[] { "entity_id", "resource_id", "direction_index" },
        ["location_state"] = new[] { "entity_id", "chunk_index", "x", "z", "dimension" },
        ["enemy_mob_monitor_state"] = new[] { "entity_id", "enemy_type", "herd_entity_id", "herd_location" },
    };

    /// <summary>Column order for herd_location, a nested Product (not a top-level table) inside
    /// enemy_mob_monitor_state rows - confirmed from the schema's typespace. Same object/array
    /// duality applies to nested Products as to row shapes, so this needs the same GetNestedField
    /// treatment rather than a plain GetProperty.</summary>
    public static readonly string[] HerdLocationColumns = { "x", "z", "dimension" };

    /// <summary>Reads one field from a row regardless of whether it came from InitialSubscription
    /// (named-field object) or a live TransactionUpdate (positional array) - see TableColumns.</summary>
    public static JsonElement GetField(JsonElement row, string table, string field) =>
        row.ValueKind == JsonValueKind.Object
            ? row.GetProperty(field)
            : row[Array.IndexOf(TableColumns[table], field)];

    /// <summary>Same idea as GetField but for a nested Product value (e.g. herd_location) rather
    /// than a top-level table row - the column list comes from the caller instead of TableColumns
    /// since nested types aren't tables. Sum types (like enemy_type) don't need this: they're
    /// always encoded as a [tagIndex, payload] array regardless of context, confirmed empirically
    /// (unlike Products, which flip between named-object and positional-array).</summary>
    public static JsonElement GetNestedField(JsonElement value, string[] columns, string field) =>
        value.ValueKind == JsonValueKind.Object
            ? value.GetProperty(field)
            : value[Array.IndexOf(columns, field)];

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

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_ws is { State: WebSocketState.Open })
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); } catch { /* best effort */ }
        }
        _ws?.Dispose();
        if (_receiveLoop is not null)
        {
            try { await _receiveLoop; } catch { /* already logged via Disconnected if unexpected */ }
        }
    }
}
