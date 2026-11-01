using System.Globalization;
using System.Net.Http.Json;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace Minter.Protocol.Checks;

// End to end against a real minter-go-node v3.3.0 (tools/devnet, chain ID 2): transactions and checks built and signed
// by Minter.Protocol are sent through the node's API v2 and must be accepted by CheckTx and executed by DeliverTx with
// the expected balance changes and commissions. Test keys only; run with: --devnet http://WSL-IP:38843
internal sealed class DevnetChecks(string baseUrl)
{
    private const byte Chain = ChainId.Testnet;
    private readonly HttpClient http = new(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/v2/"), Timeout = TimeSpan.FromSeconds(10) };
    private PriceCommissions prices = new();
    private int step;

    public static int Run(string url)
    {
        try { new DevnetChecks(url).Scenario().GetAwaiter().GetResult(); return 0; }
        catch (Exception error) { Console.WriteLine($"FAIL devnet: {error.Message}"); return 1; }
    }

    private async Task Scenario()
    {
        using var a = TestKey(1); using var b = TestKey(2); using var c = TestKey(3); using var d = TestKey(4);
        var status = await Get("status");
        Assert.Equal("minter-devnet", status.GetProperty("network").GetString(), "devnet network (never mainnet)");
        prices = await Prices();
        var validator = Pubkey.Parse((await Get("candidates")).GetProperty("candidates")[0].GetProperty("public_key").GetString()!);
        var coin = Units.PipPerCoin;

        // Plain transfers: exact balance changes, commission equals Minter.Protocol's calculator.
        var before = await Balance(b.Address, 0);
        await Expect(a, new SendData(0, b.Address, 10 * coin), "send", paidValue: 10 * coin);
        Assert.Equal(before + 10 * coin, await Balance(b.Address, 0), "recipient got 10");
        await Expect(a, new MultisendData([new(0, b.Address, coin), new(0, c.Address, coin), new(0, d.Address, coin)]), "multisend", paidValue: 3 * coin);
        await Expect(a, new SendData(0, b.Address, coin), "send with payload", payload: Encoding.UTF8.GetBytes("привет из кошелька ✓"), paidValue: coin);

        // Staking and locks.
        await Expect(a, new DelegateData(validator, 0, 100 * coin), "delegate", paidValue: 100 * coin);
        await Expect(a, new UnbondData(validator, 0, 50 * coin), "unbond");
        var height = await Height();
        await Expect(a, new LockData(checked((uint)height + 100), 0, coin), "lock", paidValue: coin);

        // Multisignature: create (address as computed locally), fund, spend with two co-signers.
        var nonceB = await Nonce(b.Address);
        var multisig = Address.Multisig(b.Address, nonceB);
        await Expect(b, new CreateMultisigData(2, [1, 1], [c.Address, d.Address]), "create multisig");
        await Expect(a, new SendData(0, multisig, 5 * coin), "fund multisig", paidValue: 5 * coin);
        var aBefore = await Balance(a.Address, 0);
        var spend = Transaction.Create(await Nonce(multisig), Chain, 1, 0, new SendData(0, a.Address, coin), signatureType: SignatureType.Multi);
        spend.SetMultisigAddress(multisig);
        spend.Sign(c);
        var passed = Transaction.Decode(spend.Encode());   // to the second participant as bytes, signed there
        passed.Sign(d);
        Assert.Equal(multisig, passed.Sender(), "multisig sender");
        await Submit(passed, "multisig send (2 of 2)");
        Assert.Equal(aBefore + coin, await Balance(a.Address, 0), "multisig paid out");

        // Check: issued by A offline, redeemed by B with the password.
        height = await Height();
        var check = Check.Issue(a, "e2e пароль", Encoding.UTF8.GetBytes("e2e-1"), Chain, (ulong)height + 1000, 0, 2 * coin, 0);
        var bBefore = await Balance(b.Address, 0);
        var redeem = Transaction.Create(await Nonce(b.Address), Chain, 1, 0, check.Redeem(b.Address, "e2e пароль"));
        redeem.Sign(b);
        await Submit(redeem, "redeem check");
        Assert.Equal(bBefore + 2 * coin, await Balance(b.Address, 0), "check value received in full (issuer pays the commission)");

        // Token, pool, swaps, limit order, commission paid in the token.
        // A fresh 10-character ticker per run (the scenario may run again on the same devnet).
        var ticker = $"WALLET{await Height() % 10000:D4}";
        await Expect(a, new CreateTokenData("Wallet E2E", CoinSymbol.FromString(ticker), 1000 * coin, 1_000_000 * coin, true, true), "create token");
        var token = uint.Parse((await Get($"coin_info/{ticker}")).GetProperty("id").GetString()!, CultureInfo.InvariantCulture);
        await Expect(a, new MintTokenData(token, 10 * coin), "mint token");
        await Expect(a, new BurnTokenData(token, 5 * coin), "burn token");
        await Expect(a, new CreateSwapPoolData(0, token, 100 * coin, 100 * coin), "create swap pool");
        await Expect(a, new SellSwapPoolData([token, 0], coin, 1), "sell swap pool");
        await Expect(a, new BuySwapPoolData([0, token], coin, 1000 * coin), "buy swap pool");
        await Expect(a, new SendData(0, b.Address, coin), "send, commission paid in the token through its pool", paidValue: coin, gasCoin: token);
        await Expect(a, new SellAllSwapPoolData([token, 0], 1), "sell all swap pool");
        // The network admits an order price within (pool price / 5, pool price]: sell 1 BIP at half the pool price.
        var pool = await Get($"swap_pool/0/{token}");
        var bipReserve = BigInteger.Parse(pool.GetProperty("amount0").GetString()!, CultureInfo.InvariantCulture);
        var tokenReserve = BigInteger.Parse(pool.GetProperty("amount1").GetString()!, CultureInfo.InvariantCulture);
        var order = await Expect(a, new AddLimitOrderData(0, coin, token, 2 * coin * tokenReserve / bipReserve), "add limit order");
        var orderId = uint.Parse(order.GetProperty("tags").GetProperty("tx.order_id").GetString()!, CultureInfo.InvariantCulture);
        await Expect(a, new RemoveLimitOrderData(orderId), "remove limit order");
        await Expect(c, new SellCoinData(0, coin, 0, 0), "rejected: sell a coin for itself", expectCode: 0, mustFail: true);
        Console.WriteLine($"PASS devnet: {step} transactions accepted and executed by minter-go-node v3.3.0, balances and commissions as computed");
    }

    // Signs, sends and waits for the block; checks the code and, for the base gas coin, the commission charged.
    private async Task<JsonElement> Expect(PrivateKey key, ITxData data, string what, byte[]? payload = null, BigInteger? paidValue = null,
        uint expectCode = 0, bool mustFail = false, uint gasCoin = 0)
    {
        var tx = Transaction.Create(await Nonce(key.Address), Chain, 1, gasCoin, data, payload);
        tx.Sign(key);
        if (mustFail)
        {
            var response = await Send(tx);
            Assert.True(response.Code != expectCode, $"{what}: the node must reject it (got code {response.Code})");
            Console.WriteLine($"  {what}: node answered code {response.Code} ({response.Log})");
            return default;
        }
        var balanceBefore = await Balance(key.Address, 0);
        var result = await Submit(tx, what);
        if (paidValue is { } value)
        {
            // A commission in another coin is converted through its pool by the node; the base coin then moves by the value only.
            var fee = gasCoin == 0 ? Commission.WithGasPrice(Commission.Price(tx, prices), tx.GasPrice) : 0;
            Assert.Equal(balanceBefore - value - fee, await Balance(key.Address, 0), $"{what}: balance after value and commission {Units.FormatCoins(fee)}");
        }
        return result;
    }

    private async Task<JsonElement> Submit(Transaction tx, string what)
    {
        var response = await Send(tx);
        Assert.True(response.Code == 0, $"{what}: CheckTx code {response.Code}: {response.Log}");
        Assert.Equal(tx.Hash.ToLowerInvariant(), response.Hash.ToLowerInvariant(), $"{what}: node hash");
        for (var i = 0; i < 60; i++)
        {
            using var reply = await http.GetAsync($"transaction/{tx.Hash}");
            if (reply.IsSuccessStatusCode)
            {
                var result = JsonDocument.Parse(await reply.Content.ReadAsStringAsync()).RootElement;
                var code = result.TryGetProperty("code", out var c) ? ulong.Parse(c.GetString()!, CultureInfo.InvariantCulture) : 0;
                Assert.True(code == 0, $"{what}: DeliverTx code {code}: {(result.TryGetProperty("log", out var l) ? l.GetString() : "")}");
                Assert.Equal(tx.Sender().ToString(), result.GetProperty("from").GetString(), $"{what}: sender seen by the node");
                step++;
                Console.WriteLine($"  {what}: block {result.GetProperty("height").GetString()}, {tx.Hash}");
                return result;
            }
            await Task.Delay(500);
        }
        throw new InvalidOperationException($"{what}: not included within 30 s");
    }

    private async Task<(ulong Code, string Log, string Hash)> Send(Transaction tx)
    {
        using var reply = await http.PostAsJsonAsync("send_transaction", new { tx = "0x" + tx.ToHex() });
        var body = JsonDocument.Parse(await reply.Content.ReadAsStringAsync()).RootElement;
        if (body.TryGetProperty("error", out var error))
            return (ulong.Parse(error.GetProperty("code").GetString() ?? "1", CultureInfo.InvariantCulture), error.GetProperty("message").GetString() ?? "", "");
        return (ulong.Parse(body.GetProperty("code").GetString()!, CultureInfo.InvariantCulture), body.GetProperty("log").GetString() ?? "", body.GetProperty("hash").GetString()!);
    }

    private async Task<JsonElement> Get(string path)
    {
        using var reply = await http.GetAsync(path);
        var text = await reply.Content.ReadAsStringAsync();
        if (!reply.IsSuccessStatusCode) throw new InvalidOperationException($"GET {path}: {(int)reply.StatusCode} {text}");
        return JsonDocument.Parse(text).RootElement;
    }

    private async Task<long> Height() => long.Parse((await Get("status")).GetProperty("latest_block_height").GetString()!, CultureInfo.InvariantCulture);
    private async Task<ulong> Nonce(Address address) => ulong.Parse((await Get($"address/{address}")).GetProperty("transaction_count").GetString()!, CultureInfo.InvariantCulture) + 1;
    private async Task<BigInteger> Balance(Address address, uint coin)
    {
        foreach (var balance in (await Get($"address/{address}")).GetProperty("balance").EnumerateArray())
            if (balance.GetProperty("coin").GetProperty("id").GetString() == coin.ToString(CultureInfo.InvariantCulture))
                return BigInteger.Parse(balance.GetProperty("value").GetString()!, CultureInfo.InvariantCulture);
        return 0;
    }
    private async Task<PriceCommissions> Prices()
    {
        var json = await Get("price_commissions");
        var p = new PriceCommissions { Coin = uint.Parse(json.GetProperty("coin").GetProperty("id").GetString()!, CultureInfo.InvariantCulture) };
        foreach (var field in PriceCommissions.Fields) p[field] = BigInteger.Parse(json.GetProperty(PriceCommissions.ApiName(field)).GetString()!, CultureInfo.InvariantCulture);
        return p;
    }

    private static PrivateKey TestKey(int n) => new(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes($"minterwallet-test-{n}")));
}
