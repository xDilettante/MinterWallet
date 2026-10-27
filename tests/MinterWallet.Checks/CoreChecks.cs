using System.Collections.Concurrent;
using System.Numerics;
using Minter.Protocol;
using MinterWallet.Core;

namespace MinterWallet.Checks;

internal static class QueueChecks
{
    public static async Task Run()
    {
        var chain = new FakeChain();
        var queue = new SendQueue(chain, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(20));
        using var a = new PrivateKey(TestKeys.Bytes(1));
        using var b = new PrivateKey(TestKeys.Bytes(2));
        Func<ulong, Transaction> Builder(PrivateKey key) => nonce =>
        {
            var tx = Transaction.Create(nonce, ChainId.Testnet, 1, 0, new SendData(0, TestKeys.Address(4), 1));
            tx.Sign(key);
            return tx;
        };

        // Five transactions of one sender at once: one at a time, consecutive nonces in submission order, never two in
        // the mempool. Each builder records the nonce it was given, in the order the user submitted them.
        var given = new List<(int Order, ulong Nonce)>();
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 5).Select(i => queue.SubmitAsync(a.Address, nonce => { lock (given) given.Add((i, nonce)); return Builder(a)(nonce); })));
        Assert.True(outcomes.All(o => o.State == TxState.Included), "all included");
        Assert.Equal("1,2,3,4,5", string.Join(",", chain.Nonces(a.Address)), "consecutive nonces");
        Assert.Equal("0:1,1:2,2:3,3:4,4:5", string.Join(",", given.Select(g => $"{g.Order}:{g.Nonce}")), "first come, first served");
        Assert.Equal(0, chain.Code113, "no second transaction of a sender in the mempool");

        // Another sender is not blocked by the first one.
        chain.Delay = TimeSpan.FromMilliseconds(800);
        var slow = queue.SubmitAsync(a.Address, Builder(a));
        await Task.Delay(100);
        Assert.True(queue.IsBusy(a.Address), "sender A busy while its transaction waits for a block");
        chain.Delay = TimeSpan.FromMilliseconds(30);
        var other = await queue.SubmitAsync(b.Address, Builder(b));
        Assert.True(other.Succeeded && !slow.IsCompleted, "sender B goes through while A waits");
        await slow;

        // A refusal comes back at once; a transaction that never lands is reported as lost.
        chain.Refuse = 107;
        var refused = await queue.SubmitAsync(a.Address, Builder(a));
        Assert.Equal((TxState.Rejected, 107u), (refused.State, refused.Code), "refused by CheckTx");
        chain.Refuse = 0; chain.Swallow = true;
        var lost = await queue.SubmitAsync(a.Address, Builder(a));
        Assert.Equal(TxState.Lost, lost.State, "lost");
        chain.Swallow = false; chain.FailInBlock = 302;
        var failed = await queue.SubmitAsync(b.Address, Builder(b));
        Assert.Equal((TxState.FailedInBlock, 302u), (failed.State, failed.Code), "failed in block");

        // A transaction signed by another key is never sent for this sender.
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.SubmitAsync(a.Address, Builder(b)), "wrong signer");
    }
}

internal static class GuardChecks
{
    public static Task Run()
    {
        var gas = new GasPolicy { Cap = 5 };
        Assert.Equal(1u, gas.Choose(0), "gas price at least 1");
        Assert.Equal(5u, gas.Choose(5), "at the cap");
        Assert.Throws<GasPriceTooHighException>(() => gas.Choose(6), "above the cap");

        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "coins.json");
        var registry = new CoinRegistry(path);
        Assert.True(registry.Observe(new CoinRef(1993, "USDTE")) == null, "first sight");
        var conflict = registry.Observe(new CoinRef(4242, "USDTE"));
        Assert.Equal(new CoinConflict("USDTE", 1993, 4242), conflict, "known ticker under another id");
        var reloaded = new CoinRegistry(path);
        Assert.True(reloaded.Observe(new CoinRef(4242, "USDTE")) != null, "remembered across runs");
        Assert.Equal("USDTE", reloaded.Symbol(1993), "symbol by id");
        Assert.True(reloaded.Observe(new CoinRef(77, "BIP")) != null, "base ticker is pinned to id 0");
        reloaded.Accept(conflict!);
        Assert.True(new CoinRegistry(path).Observe(new CoinRef(4242, "USDTE")) == null, "accepted change persists");

        Assert.Equal(ChainId.Mainnet, WalletService.ChainIdOf("minter-mainnet-5"), "mainnet chain id");
        Assert.Equal(ChainId.Testnet, WalletService.ChainIdOf("minter-devnet"), "other networks use 2");
        return Task.CompletedTask;
    }
}

