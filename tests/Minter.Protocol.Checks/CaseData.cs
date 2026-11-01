using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace Minter.Protocol.Checks;

// Reads the shared case format (field names of the Go structs, see tools/oracle/main.go) into Minter.Protocol records.
internal static class CaseData
{
    public static ITxData Data(TxType type, JsonElement d) => type switch
    {
        TxType.Send => new SendData(U32(d, "Coin"), Addr(d, "To"), Big(d, "Value")),
        TxType.SellCoin => new SellCoinData(U32(d, "CoinToSell"), Big(d, "ValueToSell"), U32(d, "CoinToBuy"), Big(d, "MinimumValueToBuy")),
        TxType.SellAllCoin => new SellAllCoinData(U32(d, "CoinToSell"), U32(d, "CoinToBuy"), Big(d, "MinimumValueToBuy")),
        TxType.BuyCoin => new BuyCoinData(U32(d, "CoinToBuy"), Big(d, "ValueToBuy"), U32(d, "CoinToSell"), Big(d, "MaximumValueToSell")),
        TxType.CreateCoin => new CreateCoinData(Str(d, "Name"), Sym(d, "Symbol"), Big(d, "InitialAmount"), Big(d, "InitialReserve"), U32(d, "ConstantReserveRatio"), Big(d, "MaxSupply")),
        TxType.RecreateCoin => new RecreateCoinData(Str(d, "Name"), Sym(d, "Symbol"), Big(d, "InitialAmount"), Big(d, "InitialReserve"), U32(d, "ConstantReserveRatio"), Big(d, "MaxSupply")),
        TxType.DeclareCandidacy => new DeclareCandidacyData(Addr(d, "Address"), Pk(d, "PubKey"), U32(d, "Commission"), U32(d, "Coin"), Big(d, "Stake")),
        TxType.Delegate => new DelegateData(Pk(d, "PubKey"), U32(d, "Coin"), Big(d, "Value")),
        TxType.Unbond => new UnbondData(Pk(d, "PubKey"), U32(d, "Coin"), Big(d, "Value")),
        TxType.MoveStake => new MoveStakeData(Pk(d, "FromPubKey"), Pk(d, "ToPubKey"), U32(d, "Coin"), Big(d, "Value")),
        TxType.RedeemCheck => new RedeemCheckData(Bytes(d, "RawCheck"), Bytes(d, "Proof")),
        TxType.SetCandidateOnline => new SetCandidateOnlineData(Pk(d, "PubKey")),
        TxType.SetCandidateOffline => new SetCandidateOfflineData(Pk(d, "PubKey")),
        TxType.CreateMultisig => new CreateMultisigData(U32(d, "Threshold"), U32s(d, "Weights"), Addrs(d, "Addresses")),
        TxType.EditMultisig => new EditMultisigData(U32(d, "Threshold"), U32s(d, "Weights"), Addrs(d, "Addresses")),
        TxType.Multisend => new MultisendData(d.GetProperty("List").EnumerateArray().Select(i => new MultisendItem(U32(i, "Coin"), Addr(i, "To"), Big(i, "Value"))).ToArray()),
        TxType.EditCandidate => new EditCandidateData(Pk(d, "PubKey"), Addr(d, "RewardAddress"), Addr(d, "OwnerAddress"), Addr(d, "ControlAddress")),
        TxType.EditCandidatePublicKey => new EditCandidatePublicKeyData(Pk(d, "PubKey"), Pk(d, "NewPubKey")),
        TxType.EditCandidateCommission => new EditCandidateCommissionData(Pk(d, "PubKey"), U32(d, "Commission")),
        TxType.SetHaltBlock => new SetHaltBlockData(Pk(d, "PubKey"), U64(d.GetProperty("Height"))),
        TxType.EditCoinOwner => new EditCoinOwnerData(Sym(d, "Symbol"), Addr(d, "NewOwner")),
        TxType.AddLiquidity => new AddLiquidityData(U32(d, "Coin0"), U32(d, "Coin1"), Big(d, "Volume0"), Big(d, "MaximumVolume1")),
        TxType.RemoveLiquidity => new RemoveLiquidityData(U32(d, "Coin0"), U32(d, "Coin1"), Big(d, "Liquidity"), Big(d, "MinimumVolume0"), Big(d, "MinimumVolume1")),
        TxType.CreateSwapPool => new CreateSwapPoolData(U32(d, "Coin0"), U32(d, "Coin1"), Big(d, "Volume0"), Big(d, "Volume1")),
        TxType.SellSwapPool => new SellSwapPoolData(U32s(d, "Coins"), Big(d, "ValueToSell"), Big(d, "MinimumValueToBuy")),
        TxType.BuySwapPool => new BuySwapPoolData(U32s(d, "Coins"), Big(d, "ValueToBuy"), Big(d, "MaximumValueToSell")),
        TxType.SellAllSwapPool => new SellAllSwapPoolData(U32s(d, "Coins"), Big(d, "MinimumValueToBuy")),
        TxType.MintToken => new MintTokenData(U32(d, "Coin"), Big(d, "Value")),
        TxType.BurnToken => new BurnTokenData(U32(d, "Coin"), Big(d, "Value")),
        TxType.CreateToken => new CreateTokenData(Str(d, "Name"), Sym(d, "Symbol"), Big(d, "InitialAmount"), Big(d, "MaxSupply"), d.GetProperty("Mintable").GetBoolean(), d.GetProperty("Burnable").GetBoolean()),
        TxType.RecreateToken => new RecreateTokenData(Str(d, "Name"), Sym(d, "Symbol"), Big(d, "InitialAmount"), Big(d, "MaxSupply"), d.GetProperty("Mintable").GetBoolean(), d.GetProperty("Burnable").GetBoolean()),
        TxType.VoteCommission => new VoteCommissionData(Pk(d, "PubKey"), U64(d.GetProperty("Height")), Prices(d)),
        TxType.VoteUpdate => new VoteUpdateData(Str(d, "Version"), Pk(d, "PubKey"), U64(d.GetProperty("Height"))),
        TxType.AddLimitOrder => new AddLimitOrderData(U32(d, "CoinToSell"), Big(d, "ValueToSell"), U32(d, "CoinToBuy"), Big(d, "ValueToBuy")),
        TxType.RemoveLimitOrder => new RemoveLimitOrderData(U32(d, "ID")),
        TxType.LockStake => new LockStakeData(),
        TxType.Lock => new LockData(U32(d, "DueBlock"), U32(d, "Coin"), Big(d, "Value")),
        _ => throw new NotSupportedException(type.ToString())
    };

