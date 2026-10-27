using System.Numerics;

namespace Minter.Protocol;

// Commission prices in the order of VoteCommissionDataV3 (the wire order of a vote). Note that state/commission
// Price stores BurnToken before MintToken; the vote has MintToken first.
public enum PriceField
{
    PayloadByte, Send, BuyBancor, SellBancor, SellAllBancor, BuyPoolBase, BuyPoolDelta, SellPoolBase, SellPoolDelta,
    SellAllPoolBase, SellAllPoolDelta, CreateTicker3, CreateTicker4, CreateTicker5, CreateTicker6, CreateTicker7to10,
    CreateCoin, CreateToken, RecreateCoin, RecreateToken, DeclareCandidacy, Delegate, Unbond, RedeemCheck,
    SetCandidateOn, SetCandidateOff, CreateMultisig, MultisendBase, MultisendDelta, EditCandidate, SetHaltBlock,
    EditTickerOwner, EditMultisig, EditCandidatePublicKey, CreateSwapPool, AddLiquidity, RemoveLiquidity,
    EditCandidateCommission, MintToken, BurnToken, VoteCommission, VoteUpdate, FailedTx, AddLimitOrder,
    RemoveLimitOrder, MoveStake, LockStake, Lock,
}

// Prices are denominated in Coin (pip of that coin); the network converts them to the base coin through the
// Coin/BIP pool, and then to the transaction's gas coin. Those conversions need state, so they belong to the source.
public sealed class PriceCommissions
{
    public static IReadOnlyList<PriceField> Fields { get; } = Enum.GetValues<PriceField>();

    // Field name in the node's API v2 (/v2/price_commissions): snake_case, CreateTicker7to10 as create_ticker7_10.
    public static string ApiName(PriceField field)
    {
        if (field == PriceField.CreateTicker7to10) return "create_ticker7_10";
        var name = field.ToString();
        var result = new System.Text.StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0) result.Append('_');
            result.Append(char.ToLowerInvariant(name[i]));
        }
        return result.ToString();
    }
    private readonly BigInteger[] values = new BigInteger[Fields.Count];
    public uint Coin { get; set; }
    public List<BigInteger> More { get; } = [];
    public BigInteger this[PriceField field] { get => values[(int)field]; set => values[(int)field] = value.Sign >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value)); }
}

public static class Commission
{
    // transaction.Price: CommissionData of the data plus PayloadByte for every byte of payload and service data.
    // The commission charged is this times the gas price (Transaction.MulGasPrice), in units of prices.Coin.
    public static BigInteger Price(ITxData data, int payloadAndServiceDataLength, PriceCommissions prices) =>
        DataPrice(data, prices) + payloadAndServiceDataLength * prices[PriceField.PayloadByte];

    public static BigInteger Price(Transaction tx, PriceCommissions prices) =>
        Price(tx.DecodeData(), tx.Payload.Length + tx.ServiceData.Length, prices);

    public static BigInteger WithGasPrice(BigInteger price, uint gasPrice) => price * gasPrice;

    public static BigInteger DataPrice(ITxData data, PriceCommissions p) => data switch
    {
        SendData => p[PriceField.Send],
        SellCoinData => p[PriceField.SellBancor],
        SellAllCoinData => p[PriceField.SellAllBancor],
        BuyCoinData => p[PriceField.BuyBancor],
        CreateCoinData c => Ticker(c.Symbol, p) + p[PriceField.CreateCoin],
        // create_token.go charges the ticker price plus CreateCoin (not CreateToken), as the network does.
        CreateTokenData t => Ticker(t.Symbol, p) + p[PriceField.CreateCoin],
        RecreateCoinData => p[PriceField.RecreateCoin],
        RecreateTokenData => p[PriceField.RecreateToken],
        DeclareCandidacyData => p[PriceField.DeclareCandidacy],
        DelegateData => p[PriceField.Delegate],
        UnbondData => p[PriceField.Unbond],
        MoveStakeData => p[PriceField.MoveStake],
        RedeemCheckData => p[PriceField.RedeemCheck],
        SetCandidateOnlineData => p[PriceField.SetCandidateOn],
        SetCandidateOfflineData => p[PriceField.SetCandidateOff],
        CreateMultisigData => p[PriceField.CreateMultisig],
        EditMultisigData => p[PriceField.EditMultisig],
        MultisendData m => p[PriceField.MultisendBase] + (m.List.Count - 1) * p[PriceField.MultisendDelta],
        EditCandidateData => p[PriceField.EditCandidate],
        EditCandidatePublicKeyData => p[PriceField.EditCandidatePublicKey],
        EditCandidateCommissionData => p[PriceField.EditCandidateCommission],
        SetHaltBlockData => p[PriceField.SetHaltBlock],
        EditCoinOwnerData => p[PriceField.EditTickerOwner],
        AddLiquidityData => p[PriceField.AddLiquidity],
        RemoveLiquidityData => p[PriceField.RemoveLiquidity],
        CreateSwapPoolData => p[PriceField.CreateSwapPool],
        SellSwapPoolData s => p[PriceField.SellPoolBase] + (s.Coins.Count - 2) * p[PriceField.SellPoolDelta],
        BuySwapPoolData b => p[PriceField.BuyPoolBase] + (b.Coins.Count - 2) * p[PriceField.BuyPoolDelta],
        SellAllSwapPoolData a => p[PriceField.SellAllPoolBase] + (a.Coins.Count - 2) * p[PriceField.SellAllPoolDelta],
        MintTokenData => p[PriceField.MintToken],
        BurnTokenData => p[PriceField.BurnToken],
        VoteCommissionData => p[PriceField.VoteCommission],
        VoteUpdateData => p[PriceField.VoteUpdate],
        AddLimitOrderData => p[PriceField.AddLimitOrder],
        RemoveLimitOrderData => p[PriceField.RemoveLimitOrder],
        LockStakeData => p[PriceField.LockStake],
        LockData => p[PriceField.Lock],
        _ => throw new ArgumentException($"No commission rule for {data.GetType().Name}.")
    };

    // PayForSymbol: by the length of the ticker text (trailing zero bytes trimmed).
    private static BigInteger Ticker(CoinSymbol symbol, PriceCommissions p) => symbol.ToString().Length switch
    {
        3 => p[PriceField.CreateTicker3],
        4 => p[PriceField.CreateTicker4],
        5 => p[PriceField.CreateTicker5],
        6 => p[PriceField.CreateTicker6],
        _ => p[PriceField.CreateTicker7to10],
    };
}
