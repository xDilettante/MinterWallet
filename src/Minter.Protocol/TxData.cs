using System.Numerics;

namespace Minter.Protocol;

// coreV2/transaction TxType. PriceVote (0x13) is not decoded by V3 (GetDataV3) and is left out.
public enum TxType : byte
{
    Send = 0x01, SellCoin = 0x02, SellAllCoin = 0x03, BuyCoin = 0x04, CreateCoin = 0x05, DeclareCandidacy = 0x06,
    Delegate = 0x07, Unbond = 0x08, RedeemCheck = 0x09, SetCandidateOnline = 0x0A, SetCandidateOffline = 0x0B,
    CreateMultisig = 0x0C, Multisend = 0x0D, EditCandidate = 0x0E, SetHaltBlock = 0x0F, RecreateCoin = 0x10,
    EditCoinOwner = 0x11, EditMultisig = 0x12, EditCandidatePublicKey = 0x14, AddLiquidity = 0x15,
    RemoveLiquidity = 0x16, SellSwapPool = 0x17, BuySwapPool = 0x18, SellAllSwapPool = 0x19,
    EditCandidateCommission = 0x1A, MoveStake = 0x1B, MintToken = 0x1C, BurnToken = 0x1D, CreateToken = 0x1E,
    RecreateToken = 0x1F, VoteCommission = 0x20, VoteUpdate = 0x21, CreateSwapPool = 0x22, AddLimitOrder = 0x23,
    RemoveLimitOrder = 0x24, LockStake = 0x25, Lock = 0x26,
}

// Transaction data of Minter V3: one record per type, fields in the order of the Go struct GetDataV3 returns
// (SendData, DelegateDataV260, UnbondDataV3, RemoveLiquidityV240, VoteCommissionDataV3, ...).
public interface ITxData
{
    TxType Type { get; }
    byte[] Encode();
}

public static class TxData
{
    public static IReadOnlyList<TxType> Types { get; } = Enum.GetValues<TxType>();

    public static ITxData Decode(TxType type, ReadOnlySpan<byte> data)
    {
        var v = Rlp.Decode(data);
        return type switch
        {
            TxType.Send => SendData.Decode(v),
            TxType.SellCoin => SellCoinData.Decode(v),
            TxType.SellAllCoin => SellAllCoinData.Decode(v),
            TxType.BuyCoin => BuyCoinData.Decode(v),
            TxType.CreateCoin => CreateCoinData.Decode(v),
            TxType.DeclareCandidacy => DeclareCandidacyData.Decode(v),
            TxType.Delegate => DelegateData.Decode(v),
            TxType.Unbond => UnbondData.Decode(v),
            TxType.RedeemCheck => RedeemCheckData.Decode(v),
            TxType.SetCandidateOnline => SetCandidateOnlineData.Decode(v),
            TxType.SetCandidateOffline => SetCandidateOfflineData.Decode(v),
            TxType.CreateMultisig => CreateMultisigData.Decode(v),
            TxType.Multisend => MultisendData.Decode(v),
            TxType.EditCandidate => EditCandidateData.Decode(v),
            TxType.SetHaltBlock => SetHaltBlockData.Decode(v),
            TxType.RecreateCoin => RecreateCoinData.Decode(v),
            TxType.EditCoinOwner => EditCoinOwnerData.Decode(v),
            TxType.EditMultisig => EditMultisigData.Decode(v),
            TxType.EditCandidatePublicKey => EditCandidatePublicKeyData.Decode(v),
            TxType.AddLiquidity => AddLiquidityData.Decode(v),
            TxType.RemoveLiquidity => RemoveLiquidityData.Decode(v),
            TxType.SellSwapPool => SellSwapPoolData.Decode(v),
            TxType.BuySwapPool => BuySwapPoolData.Decode(v),
            TxType.SellAllSwapPool => SellAllSwapPoolData.Decode(v),
            TxType.EditCandidateCommission => EditCandidateCommissionData.Decode(v),
            TxType.MoveStake => MoveStakeData.Decode(v),
            TxType.MintToken => MintTokenData.Decode(v),
            TxType.BurnToken => BurnTokenData.Decode(v),
            TxType.CreateToken => CreateTokenData.Decode(v),
            TxType.RecreateToken => RecreateTokenData.Decode(v),
            TxType.VoteCommission => VoteCommissionData.Decode(v),
            TxType.VoteUpdate => VoteUpdateData.Decode(v),
            TxType.CreateSwapPool => CreateSwapPoolData.Decode(v),
            TxType.AddLimitOrder => AddLimitOrderData.Decode(v),
            TxType.RemoveLimitOrder => RemoveLimitOrderData.Decode(v),
            TxType.LockStake => LockStakeData.Decode(v),
            TxType.Lock => LockData.Decode(v),
            _ => throw new FormatException($"Unsupported transaction type 0x{(byte)type:X2}.")
        };
    }