internal static class HistoryChecks
{
    public static Task Run()
    {
        var registry = new CoinRegistry();
        registry.Observe(new CoinRef(1993, "USDTE"));
        string Describe(string name, Address me)
        {
            var hex = Fixtures.Go.GetProperty("transactions").EnumerateArray().Single(t => t.GetProperty("name").GetString() == name).GetProperty("hex").GetString()!;
            var raw = Convert.FromHexString(hex);
            return History.Decode(new TxRecord("Mt", 1, 0, "", raw, new Dictionary<string, string>()), me, registry.Symbol).Summary;
        }
        var sender = TestKeys.Address(1);
        var recipient = Address.Parse("Mxb8aee30fb8c75e725657d04fb8cd7fc21126f11b");
        Assert.Equal("Перевод 0.000000000000000001 BIP → Mxb8ae…f11b", Describe("send-min", sender), "outgoing send");
        Assert.Equal("Получено 0.000000000000000001 BIP от Mx0ee5…100e", Describe("send-min", recipient), "incoming send");
        Assert.Equal("Обмен через пулы: 1 BIP, BIP → USDTE", Describe("sell-swap-pool-2", sender), "swap route");
        Assert.True(Describe("delegate", sender).StartsWith("Делегирование 1 BIP валидатору Mp", StringComparison.Ordinal), "delegate");
        Assert.Equal("Создание токена TOKEN", Describe("create-token", sender), "create token");
        var entry = History.Decode(new TxRecord("Mt", 1, 0, "", Convert.FromHexString(Fixtures.Go.GetProperty("transactions")[2].GetProperty("hex").GetString()!), new Dictionary<string, string>()), sender, registry.Symbol);
        Assert.Equal("привет, Minter ✓", entry.Payload, "payload text");
        return Task.CompletedTask;
    }
}

// In-memory chain for the queue: nonces per sender, a mempool of one transaction per sender (code 113 otherwise),
// a block after Delay.
internal sealed class FakeChain : IChainBackend
{
    private readonly ConcurrentDictionary<Address, ulong> counts = new();
    private readonly ConcurrentDictionary<Address, List<ulong>> nonces = new();
    private readonly ConcurrentDictionary<string, (DateTime At, Transaction Tx)> pending = new();
    private readonly ConcurrentDictionary<Address, string> inMempool = new();
    public TimeSpan Delay = TimeSpan.FromMilliseconds(30);
    public uint Refuse, FailInBlock;
    public bool Swallow;
    public int Code113;
    public IEnumerable<ulong> Nonces(Address a) => nonces.GetValueOrDefault(a) ?? [];

    public string Name => "fake";
    public SourceTrust Trust => SourceTrust.Node;
    public Task<AccountState> AccountAsync(Address address, CancellationToken ct = default) =>
        Task.FromResult(new AccountState(address, counts.GetValueOrDefault(address), [], [], 0));
    public Task<BroadcastResult> SendAsync(Transaction tx, CancellationToken ct = default)
    {
        if (Refuse != 0) return Task.FromResult(new BroadcastResult(Refuse, "refused", tx.Hash));
        var sender = tx.Sender();
        if (!inMempool.TryAdd(sender, tx.Hash)) { Interlocked.Increment(ref Code113); return Task.FromResult(new BroadcastResult(113, "already in mempool", tx.Hash)); }
        if (!Swallow) pending[tx.Hash] = (DateTime.UtcNow + Delay, tx);
        else inMempool.TryRemove(sender, out _);
        return Task.FromResult(new BroadcastResult(0, "", tx.Hash));
    }
    public Task<TxRecord?> TransactionAsync(string hash, CancellationToken ct = default)
    {
        if (!pending.TryGetValue(hash, out var p) || DateTime.UtcNow < p.At) return Task.FromResult<TxRecord?>(null);
        var sender = p.Tx.Sender();
        if (pending.TryRemove(hash, out _))
        {
            counts.AddOrUpdate(sender, 1, (_, c) => c + 1);
            nonces.GetOrAdd(sender, _ => []).Add(p.Tx.Nonce);
            inMempool.TryRemove(sender, out _);
        }
        return Task.FromResult<TxRecord?>(new TxRecord(hash, 1, FailInBlock, FailInBlock == 0 ? "" : "failed", p.Tx.Encode(), new Dictionary<string, string>()));
    }

    public Task<ChainStatus> StatusAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task<CoinDetails?> CoinAsync(uint id, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<CoinDetails?> CoinAsync(string symbol, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<PriceCommissions> PricesAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task<uint> MinGasPriceAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task<SwapEstimate> EstimateSellAsync(uint sell, uint buy, BigInteger value, SwapFrom from, IReadOnlyList<uint>? route = null, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<SwapEstimate> EstimateBuyAsync(uint sell, uint buy, BigInteger value, SwapFrom from, IReadOnlyList<uint>? route = null, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<BestTrade?> BestTradeAsync(uint sell, uint buy, BigInteger amount, bool exactInput, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<BigInteger> EstimateCommissionAsync(Transaction tx, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<TxRecord>> HistoryAsync(Address address, int page, int perPage, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<CandidateInfo>> CandidatesAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<FrozenFunds>> FrozenAsync(Address address, CancellationToken ct = default) => throw new NotSupportedException();
}
