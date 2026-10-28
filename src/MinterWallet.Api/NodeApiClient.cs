using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Minter.Protocol;
using MinterWallet.Core;

namespace MinterWallet.Api;

// API v2 of minter-go-node (grpc-gateway JSON: 64-bit numbers and amounts as strings, snake_case, errors as
// {"error":{"code","message","data"}}). Paths and fields checked against node-grpc-gateway api.proto and a v3.3.0 node.
public sealed class NodeApiClient : IChainBackend, IDisposable
{
    private readonly HttpClient http;
    public string Name { get; }
    public SourceTrust Trust => SourceTrust.Node;
    public Uri BaseUri { get; }

    // skipCertificateCheck: any TLS certificate is accepted (the user's choice for one source, to tell a certificate
    // problem from others); the answers can then come from anyone in between.
    public NodeApiClient(Uri baseUri, string? name = null, HttpMessageHandler? handler = null, bool skipCertificateCheck = false)
    {
        var text = baseUri.AbsoluteUri.TrimEnd('/');
        if (!text.EndsWith("/v2", StringComparison.Ordinal)) text += "/v2";
        BaseUri = new Uri(text + "/");
        Name = name ?? $"Нода {baseUri.Authority}";
        // A node on this machine or in the local network is reached directly, not through a system proxy.
        handler ??= new SocketsHttpHandler
        {
            UseProxy = !IsLocal(baseUri.Host), PooledConnectionLifetime = TimeSpan.FromMinutes(2), AutomaticDecompression = DecompressionMethods.All,
            SslOptions = skipCertificateCheck ? new System.Net.Security.SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, _, _, _) => true } : new(),
        };
        http = new HttpClient(handler) { BaseAddress = BaseUri, Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 32 * 1024 * 1024 };
    }

    public void Dispose() => http.Dispose();

    public async Task<ChainStatus> StatusAsync(CancellationToken ct = default)
    {
        var j = await Get("status", ct);
        return new ChainStatus(Str(j, "network"), Str(j, "version"), (long)U64(j, "latest_block_height"),
            DateTimeOffset.Parse(Str(j, "latest_block_time"), CultureInfo.InvariantCulture));
    }

    public async Task<AccountState> AccountAsync(Address address, CancellationToken ct = default)
    {
        var j = await Get($"address/{address}?delegated=true", ct);
        var balances = j.GetProperty("balance").EnumerateArray().Select(b => new Balance(Coin(b.GetProperty("coin")), Big(b, "value"), Big(b, "bip_value"))).ToList();
        var delegated = j.TryGetProperty("delegated", out var d) && d.ValueKind == JsonValueKind.Array
            ? d.EnumerateArray().Select(x => new Delegation(Coin(x.GetProperty("coin")), Big(x, "value"), Big(x, "bip_value"))).ToList() : [];
        MultisigInfo? multisig = null;
        if (j.TryGetProperty("multisig", out var m) && m.ValueKind == JsonValueKind.Object)
        {
            var weights = m.GetProperty("weights").EnumerateArray().Select(w => (uint)ParseU64(w)).ToList();
            var addresses = m.GetProperty("addresses").EnumerateArray().Select(a => Address.Parse(a.GetString()!)).ToList();
            multisig = new MultisigInfo((uint)U64(m, "threshold"), addresses.Zip(weights, (a, w) => new MultisigMember(a, w)).ToList());
        }
        return new AccountState(address, U64(j, "transaction_count"), balances, delegated, Big(j, "bip_value"), multisig, U64(j, "locked_stake_until_block"));
    }

    public async Task<CoinDetails?> CoinAsync(uint id, CancellationToken ct = default) => await CoinOrNull($"coin_info_by_id/{id}", ct);
    public async Task<CoinDetails?> CoinAsync(string symbol, CancellationToken ct = default) => await CoinOrNull($"coin_info/{Uri.EscapeDataString(symbol)}", ct);

    private async Task<CoinDetails?> CoinOrNull(string path, CancellationToken ct)
    {
        JsonElement j;
        try { j = await Get(path, ct); }
        catch (ChainApiException e) when (e.Code is 102 or 404) { return null; }
        var owner = j.TryGetProperty("owner_address", out var o) && o.ValueKind == JsonValueKind.String ? Address.Parse(o.GetString()!) : (Address?)null;
        return new CoinDetails((uint)U64(j, "id"), Str(j, "symbol"), Str(j, "name"), Big(j, "volume"), (uint)U64(j, "crr"), Big(j, "reserve_balance"),
            Big(j, "max_supply"), owner, j.GetProperty("mintable").GetBoolean(), j.GetProperty("burnable").GetBoolean());
    }

    public async Task<PriceCommissions> PricesAsync(CancellationToken ct = default)
    {
        var j = await Get("price_commissions", ct);
        var prices = new PriceCommissions { Coin = (uint)U64(j.GetProperty("coin"), "id") };
        foreach (var field in PriceCommissions.Fields)
            if (j.TryGetProperty(PriceCommissions.ApiName(field), out var value)) prices[field] = ParseBig(value);
        return prices;
    }

    public async Task<uint> MinGasPriceAsync(CancellationToken ct = default) => (uint)U64(await Get("min_gas_price", ct), "min_gas_price");

    public async Task<SwapEstimate> EstimateSellAsync(uint sell, uint buy, BigInteger value, SwapFrom from, IReadOnlyList<uint>? route = null, CancellationToken ct = default)
    {
        var j = await Get($"estimate_coin_sell?coin_id_to_sell={sell}&coin_id_to_buy={buy}&value_to_sell={value}&swap_from={From(from)}{Route(route)}", ct);
        return new SwapEstimate(Big(j, "will_get"), Big(j, "commission"), Str(j, "swap_from"));
    }

    public async Task<BigInteger> EstimateSellAllAsync(uint sell, uint buy, BigInteger balance, SwapFrom from, IReadOnlyList<uint>? route = null, CancellationToken ct = default)
    {
        var j = await Get($"estimate_coin_sell_all?coin_id_to_sell={sell}&coin_id_to_buy={buy}&value_to_sell={balance}&swap_from={From(from)}{Route(route)}", ct);
        return Big(j, "will_get");
    }

    public async Task<SwapEstimate> EstimateBuyAsync(uint sell, uint buy, BigInteger value, SwapFrom from, IReadOnlyList<uint>? route = null, CancellationToken ct = default)
    {
        var j = await Get($"estimate_coin_buy?coin_id_to_sell={sell}&coin_id_to_buy={buy}&value_to_buy={value}&swap_from={From(from)}{Route(route)}", ct);
        return new SwapEstimate(Big(j, "will_pay"), Big(j, "commission"), Str(j, "swap_from"));
    }

    public async Task<BestTrade?> BestTradeAsync(uint sell, uint buy, BigInteger amount, bool exactInput, CancellationToken ct = default)
    {
        JsonElement j;
        try { j = await Get($"best_trade/{sell}/{buy}/{(exactInput ? "input" : "output")}/{amount}?max_depth=4", ct); }
        catch (ChainApiException e) when (e.Code == 404) { return null; }
        return new BestTrade(j.GetProperty("path").EnumerateArray().Select(p => (uint)ParseU64(p)).ToList(), Big(j, "result"));
    }

    public async Task<BigInteger> EstimateCommissionAsync(Transaction tx, CancellationToken ct = default) =>
        Big(await Get($"estimate_tx_commission/0x{tx.ToHex()}", ct), "commission");

    public async Task<BroadcastResult> SendAsync(Transaction tx, CancellationToken ct = default)
    {
        using var content = new StringContent($"{{\"tx\":\"0x{tx.ToHex()}\"}}", Encoding.UTF8, "application/json");
        using var reply = await http.PostAsync("send_transaction", content, ct);
        var j = JsonDocument.Parse(await reply.Content.ReadAsStringAsync(ct)).RootElement;
        if (j.TryGetProperty("error", out var error))   // CheckTx refusals come back as errors with the transaction code
            return new BroadcastResult((uint)ParseU64(error.GetProperty("code")), error.GetProperty("message").GetString() ?? "", tx.Hash);
        return new BroadcastResult((uint)U64(j, "code"), j.TryGetProperty("log", out var log) ? log.GetString() ?? "" : "", Str(j, "hash"));
    }

    public async Task<TxRecord?> TransactionAsync(string hash, CancellationToken ct = default)
    {
        try { return Record(await Get($"transaction/{hash}", ct)); }
        catch (ChainApiException e) when (e.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)) { return null; }
    }

    // Sent and received (tx.to: Send and single recipients), newest first, merged by hash.
    public async Task<IReadOnlyList<TxRecord>> HistoryAsync(Address address, int page, int perPage, CancellationToken ct = default)
    {
        var hex = Convert.ToHexStringLower(address.Span);
        var sent = await Query($"tags.tx.from='{hex}'", page, perPage, ct);
        var received = await Query($"tags.tx.to='{hex}'", page, perPage, ct);
        return sent.Concat(received).GroupBy(r => r.Hash).Select(g => g.First()).OrderByDescending(r => r.Height).ToList();
    }

    // The time of a block; "size" keeps the answer short (the list of fields has no "time": it comes anyway).
    public async Task<DateTimeOffset> BlockTimeAsync(long height, CancellationToken ct = default)
    {
        var j = await Get($"block/{height.ToString(CultureInfo.InvariantCulture)}?fields=size", ct);
        return DateTimeOffset.Parse(Str(j, "time"), CultureInfo.InvariantCulture);
    }

    private async Task<List<TxRecord>> Query(string query, int page, int perPage, CancellationToken ct)
    {
        JsonElement j;
        try { j = await Get($"transactions?query={Uri.EscapeDataString(query)}&page={page}&per_page={perPage}", ct); }
        catch (ChainApiException e) when (e.Message.StartsWith("page should be within", StringComparison.Ordinal)) { return []; }
        return j.GetProperty("transactions").EnumerateArray().Select(Record).ToList();
    }

    public async Task<IReadOnlyList<CandidateInfo>> CandidatesAsync(CancellationToken ct = default)
    {
        var j = await Get("candidates?status=all&not_show_stakes=true", ct);
        return j.GetProperty("candidates").EnumerateArray().Select(c => new CandidateInfo(
            Pubkey.Parse(Str(c, "public_key")), (uint)U64(c, "commission"), Big(c, "total_stake"), (int)U64(c, "status"),
            c.TryGetProperty("validator", out var v) && v.GetBoolean(), Address.Parse(Str(c, "owner_address")),
            Address.TryParse(c.TryGetProperty("reward_address", out var r) ? r.GetString() ?? "" : "", out var reward) ? reward : null,
            Address.TryParse(c.TryGetProperty("control_address", out var k) ? k.GetString() ?? "" : "", out var control) ? control : null)).ToList();
    }

    // Who has staked what with a candidate.
    public async Task<IReadOnlyList<StakeInfo>> StakesAsync(Pubkey candidate, CancellationToken ct = default)
    {
        var j = await Get($"candidate/{candidate}?not_show_stakes=false", ct);
        return j.TryGetProperty("stakes", out var s) && s.ValueKind == JsonValueKind.Array
            ? s.EnumerateArray().Select(x => new StakeInfo(Address.Parse(Str(x, "owner")), Coin(x.GetProperty("coin")), Big(x, "value"), Big(x, "bip_value"))).ToList()
            : [];
    }

    // The reserves of the pool of two coins, in the order asked; null when there is no such pool.
    public async Task<(BigInteger Amount0, BigInteger Amount1)?> SwapPoolAsync(uint coin0, uint coin1, CancellationToken ct = default)
    {
        try { var j = await Get($"swap_pool/{coin0}/{coin1}", ct); return (Big(j, "amount0"), Big(j, "amount1")); }
        catch (ChainApiException e) when (e.Code is 404 or 400 || e.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)) { return null; }
    }

    public async Task<IReadOnlyList<PoolInfo>> SwapPoolsAsync(CancellationToken ct = default)
    {
        var j = await Get("swap_pools", ct);
        return j.GetProperty("pools").EnumerateArray().Select(p => new PoolInfo((uint)U64(p, "id"), (uint)U64(p, "coin0"), (uint)U64(p, "coin1"),
            Big(p, "amount0"), Big(p, "amount1"), Big(p, "liquidity"))).ToList();
    }

    // A provider's part of a pool: its amounts of both coins and its liquidity tokens; null without a pool.
    public async Task<(BigInteger Amount0, BigInteger Amount1, BigInteger Liquidity)?> ProviderAsync(uint coin0, uint coin1, Address provider, CancellationToken ct = default)
    {
        try { var j = await Get($"swap_pool/{coin0}/{coin1}/{provider}", ct); return (Big(j, "amount0"), Big(j, "amount1"), Big(j, "liquidity")); }
        catch (ChainApiException e) when (e.Code is 404 or 400 || e.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)) { return null; }
    }

    // The orders among these ids that still exist (filled or cancelled ones are gone).
    public async Task<IReadOnlyList<OrderInfo>> LimitOrdersAsync(IEnumerable<uint> ids, CancellationToken ct = default)
    {
        // A repeated parameter, as the node's gateway reads a list (ids=1&ids=2; "1,2" is refused).
        var list = string.Join("&", ids.Distinct().Select(id => $"ids={id}"));
        if (list.Length == 0) return [];
        var j = await Get($"limit_orders?{list}", ct);
        return j.GetProperty("orders").EnumerateArray().Select(o => new OrderInfo((uint)U64(o, "id"), Coin(o.GetProperty("coin_sell")), Coin(o.GetProperty("coin_buy")),
            Big(o, "want_sell"), Big(o, "want_buy"), Address.Parse(Str(o, "owner")), (long)U64(o, "height"))).ToList();
    }

    // MinterGate only (/gate/v1/coins): the coins that can be bought, most liquid first; null from a plain node.
    public async Task<IReadOnlyList<CoinListing>?> CoinListAsync(CancellationToken ct = default)
    {
        JsonElement j;
        try { j = await Get("../gate/v1/coins", ct); }
        catch (ChainApiException e) when (e.Code is 404 or 405 or 501) { return null; }
        return j.GetProperty("coins").EnumerateArray().Select(c => new CoinListing((uint)U64(c, "id"), Str(c, "symbol"), Str(c, "name"),
            Str(c, "type") == "token", Big(c, "reserve"), Big(c, "pool_base"), (int)U64(c, "pools"))).ToList();
    }

    // A MinterGate answers /gate/v1/info, a node does not. IndexFrom: the first height of the gate's transaction
    // history (0 when it keeps none beyond its in-memory window).
    public sealed record GateDetails(long IndexFrom, long IndexTo);

    public async Task<GateDetails?> GateAsync(CancellationToken ct = default)
    {
        try
        {
            using var reply = await http.GetAsync("../gate/v1/info", ct);
            if (!reply.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await reply.Content.ReadAsStringAsync(ct));
            var j = doc.RootElement;
            return new GateDetails(j.TryGetProperty("index_from", out var f) ? f.GetInt64() : 0, j.TryGetProperty("index_to", out var t) ? t.GetInt64() : 0);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or InvalidOperationException) { return null; }
    }

    public async Task<bool> IsGateAsync(CancellationToken ct = default) => await GateAsync(ct) != null;

    public async Task<IReadOnlyList<FrozenFunds>> FrozenAsync(Address address, CancellationToken ct = default)
    {
        var j = await Get($"frozen/{address}", ct);
        return j.GetProperty("frozen").EnumerateArray().Select(f => new FrozenFunds((long)U64(f, "height"), Coin(f.GetProperty("coin")), Big(f, "value"),
            f.TryGetProperty("candidate_key", out var k) && k.ValueKind == JsonValueKind.String ? Pubkey.Parse(k.GetString()!) : null)).ToList();
    }

    // ---- plumbing ----

    private async Task<JsonElement> Get(string path, CancellationToken ct)
    {
        using var reply = await http.GetAsync(path, ct);
        var text = await reply.Content.ReadAsStringAsync(ct);
        JsonElement j;
        try { j = JsonDocument.Parse(text).RootElement; }
        catch (JsonException) { throw new ChainApiException((int)reply.StatusCode, $"{(int)reply.StatusCode}: ответ не JSON"); }
        if (j.ValueKind == JsonValueKind.Object && j.TryGetProperty("error", out var error))
            throw new ChainApiException((int)ParseU64(error.GetProperty("code")), error.GetProperty("message").GetString() ?? "ошибка источника");
        if (!reply.IsSuccessStatusCode) throw new ChainApiException((int)reply.StatusCode, $"HTTP {(int)reply.StatusCode}");
        return j;
    }

    private static TxRecord Record(JsonElement t)
    {
        var tags = new Dictionary<string, string>();
        if (t.TryGetProperty("tags", out var tg) && tg.ValueKind == JsonValueKind.Object)
            foreach (var p in tg.EnumerateObject()) tags[p.Name] = p.Value.GetString() ?? "";
        return new TxRecord(Str(t, "hash"), (long)U64(t, "height"), t.TryGetProperty("code", out var c) ? (uint)ParseU64(c) : 0,
            t.TryGetProperty("log", out var l) ? l.GetString() ?? "" : "", Convert.FromHexString(Str(t, "raw_tx")), tags);
    }

    private static CoinRef Coin(JsonElement c) => new((uint)U64(c, "id"), Str(c, "symbol"));
    private static string From(SwapFrom from) => from switch { SwapFrom.Pool => "pool", SwapFrom.Bancor => "bancor", _ => "optimal" };
    private static string Route(IReadOnlyList<uint>? route) => route is { Count: > 0 } ? string.Concat(route.Select(c => $"&route={c}")) : "";
    private static string Str(JsonElement j, string name) => j.GetProperty(name).GetString() ?? "";
    private static ulong U64(JsonElement j, string name) => j.TryGetProperty(name, out var v) ? ParseU64(v) : 0;
    private static ulong ParseU64(JsonElement v) => v.ValueKind == JsonValueKind.Number ? v.GetUInt64() : ulong.Parse(v.GetString() ?? "0", CultureInfo.InvariantCulture);
    private static BigInteger Big(JsonElement j, string name) => j.TryGetProperty(name, out var v) ? ParseBig(v) : BigInteger.Zero;
    private static BigInteger ParseBig(JsonElement v) => v.ValueKind == JsonValueKind.Null ? 0 : BigInteger.Parse(v.ValueKind == JsonValueKind.Number ? v.GetRawText() : v.GetString() ?? "0", CultureInfo.InvariantCulture);

    private static bool IsLocal(string host)
    {
        if (host is "localhost") return true;
        if (!IPAddress.TryParse(host, out var ip)) return host.EndsWith(".local", StringComparison.OrdinalIgnoreCase);
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily != AddressFamily.InterNetwork) return ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
    }
}