    internal static Address Addr(RlpItem v) => new(v.Fixed(Address.Length));
    internal static Pubkey Pk(RlpItem v) => new(v.Fixed(Pubkey.Length));
    internal static CoinSymbol Sym(RlpItem v) => CoinSymbol.FromBytes(v.Fixed(CoinSymbol.Length));
    internal static uint[] Coins(RlpItem v) => v.Items.Select(c => c.UInt32()).ToArray();
    internal static byte[] Coins(IEnumerable<uint> coins) => Rlp.List(coins.Select(c => Rlp.Uint(c)));
}

public sealed record SendData(uint Coin, Address To, BigInteger Value) : ITxData
{
    public TxType Type => TxType.Send;
    public byte[] Encode() => Rlp.List(Rlp.Uint(Coin), Rlp.Bytes(To.Span), Rlp.Big(Value));
    internal static SendData Decode(RlpItem v) => new(v.ExpectCount(3)[0].UInt32(), TxData.Addr(v[1]), v[2].Big());
}

public sealed record SellCoinData(uint CoinToSell, BigInteger ValueToSell, uint CoinToBuy, BigInteger MinimumValueToBuy) : ITxData
{
    public TxType Type => TxType.SellCoin;
    public byte[] Encode() => Rlp.List(Rlp.Uint(CoinToSell), Rlp.Big(ValueToSell), Rlp.Uint(CoinToBuy), Rlp.Big(MinimumValueToBuy));
    internal static SellCoinData Decode(RlpItem v) => new(v.ExpectCount(4)[0].UInt32(), v[1].Big(), v[2].UInt32(), v[3].Big());
}

public sealed record SellAllCoinData(uint CoinToSell, uint CoinToBuy, BigInteger MinimumValueToBuy) : ITxData
{
    public TxType Type => TxType.SellAllCoin;
    public byte[] Encode() => Rlp.List(Rlp.Uint(CoinToSell), Rlp.Uint(CoinToBuy), Rlp.Big(MinimumValueToBuy));
    internal static SellAllCoinData Decode(RlpItem v) => new(v.ExpectCount(3)[0].UInt32(), v[1].UInt32(), v[2].Big());
}

public sealed record BuyCoinData(uint CoinToBuy, BigInteger ValueToBuy, uint CoinToSell, BigInteger MaximumValueToSell) : ITxData
{
    public TxType Type => TxType.BuyCoin;
    public byte[] Encode() => Rlp.List(Rlp.Uint(CoinToBuy), Rlp.Big(ValueToBuy), Rlp.Uint(CoinToSell), Rlp.Big(MaximumValueToSell));
    internal static BuyCoinData Decode(RlpItem v) => new(v.ExpectCount(4)[0].UInt32(), v[1].Big(), v[2].UInt32(), v[3].Big());
}

public sealed record CreateCoinData(string Name, CoinSymbol Symbol, BigInteger InitialAmount, BigInteger InitialReserve, uint ConstantReserveRatio, BigInteger MaxSupply) : ITxData
{
    public TxType Type => TxType.CreateCoin;
    public byte[] Encode() => Rlp.List(Rlp.String(Name), Rlp.Bytes(Symbol.Span), Rlp.Big(InitialAmount), Rlp.Big(InitialReserve), Rlp.Uint(ConstantReserveRatio), Rlp.Big(MaxSupply));
    internal static CreateCoinData Decode(RlpItem v) => new(v.ExpectCount(6)[0].Utf8(), TxData.Sym(v[1]), v[2].Big(), v[3].Big(), v[4].UInt32(), v[5].Big());
}

