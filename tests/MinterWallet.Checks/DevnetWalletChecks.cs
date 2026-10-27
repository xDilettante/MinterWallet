using System.Numerics;
using System.Security.Cryptography;
using Minter.Protocol;
using MinterWallet.Api;
using MinterWallet.Core;

namespace MinterWallet.Checks;

// The wallet end to end on tools/devnet: key store -> preview through the node -> per-sender queue -> block -> history
// decoded from raw bytes. Test keys only (chain ID 2). Run: --devnet http://WSL-IP:38843
internal static class DevnetWalletChecks
{
    public static async Task<int> Run(string url)
    {
        try { await Scenario(url); return 0; }
        catch (Exception error) { Console.WriteLine($"FAIL devnet wallet: {error.GetType().Name}: {error.Message}"); return 1; }
    }

    private static async Task Scenario(string url)
    {
        using var api = new NodeApiClient(new Uri(url));
        var status = await api.StatusAsync();
        Assert.Equal("minter-devnet", status.Network, "devnet only");
        var chainId = WalletService.ChainIdOf(status.Network);
        Assert.Equal(ChainId.Testnet, chainId, "chain id 2");

        using var temp = new TempDirectory();
        var (store, session) = Keystore.Create(Path.Combine(temp.Path, Keystore.FileName), "пароль-кошелька", new Keystore.KdfParameters(8 * 1024, 1, 1, RandomNumberGenerator.GetBytes(16)));
        var a = session.AddPrivateKey(TestKeys.Bytes(1), "A");
        var b = session.AddPrivateKey(TestKeys.Bytes(2), "B");
        // A fresh mnemonic every run, so balances start from zero.
        var fresh = session.AddMnemonic(Mnemonic.Generate(), "Из мнемоники");
        var registry = new CoinRegistry(Path.Combine(temp.Path, "coins.json"));
        var baseCoin = (await api.CoinAsync(0))!;
        registry.Observe(new CoinRef(0, baseCoin.Symbol));
        var wallet = new WalletService(api, session, chainId, queue: new SendQueue(api, TimeSpan.FromSeconds(60), TimeSpan.FromMilliseconds(500)));
        var coin = Units.PipPerCoin;

        // Preview: the node's estimate equals Minter.Protocol's price (commission coin is the base coin here).
        var send = await wallet.PreviewAsync(a, new SendData(0, fresh.Address, coin), payload: "из кошелька"u8.ToArray());
        Assert.Equal(0u, send.VoteCoin, "prices in the base coin");
        Assert.Equal(send.PriceInVoteCoin, send.Commission, "node estimate = local commission");
        var outcome = await wallet.SendAsync(a, send);
        Assert.True(outcome.Succeeded, $"send: {outcome.State} {outcome.Code} {outcome.Log}");
        Assert.Equal(coin, (await api.AccountAsync(fresh.Address)).BalanceOf(0), "mnemonic account funded");
        Console.WriteLine($"  send with payload: block {outcome.Height}, commission {Units.FormatCoins(send.Commission)} {baseCoin.Symbol}");

        // Three at once from one account: the queue keeps them one per block with consecutive nonces.
        var nonceBefore = (await api.AccountAsync(a.Address)).Nonce;
        var previews = new List<TxPreview>();
        for (var i = 1; i <= 3; i++) previews.Add(await wallet.PreviewAsync(a, new SendData(0, b.Address, i * coin)));
        var batch = await Task.WhenAll(previews.Select(p => wallet.SendAsync(a, p)));   // submitted at once, in this order
        Assert.True(batch.All(o => o.Succeeded), "queued sends included: " + string.Join(", ", batch.Select(o => $"{o.State}/{o.Code}")));
        Assert.Equal(nonceBefore + 3, (await api.AccountAsync(a.Address)).Nonce, "three consecutive nonces");
        Assert.True(batch[0].Height < batch[1].Height && batch[1].Height < batch[2].Height, "blocks follow the submission order");
        Console.WriteLine($"  three queued sends: blocks {string.Join(", ", batch.Select(o => o.Height))}");

        // Spending from the mnemonic account (key derived at signing time).
        var back = await wallet.SendAsync(fresh, await wallet.PreviewAsync(fresh, new SendData(0, a.Address, coin / 2)));
        Assert.True(back.Succeeded, $"send from mnemonic account: {back.State} {back.Log}");

        // History of the mnemonic account, decoded from raw transactions.
        var history = (await api.HistoryAsync(fresh.Address, 1, 30)).Select(r => History.Decode(r, fresh.Address, registry.Symbol)).ToList();
        Assert.True(history.Any(h => h.Direction == Direction.Incoming && h.Summary == $"Получено 1 {baseCoin.Symbol} от {History.Short(a.Address)}" && h.Payload == "из кошелька"), "incoming in history");
        Assert.True(history.Any(h => h.Direction == Direction.Outgoing && h.Summary == $"Перевод 0.5 {baseCoin.Symbol} → {History.Short(a.Address)}"), "outgoing in history");
        Console.WriteLine($"  history: {history.Count} entries: {string.Join(" | ", history.Select(h => h.Summary))}");

        // Swap along the node's best route, with the minimum from the user's slippage (1%), not from the quote alone.
        var token = await api.CoinAsync(1);
        if (token != null && await api.BestTradeAsync(0, token.Id, coin, exactInput: true) is { } trade)
        {
            var minimum = trade.Result * 99 / 100;
            var before = (await api.AccountAsync(a.Address)).BalanceOf(token.Id);
            var swap = await wallet.SendAsync(a, await wallet.PreviewAsync(a, new SellSwapPoolData(trade.Path, coin, minimum)));
            Assert.True(swap.Succeeded, $"swap: {swap.State} {swap.Code} {swap.Log}");
            var got = (await api.AccountAsync(a.Address)).BalanceOf(token.Id) - before;
            Assert.True(got >= minimum, "received at least the minimum");
            Console.WriteLine($"  swap {string.Join(" → ", trade.Path.Select(registry.Symbol))}: quoted {Units.FormatCoins(trade.Result)}, got {Units.FormatCoins(got)} {token.Symbol}");
        }
        else Console.WriteLine("  swap: skipped (no pool on this devnet; run the protocol --devnet scenario first)");

        // Refusal is reported, not hidden: a watch-only account cannot sign, a bad transaction comes back with its code.
        var watch = session.AddWatchOnly(TestKeys.Address(4), "Наблюдение");
        await Assert.ThrowsAsync<InvalidOperationException>(() => wallet.SendAsync(watch, send), "watch-only cannot send");
        var refused = await wallet.SendAsync(b, await wallet.PreviewAsync(b, new SellCoinData(0, coin, 0, 0)));
        Assert.Equal((TxState.Rejected, 301u), (refused.State, refused.Code), "refusal code from CheckTx");

        var delegation = await wallet.PreviewAsync(a, new DelegateData((await api.CandidatesAsync())[0].PublicKey, 0, 10 * coin));
        Assert.Equal(delegation.PriceInVoteCoin, delegation.Commission, "delegate commission");
        _ = store;
        Console.WriteLine($"PASS devnet wallet: key store, previews, queue, sends, history and swap work against {status.Version} ({url})");
    }
}
