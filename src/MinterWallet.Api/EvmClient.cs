using System.Globalization;
using System.Net;
using System.Numerics;
using System.Text;
using System.Text.Json;
using MinterWallet.Core;

namespace MinterWallet.Api;

public sealed record EvmReceipt(bool Succeeded, long Block, ulong GasUsed);

public sealed class EvmRpcException(string message) : Exception(message);

// JSON-RPC of an Ethereum or BSC node (public endpoints, tried in turn): balances, calls, gas, nonces, sending a signed
// transaction and its receipt. Like a Minter source, it is not trusted with keys: transactions are signed on the device.
public sealed class EvmClient : IDisposable
{
    private readonly HttpClient http;
    private readonly IReadOnlyList<Uri> endpoints;
    private int current;

    public static IReadOnlyList<Uri> DefaultEndpoints(BridgeNetwork network) => network == BridgeNetwork.Bsc
        ? [new("https://bsc-dataseed.bnbchain.org"), new("https://bsc-rpc.publicnode.com")]
        : [new("https://ethereum-rpc.publicnode.com"), new("https://rpc-eth.blockmachine.io")];

    public EvmClient(IReadOnlyList<Uri> endpoints, HttpMessageHandler? handler = null)
    {
        this.endpoints = endpoints.Count > 0 ? endpoints : throw new ArgumentException("No endpoints.");
        handler ??= new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2), AutomaticDecompression = DecompressionMethods.All };
        http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 4 * 1024 * 1024 };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MinterWallet");
    }

    public void Dispose() => http.Dispose();

    public async Task<ulong> ChainIdAsync(CancellationToken ct = default) => (ulong)Quantity(await Call("eth_chainId", "[]", ct));
    public async Task<BigInteger> BalanceAsync(string address, CancellationToken ct = default) => Quantity(await Call("eth_getBalance", $"[\"{address}\",\"latest\"]", ct));
    public async Task<ulong> NonceAsync(string address, CancellationToken ct = default) => (ulong)Quantity(await Call("eth_getTransactionCount", $"[\"{address}\",\"pending\"]", ct));
    public async Task<BigInteger> GasPriceAsync(CancellationToken ct = default) => Quantity(await Call("eth_gasPrice", "[]", ct));

    public async Task<byte[]> CallAsync(string to, byte[] data, CancellationToken ct = default) =>
        Bytes(await Call("eth_call", $"[{{\"to\":\"{to}\",\"data\":\"0x{Convert.ToHexStringLower(data)}\"}},\"latest\"]", ct));

    public async Task<ulong> EstimateGasAsync(string from, string to, BigInteger value, byte[] data, CancellationToken ct = default) =>
        (ulong)Quantity(await Call("eth_estimateGas", $"[{{\"from\":\"{from}\",\"to\":\"{to}\",\"value\":\"{Hex(value)}\",\"data\":\"0x{Convert.ToHexStringLower(data)}\"}}]", ct));

    // The hash; a node that already has the transaction (sent through another endpoint before a timeout) is no failure.
    public async Task<string> SendRawAsync(byte[] raw, CancellationToken ct = default)
    {
        try { return (await Call("eth_sendRawTransaction", $"[\"0x{Convert.ToHexStringLower(raw)}\"]", ct)).GetString() ?? EvmTx.HashOf(raw); }
        catch (EvmRpcException e) when (e.Message.Contains("already known", StringComparison.OrdinalIgnoreCase)) { return EvmTx.HashOf(raw); }
    }

    public async Task<EvmReceipt?> ReceiptAsync(string hash, CancellationToken ct = default)
    {
        var r = await Call("eth_getTransactionReceipt", $"[\"{hash}\"]", ct);
        if (r.ValueKind != JsonValueKind.Object) return null;
        return new EvmReceipt(Quantity(r.GetProperty("status")) == 1, (long)Quantity(r.GetProperty("blockNumber")), (ulong)Quantity(r.GetProperty("gasUsed")));
    }

    // One request; an endpoint that does not answer, limits the rate or fails gives the turn to the next one.
    private async Task<JsonElement> Call(string method, string parameters, CancellationToken ct)
    {
        var body = $"{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"{method}\",\"params\":{parameters}}}";
        Exception? last = null;
        for (var attempt = 0; attempt < endpoints.Count; attempt++)
        {
            var endpoint = endpoints[(current + attempt) % endpoints.Count];
            try
            {
                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                using var reply = await http.PostAsync(endpoint, content, ct);
                var text = await reply.Content.ReadAsStringAsync(ct);
                if (!reply.IsSuccessStatusCode) throw new HttpRequestException($"{endpoint.Host}: HTTP {(int)reply.StatusCode}");
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("error", out var error))
                {
                    var message = error.TryGetProperty("message", out var m) ? m.GetString() ?? "" : error.GetRawText();
                    // A refusal of the transaction or the call itself is the answer, not a broken endpoint.
                    if (!message.Contains("rate", StringComparison.OrdinalIgnoreCase) && !message.Contains("limit exceeded", StringComparison.OrdinalIgnoreCase)) throw new EvmRpcException(message);
                    throw new HttpRequestException($"{endpoint.Host}: {message}");
                }
                current = (current + attempt) % endpoints.Count;
                return doc.RootElement.GetProperty("result").Clone();
            }
            catch (Exception e) when (!ct.IsCancellationRequested && e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException) { last = e; }
        }
        throw last is HttpRequestException or TaskCanceledException ? last : new HttpRequestException("Узлы сети не ответили.", last);
    }

    private static BigInteger Quantity(JsonElement e)
    {
        var text = e.GetString() ?? throw new FormatException("Not a quantity.");
        if (!text.StartsWith("0x", StringComparison.Ordinal)) throw new FormatException($"Not a quantity: {text}");
        return text.Length == 2 ? 0 : BigInteger.Parse("0" + text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    private static byte[] Bytes(JsonElement e)
    {
        var text = e.GetString() ?? "0x";
        return text.StartsWith("0x", StringComparison.Ordinal) ? Convert.FromHexString(text[2..]) : throw new FormatException("Not hex data.");
    }

    private static string Hex(BigInteger value) => value.IsZero ? "0x0" : "0x" + value.ToString("x", CultureInfo.InvariantCulture).TrimStart('0');
}

// hub.mnst.club's bridge API (the working Minter Hub front end): the coins the bridge carries in each network.
public sealed class BridgeApiClient : IDisposable
{
    public static readonly Uri Default = new("https://hub.mnst.club/bridge-api/");
    private readonly HttpClient http;

    public BridgeApiClient(Uri? baseUri = null, HttpMessageHandler? handler = null)
    {
        handler ??= new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2), AutomaticDecompression = DecompressionMethods.All };
        http = new HttpClient(handler) { BaseAddress = baseUri ?? Default, Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 2 * 1024 * 1024 };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MinterWallet");
    }

    public void Dispose() => http.Dispose();

    // Both networks' tokens (Bridge.Use keeps a network the answer leaves out).
    public async Task<IReadOnlyList<BridgeToken>> TokensAsync(CancellationToken ct = default)
    {
        var result = new List<BridgeToken>();
        foreach (var network in new[] { BridgeNetwork.Ethereum, BridgeNetwork.Bsc })
        {
            using var reply = await http.GetAsync($"tokens?chain_id={(network == BridgeNetwork.Bsc ? "bsc" : "ethereum")}", ct);
            reply.EnsureSuccessStatusCode();
            result.AddRange(Bridge.ParseTokens(await reply.Content.ReadAsStringAsync(ct), network));
        }
        return result;
    }
}
