using System.Net;
using System.Numerics;
using Minter.Protocol;
using MinterWallet.Api;
using MinterWallet.Core;

namespace MinterWallet.Checks;

// NodeApiClient against answers recorded from minter-go-node v3.3.0 (tools/devnet, 2026-10-27).
internal static class ApiChecks
{
    private const string A = "Mx0ee51895545fac3a24f6845ad24af12d2753100e";

    private static readonly Dictionary<string, (HttpStatusCode Status, string Body)> Recorded = new()
    {
        ["status"] = (HttpStatusCode.OK, """{"version":"3.3.0-testnet","network":"minter-devnet","initial_height":"1","latest_block_hash":"38F6","latest_app_hash":"AF64","latest_block_height":"10","latest_block_time":"2026-10-27T00:03:57.268023243+03:00","keep_last_states":"120"}"""),
        [$"address/{A}?delegated=true"] = (HttpStatusCode.OK, """{"balance":[{"coin":{"id":"0","symbol":"MNT"},"value":"999999765655397708875992189","bip_value":"999999765655397708875992189"},{"coin":{"id":"2","symbol":"LP-1"},"value":"99999999999999999000","bip_value":"0"}],"delegated":[{"coin":{"id":"0","symbol":"MNT"},"value":"950000000000000000000","bip_value":"950000000000000000000","delegate_bip_value":"1000000000000000000000"}],"total":[],"transaction_count":"17","bip_value":"1000000715655397708875992189","locked_stake_until_block":"0","multisig":null}"""),
        ["coin_info_by_id/1"] = (HttpStatusCode.OK, """{"id":"1","name":"Wallet E2E","symbol":"WALLET0010","volume":"1005000000000000000000","crr":"0","reserve_balance":"0","max_supply":"1000000000000000000000000","owner_address":"Mx0ee51895545fac3a24f6845ad24af12d2753100e","mintable":true,"burnable":true}"""),
        ["coin_info_by_id/999"] = (HttpStatusCode.NotFound, """{"error":{"code":"102","message":"Coin not found","data":{"coin_id":"999"}}}"""),
        ["min_gas_price"] = (HttpStatusCode.OK, """{"min_gas_price":"1"}"""),
        ["price_commissions"] = (HttpStatusCode.OK, """{"coin":{"id":"0","symbol":"MNT"},"payload_byte":"2000000000000000","send":"10000000000000000","create_ticker7_10":"100000000000000000000","multisend_delta":"5000000000000000","lock":"100000000000000000"}"""),
        ["estimate_coin_sell?coin_id_to_sell=0&coin_id_to_buy=1&value_to_sell=1000000000000000000&swap_from=optimal"] = (HttpStatusCode.OK, """{"will_get":"91218149798903793779","commission":"100000000000000000","swap_from":"pool"}"""),
        ["estimate_coin_buy?coin_id_to_sell=0&coin_id_to_buy=1&value_to_buy=1000000000000000000&swap_from=pool&route=0&route=1"] = (HttpStatusCode.OK, """{"will_pay":"9976743950443835","commission":"100000000000000000","swap_from":"pool"}"""),
        ["best_trade/0/1/input/1000000000000000000?max_depth=4"] = (HttpStatusCode.OK, """{"path":["0","1"],"result":"91218149798903793779"}"""),
        ["best_trade/0/999/input/1000000000000000000?max_depth=4"] = (HttpStatusCode.NotFound, """{"error":{"code":"404","message":"route path not found","data":{}}}"""),
        ["transaction/Mt0000"] = (HttpStatusCode.BadRequest, """{"error":{"code":"400","message":"tx (0000) not found","data":{}}}"""),
        ["candidates?status=all&not_show_stakes=true"] = (HttpStatusCode.OK, """{"candidates":[{"id":"1","reward_address":"Mx0ee51895545fac3a24f6845ad24af12d2753100e","owner_address":"Mx0ee51895545fac3a24f6845ad24af12d2753100e","control_address":"Mx0ee51895545fac3a24f6845ad24af12d2753100e","total_stake":"1001000000000000000000000","public_key":"Mp62c89de2a33995133f9476d546b21833f87bfa7ffb44dbe83086868cae3bc86e","commission":"10","used_slots":null,"uniq_users":null,"min_stake":null,"stakes":[],"status":"2","validator":true,"jailed_until":"0"}]}"""),
        [$"frozen/{A}"] = (HttpStatusCode.OK, """{"frozen":[{"height":"105","address":"Mx0ee51895545fac3a24f6845ad24af12d2753100e","candidate_key":null,"coin":{"id":"0","symbol":"MNT"},"value":"1000000000000000000","move_to_candidate_key":null},{"height":"536","address":"Mx0ee51895545fac3a24f6845ad24af12d2753100e","candidate_key":"Mp62c89de2a33995133f9476d546b21833f87bfa7ffb44dbe83086868cae3bc86e","coin":{"id":"0","symbol":"MNT"},"value":"50000000000000000000","move_to_candidate_key":null}]}"""),
        ["transactions?query=tags.tx.from%3D%271111111111111111111111111111111111111111%27&page=1&per_page=30"] = (HttpStatusCode.OK, """{"transactions":[]}"""),
        ["transactions?query=tags.tx.to%3D%271111111111111111111111111111111111111111%27&page=1&per_page=30"] = (HttpStatusCode.BadRequest, """{"error":{"code":"400","message":"page should be within [1, 0] range, given 1","data":{}}}"""),
        ["send_transaction"] = (HttpStatusCode.BadRequest, """{"error":{"code":"301","message":"\"From\" coin equals to \"to\" coin","data":{}}}"""),
    };

