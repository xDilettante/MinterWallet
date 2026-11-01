using System.Numerics;
using Minter.Protocol;
using MinterWallet.Core;

namespace MinterWallet.Checks;

// Arrivals: the first look only remembers; a send names its sender, the bridge's multisend is a deposit, base-coin growth
// of a delegating address with no transaction is a reward, growth after the address's own transaction is not reported,
// growth with nothing behind it is a plain credit, a decrease is nothing.
internal static class IncomeChecks
{
    public static async Task Run()
    {
        var chain = new IncomeChain();
        var watch = new IncomeWatch(chain);
        using var meKey = new PrivateKey(TestKeys.Bytes(5));
        using var friendKey = new PrivateKey(TestKeys.Bytes(6));
        var me = new WalletAccount("a1", "Основной", meKey.Address, null, 0, DateTimeOffset.UnixEpoch);
        var other = new WalletAccount("a2", "Наблюдение", TestKeys.Address(7), null, 0, DateTimeOffset.UnixEpoch);
        chain.Set(me.Address, 0, (0, "100"));
        chain.Set(other.Address, 0, (0, "5"));
        Assert.Equal(0, (await watch.CheckAsync([me, other])).Count, "the first look only remembers");

        // A friend sends 2.5 BIP.
        chain.Height = 10;
        chain.Set(me.Address, 0, (0, "102.5"));
        chain.Records.Add(Record(friendKey, 1, 9, new SendData(0, me.Address, Units.ParseCoins("2.5"))));
        var sent = await watch.CheckAsync([me, other]);
        Assert.True(sent is [{ Kind: ArrivalKind.Transfer } a] && a.Amount == Units.ParseCoins("2.5") && a.From == friendKey.Address && a.Account == me && a.Coin.Symbol == "BIP",
            "a send: the amount and its sender");

        // The bridge credits 18.81 USDTBSC with a multisend to several addresses.
        chain.Height = 20;
        chain.Set(me.Address, 0, (0, "102.5"), (3494, "18.81"));
        chain.Records.Add(new TxRecord("Mtbridge", 19, 0, "", BridgeMultisend(me.Address), new Dictionary<string, string>()));
        var bridged = await watch.CheckAsync([me, other]);
        Assert.True(bridged is [{ Kind: ArrivalKind.Bridge } b] && b.Amount == Units.ParseCoins("18.81") && b.Coin.Id == 3494, "the bridge's multisend is a deposit");

        // A reward: the base coin grows on a delegating address, no transaction, the nonce as it was.
        chain.Height = 30;
        chain.Delegating = true;
        chain.Set(me.Address, 0, (0, "102.6"), (3494, "18.81"));
        Assert.True(await watch.CheckAsync([me, other]) is [{ Kind: ArrivalKind.Reward } r] && r.Amount == Units.ParseCoins("0.1"), "a staking reward");

        // The address's own swap: USDTBSC down, a token up, the nonce moved — not an arrival.
        chain.Height = 40;
        chain.Set(me.Address, 1, (0, "102.5"), (3494, "8.81"), (4000, "10"));
        Assert.Equal(0, (await watch.CheckAsync([me, other])).Count, "own swap not reported");

        // A credit with nothing behind it (an unbond coming back) on a watched address; a decrease is nothing.
        chain.Height = 50;
        chain.Delegating = false;
        chain.Set(other.Address, 0, (0, "15"));
        chain.Set(me.Address, 1, (0, "100"), (3494, "8.81"), (4000, "10"));
        Assert.True(await watch.CheckAsync([me, other]) is [{ Kind: ArrivalKind.Other } o] && o.Account == other && o.Amount == Units.ParseCoins("10"), "a plain credit on a watched address");
    }

    private static TxRecord Record(PrivateKey from, ulong nonce, long height, ITxData data)
    {
        var tx = Transaction.Create(nonce, ChainId.Mainnet, 1, 0, data);
        tx.Sign(from);
        return new TxRecord(tx.Hash, height, 0, "", tx.Encode(), new Dictionary<string, string>());
    }

    // The bridge's multisig signs with several keys; a single-signature stand-in from its address is enough here, as only
    // the sender and the lines are read. Built as a multisig transaction so that its sender is the bridge's address.
    private static byte[] BridgeMultisend(Address to)
    {
        using var member = new PrivateKey(TestKeys.Bytes(8));
        var tx = Transaction.Create(1, ChainId.Mainnet, 1, 0, new MultisendData([new(3494, TestKeys.Address(9), Units.ParseCoins("1")), new(3494, to, Units.ParseCoins("18.81"))]),
            signatureType: SignatureType.Multi);
        tx.SetMultisigAddress(Bridge.Multisig);
        tx.Sign(member);
        return tx.Encode();
    }
}

internal sealed class IncomeChain : IChainBackend
{
    private readonly Dictionary<Address, AccountState> accounts = [];
    public long Height = 1;
    public bool Delegating;
    public readonly List<TxRecord> Records = [];

    public void Set(Address address, ulong nonce, params (uint Coin, string Value)[] balances) =>
        accounts[address] = new AccountState(address, nonce, balances.Select(b => new Balance(new CoinRef(b.Coin, b.Coin == 0 ? "BIP" : $"C{b.Coin}"), Units.ParseCoins(b.Value), 0)).ToList(), [], 0);

    public string Name => "income";
    public SourceTrust Trust => SourceTrust.Node;
    public Task<ChainStatus> StatusAsync(CancellationToken ct = default) => Task.FromResult(new ChainStatus("minter-mainnet-5", "test", Height, DateTimeOffset.UtcNow));
    public Task<AccountState> AccountAsync(Address address, CancellationToken ct = default)
    {
        var state = accounts[address];
        return Task.FromResult(Delegating ? state with { Delegated = [new Delegation(new CoinRef(0, "BIP"), 1, 1)] } : state);
    }
    public Task<IReadOnlyList<TxRecord>> HistoryAsync(Address address, int page, int perPage, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TxRecord>>(Records.ToList());

    public Task<BroadcastResult> SendAsync(Transaction tx, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<TxRecord?> TransactionAsync(string hash, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<CoinDetails?> CoinAsync(uint id, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<CoinDetails?> CoinAsync(string symbol, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<PriceCommissions> PricesAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task<uint> MinGasPriceAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task<SwapEstimate> EstimateSellAsync(uint sell, uint buy, BigInteger value, SwapFrom from, IReadOnlyList<uint>? route = null, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<SwapEstimate> EstimateBuyAsync(uint sell, uint buy, BigInteger value, SwapFrom from, IReadOnlyList<uint>? route = null, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<BigInteger> EstimateSellAllAsync(uint sell, uint buy, BigInteger balance, SwapFrom from, IReadOnlyList<uint>? route = null, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<BestTrade?> BestTradeAsync(uint sell, uint buy, BigInteger amount, bool exactInput, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<BigInteger> EstimateCommissionAsync(Transaction tx, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<DateTimeOffset> BlockTimeAsync(long height, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<StakeInfo>> StakesAsync(Pubkey candidate, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<CandidateInfo>> CandidatesAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<FrozenFunds>> FrozenAsync(Address address, CancellationToken ct = default) => throw new NotSupportedException();
}