public sealed record RecreateCoinData(string Name, CoinSymbol Symbol, BigInteger InitialAmount, BigInteger InitialReserve, uint ConstantReserveRatio, BigInteger MaxSupply) : ITxData
{
    public TxType Type => TxType.RecreateCoin;
    public byte[] Encode() => Rlp.List(Rlp.String(Name), Rlp.Bytes(Symbol.Span), Rlp.Big(InitialAmount), Rlp.Big(InitialReserve), Rlp.Uint(ConstantReserveRatio), Rlp.Big(MaxSupply));
    internal static RecreateCoinData Decode(RlpItem v) => new(v.ExpectCount(6)[0].Utf8(), TxData.Sym(v[1]), v[2].Big(), v[3].Big(), v[4].UInt32(), v[5].Big());
}

public sealed record DeclareCandidacyData(Address Address, Pubkey PubKey, uint Commission, uint Coin, BigInteger Stake) : ITxData
{
    public TxType Type => TxType.DeclareCandidacy;
    public byte[] Encode() => Rlp.List(Rlp.Bytes(Address.Span), Rlp.Bytes(PubKey.Span), Rlp.Uint(Commission), Rlp.Uint(Coin), Rlp.Big(Stake));
    internal static DeclareCandidacyData Decode(RlpItem v) => new(TxData.Addr(v.ExpectCount(5)[0]), TxData.Pk(v[1]), v[2].UInt32(), v[3].UInt32(), v[4].Big());
}

public sealed record DelegateData(Pubkey PubKey, uint Coin, BigInteger Value) : ITxData
{
    public TxType Type => TxType.Delegate;
    public byte[] Encode() => Rlp.List(Rlp.Bytes(PubKey.Span), Rlp.Uint(Coin), Rlp.Big(Value));
    internal static DelegateData Decode(RlpItem v) => new(TxData.Pk(v.ExpectCount(3)[0]), v[1].UInt32(), v[2].Big());
}

public sealed record UnbondData(Pubkey PubKey, uint Coin, BigInteger Value) : ITxData
{
    public TxType Type => TxType.Unbond;
    public byte[] Encode() => Rlp.List(Rlp.Bytes(PubKey.Span), Rlp.Uint(Coin), Rlp.Big(Value));
    internal static UnbondData Decode(RlpItem v) => new(TxData.Pk(v.ExpectCount(3)[0]), v[1].UInt32(), v[2].Big());
}

public sealed record MoveStakeData(Pubkey FromPubKey, Pubkey ToPubKey, uint Coin, BigInteger Value) : ITxData
{
    public TxType Type => TxType.MoveStake;
    public byte[] Encode() => Rlp.List(Rlp.Bytes(FromPubKey.Span), Rlp.Bytes(ToPubKey.Span), Rlp.Uint(Coin), Rlp.Big(Value));
    internal static MoveStakeData Decode(RlpItem v) => new(TxData.Pk(v.ExpectCount(4)[0]), TxData.Pk(v[1]), v[2].UInt32(), v[3].Big());
}

public sealed record RedeemCheckData(byte[] RawCheck, byte[] Proof) : ITxData
{
    public TxType Type => TxType.RedeemCheck;
    public byte[] Encode()
    {
        if (Proof.Length != 65) throw new InvalidOperationException("A check proof is 65 bytes.");
        return Rlp.List(Rlp.Bytes(RawCheck), Rlp.Bytes(Proof));
    }
    internal static RedeemCheckData Decode(RlpItem v) => new(v.ExpectCount(2)[0].ToBytes(), v[1].Fixed(65));
}

public sealed record SetCandidateOnlineData(Pubkey PubKey) : ITxData
{
    public TxType Type => TxType.SetCandidateOnline;
    public byte[] Encode() => Rlp.List(Rlp.Bytes(PubKey.Span));
    internal static SetCandidateOnlineData Decode(RlpItem v) => new(TxData.Pk(v.ExpectCount(1)[0]));
}

public sealed record SetCandidateOfflineData(Pubkey PubKey) : ITxData
{
    public TxType Type => TxType.SetCandidateOffline;
    public byte[] Encode() => Rlp.List(Rlp.Bytes(PubKey.Span));
    internal static SetCandidateOfflineData Decode(RlpItem v) => new(TxData.Pk(v.ExpectCount(1)[0]));
}

public sealed record CreateMultisigData(uint Threshold, IReadOnlyList<uint> Weights, IReadOnlyList<Address> Addresses) : ITxData
{
    public TxType Type => TxType.CreateMultisig;
    public byte[] Encode() => Multisig.Encode(Threshold, Weights, Addresses);
    internal static CreateMultisigData Decode(RlpItem v) { var (t, w, a) = Multisig.Decode(v); return new(t, w, a); }
}