    public static async Task Parsing()
    {
        using var api = new NodeApiClient(new Uri("http://node.test:8843"), handler: new RecordedHandler(Recorded));
        Assert.Equal("http://node.test:8843/v2/", api.BaseUri.AbsoluteUri, "base path");
        var status = await api.StatusAsync();
        Assert.Equal(("minter-devnet", 10L), (status.Network, status.Height), "status");
        var account = await api.AccountAsync(Address.Parse(A));
        Assert.Equal(17UL, account.Nonce, "nonce = transaction_count");
        Assert.Equal(BigInteger.Parse("999999765655397708875992189"), account.BalanceOf(0), "base balance");
        Assert.Equal("LP-1", account.Balances[1].Coin.Symbol, "LP token");
        Assert.Equal(BigInteger.Parse("950000000000000000000"), account.Delegated[0].Value, "delegated");
        var coin = await api.CoinAsync(1);
        Assert.Equal(("WALLET0010", true), (coin!.Symbol, coin.Mintable), "coin");
        Assert.True(await api.CoinAsync(999) == null, "missing coin is null");
        Assert.Equal(1u, await api.MinGasPriceAsync(), "min gas price");
        var prices = await api.PricesAsync();
        Assert.Equal(BigInteger.Parse("100000000000000000000"), prices[PriceField.CreateTicker7to10], "create_ticker7_10");
        Assert.Equal(BigInteger.Parse("5000000000000000"), prices[PriceField.MultisendDelta], "multisend_delta");
        var sell = await api.EstimateSellAsync(0, 1, BigInteger.Pow(10, 18), SwapFrom.Optimal);
        Assert.Equal((BigInteger.Parse("91218149798903793779"), "pool"), (sell.Amount, sell.SwapFrom), "estimate sell");
        var buy = await api.EstimateBuyAsync(0, 1, BigInteger.Pow(10, 18), SwapFrom.Pool, [0, 1]);
        Assert.Equal(BigInteger.Parse("9976743950443835"), buy.Amount, "estimate buy (will_pay)");
        var trade = await api.BestTradeAsync(0, 1, BigInteger.Pow(10, 18), exactInput: true);
        Assert.Equal("0,1", string.Join(",", trade!.Path), "best trade path");
        Assert.True(await api.BestTradeAsync(0, 999, BigInteger.Pow(10, 18), true) == null, "no route is null");
        Assert.True(await api.TransactionAsync("Mt0000") == null, "unknown transaction is null");
        var candidates = await api.CandidatesAsync();
        Assert.Equal((10u, true, 2), (candidates[0].Commission, candidates[0].Validator, candidates[0].Status), "candidate");
        var frozen = await api.FrozenAsync(Address.Parse(A));
        Assert.Equal((2, 536L, true), (frozen.Count, frozen[1].UnlockHeight, frozen[1].Candidate != null), "frozen");
        Assert.Equal(0, (await api.HistoryAsync(Address.Parse("Mx1111111111111111111111111111111111111111"), 1, 30)).Count, "empty history (and the page-range error)");
        var tx = Transaction.Create(1, ChainId.Testnet, 1, 0, new SellCoinData(0, 1, 0, 0));
        using (var key = new PrivateKey(TestKeys.Bytes(3))) tx.Sign(key);
        var refused = await api.SendAsync(tx);
        Assert.Equal(301u, refused.Code, "CheckTx refusal code");
    }

    private sealed class RecordedHandler(Dictionary<string, (HttpStatusCode Status, string Body)> answers) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var key = request.RequestUri!.PathAndQuery["/v2/".Length..];
            if (!answers.TryGetValue(key, out var answer)) throw new InvalidOperationException($"unexpected request {key}");
            return Task.FromResult(new HttpResponseMessage(answer.Status) { Content = new StringContent(answer.Body) });
        }
    }
}
