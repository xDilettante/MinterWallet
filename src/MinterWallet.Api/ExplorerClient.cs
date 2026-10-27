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
            records.Add(new TxRecord(t.GetProperty("hash").GetString() ?? "", t.GetProperty("height").GetInt64(), 0, "", Convert.FromHexString(raw), new Dictionary<string, string>()));
        }
        var meta = root.GetProperty("meta");
        return (records, meta.GetProperty("current_page").GetInt32() < meta.GetProperty("last_page").GetInt32());
    }

    public void Dispose() => http.Dispose();
}