public sealed record EditMultisigData(uint Threshold, IReadOnlyList<uint> Weights, IReadOnlyList<Address> Addresses) : ITxData
{
    public TxType Type => TxType.EditMultisig;
    public byte[] Encode() => Multisig.Encode(Threshold, Weights, Addresses);
    internal static EditMultisigData Decode(RlpItem v) { var (t, w, a) = Multisig.Decode(v); return new(t, w, a); }
}

internal static class Multisig
{
    public static byte[] Encode(uint threshold, IReadOnlyList<uint> weights, IReadOnlyList<Address> addresses) =>
        Rlp.List(Rlp.Uint(threshold), Rlp.List(weights.Select(w => Rlp.Uint(w))), Rlp.List(addresses.Select(a => Rlp.Bytes(a.Span))));
    public static (uint, uint[], Address[]) Decode(RlpItem v) =>
        (v.ExpectCount(3)[0].UInt32(), v[1].Items.Select(w => w.UInt32()).ToArray(), v[2].Items.Select(TxData.Addr).ToArray());
}

public sealed record MultisendItem(uint Coin, Address To, BigInteger Value);

public sealed record MultisendData(IReadOnlyList<MultisendItem> List) : ITxData
{
    public TxType Type => TxType.Multisend;
    public byte[] Encode() => Rlp.List(Rlp.List(List.Select(i => Rlp.List(Rlp.Uint(i.Coin), Rlp.Bytes(i.To.Span), Rlp.Big(i.Value)))));
    internal static MultisendData Decode(RlpItem v) =>
        new(v.ExpectCount(1)[0].Items.Select(i => new MultisendItem(i.ExpectCount(3)[0].UInt32(), TxData.Addr(i[1]), i[2].Big())).ToArray());
}

public sealed record EditCandidateData(Pubkey PubKey, Address RewardAddress, Address OwnerAddress, Address ControlAddress) : ITxData
{
    public TxType Type => TxType.EditCandidate;
    public byte[] Encode() => Rlp.List(Rlp.Bytes(PubKey.Span), Rlp.Bytes(RewardAddress.Span), Rlp.Bytes(OwnerAddress.Span), Rlp.Bytes(ControlAddress.Span));
    internal static EditCandidateData Decode(RlpItem v) => new(TxData.Pk(v.ExpectCount(4)[0]), TxData.Addr(v[1]), TxData.Addr(v[2]), TxData.Addr(v[3]));
}

public sealed record EditCandidatePublicKeyData(Pubkey PubKey, Pubkey NewPubKey) : ITxData
{
    public TxType Type => TxType.EditCandidatePublicKey;
    public byte[] Encode() => Rlp.List(Rlp.Bytes(PubKey.Span), Rlp.Bytes(NewPubKey.Span));
    internal static EditCandidatePublicKeyData Decode(RlpItem v) => new(TxData.Pk(v.ExpectCount(2)[0]), TxData.Pk(v[1]));
}

public sealed record EditCandidateCommissionData(Pubkey PubKey, uint Commission) : ITxData
{
    public TxType Type => TxType.EditCandidateCommission;
    public byte[] Encode() => Rlp.List(Rlp.Bytes(PubKey.Span), Rlp.Uint(Commission));
    internal static EditCandidateCommissionData Decode(RlpItem v) => new(TxData.Pk(v.ExpectCount(2)[0]), v[1].UInt32());
}

public sealed record SetHaltBlockData(Pubkey PubKey, ulong Height) : ITxData
{
    public TxType Type => TxType.SetHaltBlock;
    public byte[] Encode() => Rlp.List(Rlp.Bytes(PubKey.Span), Rlp.Uint(Height));
    internal static SetHaltBlockData Decode(RlpItem v) => new(TxData.Pk(v.ExpectCount(2)[0]), v[1].UInt64());
}

public sealed record EditCoinOwnerData(CoinSymbol Symbol, Address NewOwner) : ITxData
{
    public TxType Type => TxType.EditCoinOwner;
    public byte[] Encode() => Rlp.List(Rlp.Bytes(Symbol.Span), Rlp.Bytes(NewOwner.Span));
    internal static EditCoinOwnerData Decode(RlpItem v) => new(TxData.Sym(v.ExpectCount(2)[0]), TxData.Addr(v[1]));
}

