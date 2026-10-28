using System.Numerics;
using Minter.Protocol;
using MinterWallet.Core;

namespace MinterWallet.Checks;

// The history's account of transactions: Mt3bd56b2a… of block 40392852 on mainnet (0.428 USDTE through four pools for
// 29.24 GOODZONE, the commission paid in GOODZONE) with the tags the gate gives; a send both ways; a failed transaction.
internal static class ExplainChecks
{
    private const string SwapRaw = "f88e81880101820a7c17a2e1cd8207c982068e820e2080820a7c8805f08fbb7bde0000890195c95914706c0000808002b85ef85c945aea138bd36a4e6019472ebbbe0a88ac2e4f9969f845f8431ba0efc24dabc1fa89a09823f415efd738d416769a92010ca7616c6f709437c36263a026de64250e620fa05325f6b1dc0f3c18b78175dbbf58dd6cf0930af665c54e26";
    private const string Pools = """[{"pool_id":139,"coin_in":1993,"value_in":"428000000000000000","coin_out":1678,"value_out":"453600652624742113"},{"pool_id":1213,"coin_in":1678,"value_in":"453600652624742113","coin_out":3616,"value_out":"5164567982785521061943"},{"pool_id":1212,"coin_in":3616,"value_in":"5164567982785521061943","coin_out":0,"value_out":"5112050467954593935944"},{"pool_id":612,"coin_in":0,"value_in":"5112050467954593935944","coin_out":2684,"value_out":"29240364784398093376"}]""";

    public static Task Run()
    {
        string Symbol(uint id) => id switch { 0 => "BIP", 1993 => "USDTE", 1678 => "USDX", 3616 => "BIPXBIP", 2684 => "GOODZONE", _ => $"#{id}" };
        var trader = Address.Parse("Mx5aea138bd36a4e6019472ebbbe0a88ac2e4f9969");
        var tags = new Dictionary<string, string>
        {
            ["tx.return"] = "29240364784398093376", ["tx.pools"] = Pools, ["tx.commission_amount"] = "3010510109716627414", ["tx.commission_coin"] = "2684",
            ["tx.commission_conversion"] = "pool", ["tx.coin_to_sell"] = "1993", ["tx.coin_to_buy"] = "2684",
        };
        var swap = TxExplain.Explain(new TxRecord("Mt3bd56b2a", 40392852, 0, "", Convert.FromHexString(SwapRaw), tags), trader, Symbol);
        Assert.Equal("Обмен", swap.Title, "a swap is called a swap");
        Assert.Equal("USDTE → USDX → BIPXBIP → BIP → GOODZONE", swap.Subtitle, "its route");
        Assert.True(swap.Moves is [var sold, var got] && sold == new TxMove(-Units.ParseCoins("0.428"), 1993) && got == new TxMove(BigInteger.Parse("29240364784398093376"), 2684),
            "what it gave and what it brought (tx.return)");
        Assert.Equal("3.010510109716627414 GOODZONE", swap.Commission, "the commission in the coin it was paid in");
        Assert.True(swap.Rows.Any(r => r.Label == "Маршрут" && r.Value.Split('\n') is [var first, _, _, var last]
            && first == "0.428 USDTE → 0.453600652624742113 USDX (пул #139)" && last == "5112.050467954593935944 BIP → 29.240364784398093376 GOODZONE (пул #612)"), "every pool of the route with its amounts");

        // Without the tags (an older transaction from the explorer): only what it gave.
        var bare = TxExplain.Explain(new TxRecord("Mt3bd56b2a", 40392852, 0, "", Convert.FromHexString(SwapRaw), new Dictionary<string, string>()), trader, Symbol);
        Assert.True(bare.Moves is [{ Amount.Sign: < 0 }] && bare.Commission.Length == 0, "no tags: no amount received, no commission");

        // A send: minus for the sender, plus for the recipient; a failed one moves nothing but names its error.
        using var key = new PrivateKey(TestKeys.Bytes(3));
        var to = TestKeys.Address(4);
        var send = Transaction.Create(1, ChainId.Testnet, 1, 0, new SendData(0, to, Units.ParseCoins("2.5")));
        send.Sign(key);
        var record = new TxRecord(send.Hash, 10, 0, "", send.Encode(), new Dictionary<string, string>());
        Assert.True(TxExplain.Explain(record, key.Address, Symbol) is { Title: "Перевод", Moves: [{ Amount.Sign: < 0 }] } &&
                    TxExplain.Explain(record, to, Symbol) is { Title: "Получено", Moves: [{ Amount.Sign: > 0 }] } r && r.Counterparty == key.Address, "a send seen from both ends");
        var failed = TxExplain.Explain(record with { Code = 107, Log = "Insufficient funds" }, key.Address, Symbol);
        Assert.True(failed.Moves.Count == 0 && failed.Rows[0].Value.StartsWith("не выполнена (код 107)", StringComparison.Ordinal), "a failed transaction moves nothing");
        return Task.CompletedTask;
    }
}
