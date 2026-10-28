using System.Numerics;
using Minter.Protocol;

namespace MinterWallet.Core;

// How far an answer can be trusted: a gate executes and verifies every block itself; a node is believed as it answers;
// an estimate is a forecast that the block may change.
public enum SourceTrust { Node, VerifiedGate }

public sealed record CoinRef(uint Id, string Symbol)
{
    public override string ToString() => Id == 0 ? Symbol : $"{Symbol} (id {Id})";
}

public sealed record ChainStatus(string Network, string Version, long Height, DateTimeOffset BlockTime);

public sealed record Balance(CoinRef Coin, BigInteger Value, BigInteger BipValue);
public sealed record Delegation(CoinRef Coin, BigInteger Value, BigInteger BipValue);

// Nonce is the number of transactions sent so far: the next transaction uses Nonce + 1.
public sealed record AccountState(Address Address, ulong Nonce, IReadOnlyList<Balance> Balances, IReadOnlyList<Delegation> Delegated, BigInteger TotalBipValue,
    MultisigInfo? Multisig = null, ulong LockedStakeUntil = 0)
{
    public BigInteger BalanceOf(uint coin) => Balances.FirstOrDefault(b => b.Coin.Id == coin)?.Value ?? BigInteger.Zero;
}

public sealed record CoinDetails(uint Id, string Symbol, string Name, BigInteger Volume, uint Crr, BigInteger Reserve, BigInteger MaxSupply,
    Address? Owner, bool Mintable, bool Burnable);

public enum SwapFrom { Pool, Bancor, Optimal }
public sealed record SwapEstimate(BigInteger Amount, BigInteger Commission, string SwapFrom);
public sealed record BestTrade(IReadOnlyList<uint> Path, BigInteger Result);
public sealed record BroadcastResult(uint Code, string Log, string Hash);

// A multisig address: its members with their weights, and the weight its transactions need.
public sealed record MultisigInfo(uint Threshold, IReadOnlyList<MultisigMember> Members)
{
    public uint WeightOf(IEnumerable<Address> signers) => (uint)Members.Where(m => signers.Contains(m.Address)).Sum(m => (long)m.Weight);
    public bool IsMember(Address address) => Members.Any(m => m.Address == address);
}
public sealed record MultisigMember(Address Address, uint Weight);
public sealed record TxRecord(string Hash, long Height, uint Code, string Log, byte[] Raw, IReadOnlyDictionary<string, string> Tags);
public sealed record CandidateInfo(Pubkey PublicKey, uint Commission, BigInteger TotalStake, int Status, bool Validator, Address Owner,
    Address? Reward = null, Address? Control = null);
public sealed record StakeInfo(Address Owner, CoinRef Coin, BigInteger Value, BigInteger BipValue);
// A coin offered for a swap (MinterGate's /gate/v1/coins): liquidity is its reserve plus the base coin in its pools.
public sealed record CoinListing(uint Id, string Symbol, string Name, bool Token, BigInteger Reserve, BigInteger PoolBase, int Pools)
{
    public BigInteger Liquidity => Reserve + PoolBase;
}
// A swap pool (/v2/swap_pools) and a limit order (/v2/limit_orders): enough to show a provider's share and the
// orders of an address.
public sealed record PoolInfo(uint Id, uint Coin0, uint Coin1, BigInteger Amount0, BigInteger Amount1, BigInteger Liquidity);
public sealed record OrderInfo(uint Id, CoinRef Sell, CoinRef Buy, BigInteger WantSell, BigInteger WantBuy, Address Owner, long Height);
public sealed record FrozenFunds(long UnlockHeight, CoinRef Coin, BigInteger Value, Pubkey? Candidate);

public sealed class ChainApiException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

// A source of chain data and the way out for signed transactions. The wallet never gives it a key; everything that
// goes into a signature comes from the user and is only checked against the source.
public interface IChainBackend
{
    string Name { get; }
    SourceTrust Trust { get; }
    Task<ChainStatus> StatusAsync(CancellationToken ct = default);
    Task<AccountState> AccountAsync(Address address, CancellationToken ct = default);
    Task<CoinDetails?> CoinAsync(uint id, CancellationToken ct = default);
    Task<CoinDetails?> CoinAsync(string symbol, CancellationToken ct = default);
    Task<PriceCommissions> PricesAsync(CancellationToken ct = default);
    Task<uint> MinGasPriceAsync(CancellationToken ct = default);
    Task<SwapEstimate> EstimateSellAsync(uint sell, uint buy, BigInteger value, SwapFrom from, IReadOnlyList<uint>? route = null, CancellationToken ct = default);
    Task<SwapEstimate> EstimateBuyAsync(uint sell, uint buy, BigInteger value, SwapFrom from, IReadOnlyList<uint>? route = null, CancellationToken ct = default);
    // What selling a whole balance gets: the commission is taken in the coin sold and swapped first, as the network does.
    Task<BigInteger> EstimateSellAllAsync(uint sell, uint buy, BigInteger balance, SwapFrom from, IReadOnlyList<uint>? route = null, CancellationToken ct = default);
    Task<BestTrade?> BestTradeAsync(uint sell, uint buy, BigInteger amount, bool exactInput, CancellationToken ct = default);
    // Commission of this transaction in its gas coin. Sign a copy with a throwaway key: the source must not get a valid
    // signature of a transaction the user has not confirmed.
    Task<BigInteger> EstimateCommissionAsync(Transaction tx, CancellationToken ct = default);
    Task<BroadcastResult> SendAsync(Transaction tx, CancellationToken ct = default);
    Task<TxRecord?> TransactionAsync(string hash, CancellationToken ct = default);
    Task<IReadOnlyList<TxRecord>> HistoryAsync(Address address, int page, int perPage, CancellationToken ct = default);
    Task<DateTimeOffset> BlockTimeAsync(long height, CancellationToken ct = default);
    Task<IReadOnlyList<StakeInfo>> StakesAsync(Pubkey candidate, CancellationToken ct = default);
    Task<IReadOnlyList<CandidateInfo>> CandidatesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<FrozenFunds>> FrozenAsync(Address address, CancellationToken ct = default);
}