public sealed record AddLiquidityData(uint Coin0, uint Coin1, BigInteger Volume0, BigInteger MaximumVolume1) : ITxData
{
    public TxType Type => TxType.AddLiquidity;
    public byte[] Encode() => Rlp.List(Rlp.Uint(Coin0), Rlp.Uint(Coin1), Rlp.Big(Volume0), Rlp.Big(MaximumVolume1));
    internal static AddLiquidityData Decode(RlpItem v) => new(v.ExpectCount(4)[0].UInt32(), v[1].UInt32(), v[2].Big(), v[3].Big());
}

public sealed record RemoveLiquidityData(uint Coin0, uint Coin1, BigInteger Liquidity, BigInteger MinimumVolume0, BigInteger MinimumVolume1) : ITxData
{
    public TxType Type => TxType.RemoveLiquidity;
    public byte[] Encode() => Rlp.List(Rlp.Uint(Coin0), Rlp.Uint(Coin1), Rlp.Big(Liquidity), Rlp.Big(MinimumVolume0), Rlp.Big(MinimumVolume1));
    internal static RemoveLiquidityData Decode(RlpItem v) => new(v.ExpectCount(5)[0].UInt32(), v[1].UInt32(), v[2].Big(), v[3].Big(), v[4].Big());
}

public sealed record CreateSwapPoolData(uint Coin0, uint Coin1, BigInteger Volume0, BigInteger Volume1) : ITxData
{
    public TxType Type => TxType.CreateSwapPool;
    public byte[] Encode() => Rlp.List(Rlp.Uint(Coin0), Rlp.Uint(Coin1), Rlp.Big(Volume0), Rlp.Big(Volume1));
    internal static CreateSwapPoolData Decode(RlpItem v) => new(v.ExpectCount(4)[0].UInt32(), v[1].UInt32(), v[2].Big(), v[3].Big());
}

public sealed record SellSwapPoolData(IReadOnlyList<uint> Coins, BigInteger ValueToSell, BigInteger MinimumValueToBuy) : ITxData
{
    public TxType Type => TxType.SellSwapPool;
    public byte[] Encode() => Rlp.List(TxData.Coins(Coins), Rlp.Big(ValueToSell), Rlp.Big(MinimumValueToBuy));
    internal static SellSwapPoolData Decode(RlpItem v) => new(TxData.Coins(v.ExpectCount(3)[0]), v[1].Big(), v[2].Big());
}

public sealed record BuySwapPoolData(IReadOnlyList<uint> Coins, BigInteger ValueToBuy, BigInteger MaximumValueToSell) : ITxData
{
    public TxType Type => TxType.BuySwapPool;
    public byte[] Encode() => Rlp.List(TxData.Coins(Coins), Rlp.Big(ValueToBuy), Rlp.Big(MaximumValueToSell));
    internal static BuySwapPoolData Decode(RlpItem v) => new(TxData.Coins(v.ExpectCount(3)[0]), v[1].Big(), v[2].Big());
}

public sealed record SellAllSwapPoolData(IReadOnlyList<uint> Coins, BigInteger MinimumValueToBuy) : ITxData
{
    public TxType Type => TxType.SellAllSwapPool;
    public byte[] Encode() => Rlp.List(TxData.Coins(Coins), Rlp.Big(MinimumValueToBuy));
    internal static SellAllSwapPoolData Decode(RlpItem v) => new(TxData.Coins(v.ExpectCount(2)[0]), v[1].Big());
}

public sealed record MintTokenData(uint Coin, BigInteger Value) : ITxData
{
    public TxType Type => TxType.MintToken;
    public byte[] Encode() => Rlp.List(Rlp.Uint(Coin), Rlp.Big(Value));
    internal static MintTokenData Decode(RlpItem v) => new(v.ExpectCount(2)[0].UInt32(), v[1].Big());
}

public sealed record BurnTokenData(uint Coin, BigInteger Value) : ITxData
{
    public TxType Type => TxType.BurnToken;
    public byte[] Encode() => Rlp.List(Rlp.Uint(Coin), Rlp.Big(Value));
    internal static BurnTokenData Decode(RlpItem v) => new(v.ExpectCount(2)[0].UInt32(), v[1].Big());
}