    // Prices object with Go field names (commission.Price or VoteCommissionDataV3); absent prices are zero.
    public static PriceCommissions Prices(JsonElement d)
    {
        var p = new PriceCommissions { Coin = U32(d, "Coin") };
        foreach (var field in PriceCommissions.Fields)
            if (d.TryGetProperty(field.ToString(), out var value)) p[field] = BigInteger.Parse(value.GetString()!, CultureInfo.InvariantCulture);
        if (d.TryGetProperty("More", out var more)) p.More.AddRange(more.EnumerateArray().Select(m => BigInteger.Parse(m.GetString()!, CultureInfo.InvariantCulture)));
        return p;
    }

    public static ulong U64(JsonElement e) => e.ValueKind == JsonValueKind.String ? ulong.Parse(e.GetString()!, CultureInfo.InvariantCulture) : e.GetUInt64();
    public static uint U32(JsonElement d, string name) => checked((uint)U64(d.GetProperty(name)));
    public static byte[] Hex(string text) => Convert.FromHexString(text);
    private static uint[] U32s(JsonElement d, string name) => d.GetProperty(name).EnumerateArray().Select(e => checked((uint)U64(e))).ToArray();
    private static BigInteger Big(JsonElement d, string name) => BigInteger.Parse(d.GetProperty(name).GetString()!, CultureInfo.InvariantCulture);
    private static Address Addr(JsonElement d, string name) => Address.Parse(d.GetProperty(name).GetString()!);
    private static Address[] Addrs(JsonElement d, string name) => d.GetProperty(name).EnumerateArray().Select(a => Address.Parse(a.GetString()!)).ToArray();
    private static Pubkey Pk(JsonElement d, string name) => Pubkey.Parse(d.GetProperty(name).GetString()!);
    private static CoinSymbol Sym(JsonElement d, string name) => CoinSymbol.FromString(d.GetProperty(name).GetString()!);
    private static string Str(JsonElement d, string name) => d.GetProperty(name).GetString()!;
    private static byte[] Bytes(JsonElement d, string name) => Hex(d.GetProperty(name).GetString()!);
}
