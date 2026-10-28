using System.Globalization;
using System.Net;
using System.Text.Json;
using Minter.Protocol;
using MinterWallet.Core;

namespace MinterWallet.Api;

// The Minter explorer (explorer-api.minter.network, mainnet only): the history of an address older than a MinterGate's
// index. Like a source, it is not checked; the wallet decodes each transaction from its raw bytes. It lists
// transactions that were executed, so each comes with code 0.
public sealed class ExplorerClient : IDisposable
{
    public static readonly Uri Mainnet = new("https://explorer-api.minter.network/api/v2/");
    private readonly HttpClient http;

    public ExplorerClient(Uri? baseUri = null, HttpMessageHandler? handler = null)
    {
        handler ??= new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2), AutomaticDecompression = DecompressionMethods.All };
        http = new HttpClient(handler) { BaseAddress = baseUri ?? Mainnet, Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 8 * 1024 * 1024 };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MinterWallet");
    }

    // Names validators gave themselves (mainnet), by public key.
    public async Task<IReadOnlyDictionary<string, string>> ValidatorNamesAsync(CancellationToken ct = default)
    {
        using var reply = await http.GetAsync("validators", ct);
        reply.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await reply.Content.ReadAsStringAsync(ct));
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in doc.RootElement.GetProperty("data").EnumerateArray())
            if (v.GetProperty("public_key").GetString() is { } key && v.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } name) names[key] = name.Trim();
        return names;
    }

    // The latest transactions at the bridge's Minter address (newest first): withdrawals and the bridge's multisends.
    public async Task<IReadOnlyList<BridgeTx>> BridgeActivityAsync(int pages = 2, CancellationToken ct = default)
    {
        var result = new List<BridgeTx>();
        for (var page = 1; page <= pages; page++)
        {
            using var reply = await http.GetAsync($"addresses/{Bridge.Multisig}/transactions?page={page.ToString(CultureInfo.InvariantCulture)}", ct);
            reply.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await reply.Content.ReadAsStringAsync(ct));
            foreach (var t in doc.RootElement.GetProperty("data").EnumerateArray())
            {
                var type = t.GetProperty("type").GetInt32();
                if (type is not ((int)TxType.Send or (int)TxType.Multisend)) continue;
                var data = t.GetProperty("data");
                var payload = t.TryGetProperty("payload", out var p) && p.GetString() is { Length: > 0 } b64 ? DecodePayload(b64) : "";
                var outs = type == (int)TxType.Multisend
                    ? data.GetProperty("list").EnumerateArray().Select(o => new BridgeOut(Address.Parse(o.GetProperty("to").GetString()!), o.GetProperty("coin").GetProperty("id").GetUInt32(),
                        Units.ParseCoins(o.GetProperty("value").GetString()!))).ToList()
                    : [];
                result.Add(new BridgeTx(t.GetProperty("hash").GetString() ?? "", t.GetProperty("height").GetInt64(),
                    DateTimeOffset.Parse(t.GetProperty("timestamp").GetString()!, CultureInfo.InvariantCulture), type, Address.Parse(t.GetProperty("from").GetString()!),
                    type == (int)TxType.Send ? data.GetProperty("coin").GetProperty("id").GetUInt32() : 0,
                    type == (int)TxType.Send ? Units.ParseCoins(data.GetProperty("value").GetString()!) : 0, payload, outs));
            }
        }
        return result;
    }

    private static string DecodePayload(string base64)
    {
        try { return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64)); }
        catch (FormatException) { return ""; }
    }

    // One page (50 transactions, newest first) and whether more pages follow.
    public async Task<(IReadOnlyList<TxRecord> Records, bool More)> HistoryAsync(Address address, int page, CancellationToken ct = default)
    {
        using var reply = await http.GetAsync($"addresses/{address}/transactions?page={page.ToString(CultureInfo.InvariantCulture)}", ct);
        reply.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await reply.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var records = new List<TxRecord>();
        foreach (var t in root.GetProperty("data").EnumerateArray())
        {
            if (t.GetProperty("raw_tx").GetString() is not { Length: > 0 } raw) continue;
            var tags = new Dictionary<string, string>();
            if (t.TryGetProperty("timestamp", out var time) && time.GetString() is { Length: > 0 } at) tags["timestamp"] = at;
            records.Add(new TxRecord(t.GetProperty("hash").GetString() ?? "", t.GetProperty("height").GetInt64(), 0, "", Convert.FromHexString(raw), tags));
        }
        var meta = root.GetProperty("meta");
        return (records, meta.GetProperty("current_page").GetInt32() < meta.GetProperty("last_page").GetInt32());
    }

    public void Dispose() => http.Dispose();
}