public sealed record CreateTokenData(string Name, CoinSymbol Symbol, BigInteger InitialAmount, BigInteger MaxSupply, bool Mintable, bool Burnable) : ITxData
{
    public TxType Type => TxType.CreateToken;
    public byte[] Encode() => Rlp.List(Rlp.String(Name), Rlp.Bytes(Symbol.Span), Rlp.Big(InitialAmount), Rlp.Big(MaxSupply), Rlp.Bool(Mintable), Rlp.Bool(Burnable));
    internal static CreateTokenData Decode(RlpItem v) => new(v.ExpectCount(6)[0].Utf8(), TxData.Sym(v[1]), v[2].Big(), v[3].Big(), v[4].Bool(), v[5].Bool());
}

public sealed record RecreateTokenData(string Name, CoinSymbol Symbol, BigInteger InitialAmount, BigInteger MaxSupply, bool Mintable, bool Burnable) : ITxData
{
    public TxType Type => TxType.RecreateToken;
    public byte[] Encode() => Rlp.List(Rlp.String(Name), Rlp.Bytes(Symbol.Span), Rlp.Big(InitialAmount), Rlp.Big(MaxSupply), Rlp.Bool(Mintable), Rlp.Bool(Burnable));
    internal static RecreateTokenData Decode(RlpItem v) => new(v.ExpectCount(6)[0].Utf8(), TxData.Sym(v[1]), v[2].Big(), v[3].Big(), v[4].Bool(), v[5].Bool());
}

// VoteCommissionDataV3: validator, height, commission coin, the prices in PriceField order, then rlp:"tail" More.
public sealed record VoteCommissionData(Pubkey PubKey, ulong Height, PriceCommissions Prices) : ITxData
{
    public TxType Type => TxType.VoteCommission;
    public byte[] Encode() => Rlp.List([Rlp.Bytes(PubKey.Span), Rlp.Uint(Height), Rlp.Uint(Prices.Coin),
        .. PriceCommissions.Fields.Select(f => Rlp.Big(Prices[f])), .. Prices.More.Select(Rlp.Big)]);
    internal static VoteCommissionData Decode(RlpItem v)
    {
        var count = PriceCommissions.Fields.Count;
        if (v.Count < 3 + count) throw new FormatException("VoteCommission has too few elements.");
        var prices = new PriceCommissions { Coin = v[2].UInt32() };
        foreach (var f in PriceCommissions.Fields) prices[f] = v[3 + (int)f].Big();
        for (var i = 3 + count; i < v.Count; i++) prices.More.Add(v[i].Big());
        return new(TxData.Pk(v[0]), v[1].UInt64(), prices);
    }
}

public sealed record VoteUpdateData(string Version, Pubkey PubKey, ulong Height) : ITxData
{
    public TxType Type => TxType.VoteUpdate;
    public byte[] Encode() => Rlp.List(Rlp.String(Version), Rlp.Bytes(PubKey.Span), Rlp.Uint(Height));
    internal static VoteUpdateData Decode(RlpItem v) => new(v.ExpectCount(3)[0].Utf8(), TxData.Pk(v[1]), v[2].UInt64());
}

public sealed record AddLimitOrderData(uint CoinToSell, BigInteger ValueToSell, uint CoinToBuy, BigInteger ValueToBuy) : ITxData
{
    public TxType Type => TxType.AddLimitOrder;
    public byte[] Encode() => Rlp.List(Rlp.Uint(CoinToSell), Rlp.Big(ValueToSell), Rlp.Uint(CoinToBuy), Rlp.Big(ValueToBuy));
    internal static AddLimitOrderData Decode(RlpItem v) => new(v.ExpectCount(4)[0].UInt32(), v[1].Big(), v[2].UInt32(), v[3].Big());
}

public sealed record RemoveLimitOrderData(uint Id) : ITxData
{
    public TxType Type => TxType.RemoveLimitOrder;
    public byte[] Encode() => Rlp.List(Rlp.Uint(Id));
    internal static RemoveLimitOrderData Decode(RlpItem v) => new(v.ExpectCount(1)[0].UInt32());
}

public sealed record LockStakeData : ITxData
{
    public TxType Type => TxType.LockStake;
    public byte[] Encode() => Rlp.List();
    internal static LockStakeData Decode(RlpItem v) { v.ExpectCount(0); return new(); }
}

public sealed record LockData(uint DueBlock, uint Coin, BigInteger Value) : ITxData
{
    public TxType Type => TxType.Lock;
    public byte[] Encode() => Rlp.List(Rlp.Uint(DueBlock), Rlp.Uint(Coin), Rlp.Big(Value));
    internal static LockData Decode(RlpItem v) => new(v.ExpectCount(3)[0].UInt32(), v[1].UInt32(), v[2].Big());
}
