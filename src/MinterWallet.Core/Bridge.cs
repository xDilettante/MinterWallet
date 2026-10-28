using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Minter.Protocol;

namespace MinterWallet.Core;

public enum BridgeNetwork { Ethereum, Bsc }

// A Minter coin the bridge carries between Minter and a network, as the ERC-20 token at Contract there with Decimals of
// its own (on Minter every bridged coin has 18).
public sealed record BridgeToken(uint MinterId, string Denom, BridgeNetwork Network, string Contract, int Decimals);

// A transaction at the bridge's Minter address as the explorer lists it: a withdrawal (a send with the command in the
// payload) or the bridge's own multisend (its fees and deposits from the other networks).
public sealed record BridgeTx(string Hash, long Height, DateTimeOffset Time, int Type, Address From, uint Coin, BigInteger Value, string Payload,
    IReadOnlyList<BridgeOut> Outs);
public sealed record BridgeOut(Address To, uint Coin, BigInteger Value);

// What the bridge kept of a withdrawal: its 1 % and the gas of the destination network.
public sealed record BridgeSettlement(BigInteger HubFee, BigInteger GasFee, string Hash, long Height);

// Minter Hub (github.com/MinterTeam/mhub2): a withdrawal to Ethereum or BSC is an ordinary send of the coin to the
// bridge's Minter multisig with {"type":"send_to_ethereum"|"send_to_bsc","recipient":"0x…"} as the payload. A payload the
// bridge cannot read is skipped (minter-connector), not returned, so it is built here and nowhere else. The bridge keeps
// 1 % and the destination gas, which it works out itself (withdrawals of October 2026 carry no "fee", as here), and
// reports both in a multisend from the multisig shortly after.
public static class Bridge
{
    public static readonly Address Multisig = Address.Parse("Mx68f4839d7f32831b9234f9575f3b95e1afe21a56");
    public static readonly Address FeeCollector = Address.Parse("Mxe837525f14a2c9d7a7b5685c20c41e13dab63385");

    // The same 20 bytes on Ethereum and BSC: a Minter key (and a phrase, by the same m/44'/60'/0'/0/i path) holds that
    // address there too, as MetaMask shows it.
    public static string OwnAddress(Address minter) => Checksum("0x" + minter.ToString()[2..]);

    // The coins the bridge carries, as hub.mnst.club/bridge-api/tokens lists them (the bridge's own list): this snapshot of
    // 28.10.2026 until the list is read (Use).
    public static IReadOnlyList<BridgeToken> Tokens { get; private set; } =
    [
        new(1993, "usdt", BridgeNetwork.Ethereum, "0xdac17f958d2ee523a2206206994597c13d831ec7", 6),
        new(1994, "usdc", BridgeNetwork.Ethereum, "0xa0b86991c6218b36c1d19d4a2e9eb0ce3606eb48", 6),
        new(2064, "btc", BridgeNetwork.Ethereum, "0x2260fac5e5542a773aa44fbcfedf7c193bc2c599", 8),
        new(2065, "eth", BridgeNetwork.Ethereum, "0xc02aaa39b223fe8d0a0e5c4f27ead9083c756cc2", 18),
        new(1995, "busd", BridgeNetwork.Ethereum, "0x4fabb145d64652a948d72533023f6e7a623c7c53", 18),
        new(1996, "dai", BridgeNetwork.Ethereum, "0x6b175474e89094c44da98b954eedeac495271d0f", 18),
        new(1997, "ust", BridgeNetwork.Ethereum, "0xa47c8bf37f92abed4a126bda807a7b7498661acd", 18),
        new(1998, "pax", BridgeNetwork.Ethereum, "0x8e870d67f660d95d5be530380d0ec0bd388289e1", 18),
        new(1999, "tusd", BridgeNetwork.Ethereum, "0x0000000000085d4780b73119b644ae5ecd22b376", 18),
        new(0, "bip", BridgeNetwork.Ethereum, "0xcafe34bae6f1b23a6b575303edcc0578d2188131", 18),
        new(3324, "ton", BridgeNetwork.Ethereum, "0x582d872a1b094fc48f5de31d3b73f2d9be47def1", 9),
        new(3258, "shib", BridgeNetwork.Ethereum, "0x95ad61b0a150d79219dcf64e1e6cc01f0b64c4ce", 18),
        new(0, "bip", BridgeNetwork.Bsc, "0xf2ba89a6f9670459ed5aeefbd8db52be912228b8", 18),
        new(2107, "bnb", BridgeNetwork.Bsc, "0xbb4cdb9cbd36b01bd1cbaebf2de08d9173bc095c", 18),
        new(3494, "usdtb", BridgeNetwork.Bsc, "0x55d398326f99059ff775485246999027b3197955", 18),
        new(3491, "busdb", BridgeNetwork.Bsc, "0xe9e7cea3dedca5984780bafc599bd69add087d56", 18),
        new(3474, "btcbsc", BridgeNetwork.Bsc, "0x7130d2a12b9bcbfae4f2634d864a1ee1ce3ead9c", 18),
        new(3475, "ethbsc", BridgeNetwork.Bsc, "0x2170ed0880ac9a755fd29b2688956bd959f933f8", 18),
        new(3476, "tonbsc", BridgeNetwork.Bsc, "0x76a797a59ba2c17726896976b7b3747bfd1d220f", 9),
        new(3481, "unibsc", BridgeNetwork.Bsc, "0xbf5140a22578168fd562dccf235e5d43a02ce9b1", 18),
        new(3478, "dogebsc", BridgeNetwork.Bsc, "0xba2ae424d960c26247dd6c32edc70b295c744c43", 8),
        new(3483, "dotbsc", BridgeNetwork.Bsc, "0x7083609fce4d1d8dc0c979aab8c869ea2c873402", 18),
        new(3665, "adabsc", BridgeNetwork.Bsc, "0x3ee2200efb3400fabb9aacf31297cbdd1d435d47", 18),
        new(3667, "xrpbsc", BridgeNetwork.Bsc, "0x1d2f0da169ceb9fc7b3144628db156f3f6c60dbe", 18),
        new(3482, "ltcbsc", BridgeNetwork.Bsc, "0x4338665cbb7b2485a8855a139b75d5e34ab0db94", 18),
        new(3690, "twtbsc", BridgeNetwork.Bsc, "0x4b0f1812e5df2a09796481ff14017e6005508003", 18),
        new(3702, "nearbsc", BridgeNetwork.Bsc, "0x1fa4a73a3f0133f0025378af00236f3abdee5d63", 18),
        new(3485, "usdcbsc", BridgeNetwork.Bsc, "0x8ac76a51cc950d9822d68b83fe1ad97b32cd580d", 18),
        new(3723, "nftbsc", BridgeNetwork.Bsc, "0x1fc9004ec7e5722891f5f38bae7678efcb11d34d", 6),
        new(3489, "daibsc", BridgeNetwork.Bsc, "0x1af3f329e8be154074d8769d1ffa4ee058b1dbc3", 18),
        new(3764, "lunabsc", BridgeNetwork.Bsc, "0x156ab3346823b651294766e23e6cf87254d68962", 6),
        new(3486, "ustbsc", BridgeNetwork.Bsc, "0x23396cf899ca06c4472205fc903bdb4de249d6fc", 18),
        new(3746, "gmtbsc", BridgeNetwork.Bsc, "0x3019bf2a2ef8040c242c9a4c5c4bd4c81678b2a1", 8),
        new(4253, "cashback", BridgeNetwork.Bsc, "0xa757a06297a26197b6920a21c0f0c505c1360458", 18),
    ];

    // A network missing from the new list keeps its tokens.
    public static void Use(IReadOnlyList<BridgeToken> tokens) =>
        Tokens = [.. Enum.GetValues<BridgeNetwork>().SelectMany(n => tokens.Any(t => t.Network == n) ? tokens.Where(t => t.Network == n) : Tokens.Where(t => t.Network == n))];

    // One network's list of /bridge-api/tokens?chain_id=ethereum|bsc: {"tokens":[{"denom", "chains":{"bsc":{"token_id",
    // "decimals"}, "minter":{"token_id"}}}]}; entries without both ends are skipped.
    public static IReadOnlyList<BridgeToken> ParseTokens(string json, BridgeNetwork network)
    {
        var key = network == BridgeNetwork.Bsc ? "bsc" : "ethereum";
        var result = new List<BridgeToken>();
        using var doc = JsonDocument.Parse(json);
        foreach (var t in doc.RootElement.GetProperty("tokens").EnumerateArray())
        {
            if (!t.TryGetProperty("chains", out var chains) || !chains.TryGetProperty(key, out var there) || !chains.TryGetProperty("minter", out var minter)) continue;
            if (!uint.TryParse(Text(minter, "token_id"), CultureInfo.InvariantCulture, out var id) || Text(there, "token_id") is not { } contract || RecipientProblem(contract) != null
                || !int.TryParse(Text(there, "decimals"), CultureInfo.InvariantCulture, out var decimals) || decimals is < 0 or > 36) continue;
            result.Add(new BridgeToken(id, Text(t, "denom") ?? $"#{id}", network, contract.ToLowerInvariant(), decimals));
        }
        return result;

        static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.Number ? v.GetRawText() : v.GetString() : null;
    }

    // Deposits: the bridge's contract takes a token approved to it and records the Minter address it goes to.
    public static string ContractOf(BridgeNetwork network) => network == BridgeNetwork.Bsc ? "0xf5b0ed82a0b3e11567081694cc66c3df133f7c8f" : "0x897c27fa372aa730d4c75b1243e7ea38879194e2";
    public static ulong ChainIdOf(BridgeNetwork network) => network == BridgeNetwork.Bsc ? 56UL : 1UL;
    public static string NativeOf(BridgeNetwork network) => network == BridgeNetwork.Bsc ? "BNB" : "ETH";
    // The wrapped native coin (WETH, WBNB): the bridge takes it as a token; the wallet wraps the native coin first.
    public static string WrappedOf(BridgeNetwork network) => network == BridgeNetwork.Bsc ? "0xbb4cdb9cbd36b01bd1cbaebf2de08d9173bc095c" : "0xc02aaa39b223fe8d0a0e5c4f27ead9083c756cc2";
    public static bool IsWrappedNative(BridgeToken token) => string.Equals(token.Contract, WrappedOf(token.Network), StringComparison.OrdinalIgnoreCase);
    public static IEnumerable<BridgeToken> TokensOf(BridgeNetwork network) => Tokens.Where(t => t.Network == network);

    public static byte[] BalanceOfCall(Address owner) => Abi.Call("balanceOf(address)", Abi.Address(owner));
    public static byte[] AllowanceCall(Address owner, string spender) => Abi.Call("allowance(address,address)", Abi.Address(owner), Abi.Address(spender));
    public static byte[] ApproveCall(string spender, BigInteger amount) => Abi.Call("approve(address,uint256)", Abi.Address(spender), Abi.Uint(amount));
    public static byte[] WrapCall() => Abi.Call("deposit()");
    // transferToChain(token, "minter", the Minter address left-padded, amount in the token's decimals, fee 0) — as the
    // deposits on BSC of October 2026 carry it.
    public static byte[] DepositCall(BridgeToken token, Address recipient, BigInteger amount) =>
        Abi.Call("transferToChain(address,bytes32,bytes32,uint256,uint256)", Abi.Address(token.Contract), Abi.Text32("minter"), Abi.Address(recipient), Abi.Uint(amount), Abi.Uint(0));

    // The bridge's multisend that credits a deposit: the first one after `since` with this coin to the recipient. The
    // bridge keeps 1 % of a deposit too (19 USDT in, 18.81 USDTBSC out on 27.10.2026).
    public static BridgeOut? ArrivalOf(Address recipient, uint coin, DateTimeOffset since, IEnumerable<BridgeTx> activity) =>
        activity.Where(t => t.Type == (int)TxType.Multisend && t.Time >= since).OrderBy(t => t.Height)
            .SelectMany(t => t.Outs).FirstOrDefault(o => o.To == recipient && o.Coin == coin);

    public static string TxExplorerOf(BridgeNetwork network, string hash) => network == BridgeNetwork.Bsc ? $"https://bscscan.com/tx/{hash}" : $"https://etherscan.io/tx/{hash}";

    public static IEnumerable<BridgeNetwork> NetworksOf(uint coin) => Tokens.Where(t => t.MinterId == coin).Select(t => t.Network);
    public static BridgeToken? TokenOf(uint coin, BridgeNetwork network) => Tokens.FirstOrDefault(t => t.MinterId == coin && t.Network == network);
    public static string NameOf(BridgeNetwork network) => network == BridgeNetwork.Bsc ? "BSC" : "Ethereum";

    public static string Payload(BridgeNetwork network, string recipient) =>
        $"{{\"type\":\"{(network == BridgeNetwork.Bsc ? "send_to_bsc" : "send_to_ethereum")}\",\"recipient\":\"{Checksum(recipient)}\"}}";

    // The command a withdrawal carries, or null when the bridge would skip it.
    public static (BridgeNetwork Network, string Recipient)? ReadPayload(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var type = doc.RootElement.GetProperty("type").GetString();
            var recipient = doc.RootElement.GetProperty("recipient").GetString() ?? "";
            if (RecipientProblem(recipient) != null) return null;
            return type switch { "send_to_bsc" => (BridgeNetwork.Bsc, recipient), "send_to_ethereum" => (BridgeNetwork.Ethereum, recipient), _ => null };
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }

    // An Ethereum/BSC address: 0x and 40 hex digits; one in mixed case must carry a valid EIP-55 checksum.
    public static string? RecipientProblem(string text)
    {
        if (text.Length != 42 || !text.StartsWith("0x", StringComparison.Ordinal) || !text[2..].All(Uri.IsHexDigit))
            return "Адрес в Ethereum или BSC — 0x и 40 шестнадцатеричных символов.";
        var hex = text[2..];
        if (hex.Any(char.IsUpper) && hex.Any(char.IsLower) && Checksum(text) != text)
            return "В адресе ошибка: не сходится контрольная сумма заглавных и строчных букв (EIP-55). Скопируйте адрес заново.";
        return null;
    }

    // EIP-55: a hex letter is upper case where the keccak of the lower-case address has a nibble of 8 or more.
    public static string Checksum(string address)
    {
        var lower = address[2..].ToLowerInvariant();
        var hash = Keccak.Hash256(Encoding.ASCII.GetBytes(lower));
        var result = new StringBuilder("0x", 42);
        for (var i = 0; i < 40; i++)
        {
            var nibble = (hash[i / 2] >> (i % 2 == 0 ? 4 : 0)) & 0xf;
            result.Append(char.IsLetter(lower[i]) && nibble >= 8 ? char.ToUpperInvariant(lower[i]) : lower[i]);
        }
        return result.ToString();
    }

    public static BigInteger HubFee(BigInteger value) => value / 100;

    public static string ExplorerOf(BridgeNetwork network, string recipient) =>
        network == BridgeNetwork.Bsc ? $"https://bscscan.com/address/{recipient}#tokentxns" : $"https://etherscan.io/address/{recipient}#tokentxns";

    // The bridge's multisend for a withdrawal: the first one after it with exactly its 1 % in that coin to the fee
    // collector; the rest of that coin in it, not to the sender, is the destination gas.
    public static BridgeSettlement? SettlementOf(BridgeTx withdrawal, IEnumerable<BridgeTx> activity)
    {
        var fee = HubFee(withdrawal.Value);
        foreach (var tx in activity.Where(t => t.Type == (int)TxType.Multisend && t.Height >= withdrawal.Height).OrderBy(t => t.Height))
        {
            if (!tx.Outs.Any(o => o.To == FeeCollector && o.Coin == withdrawal.Coin && o.Value == fee)) continue;
            var gas = tx.Outs.Where(o => o.Coin == withdrawal.Coin && o.To != FeeCollector && o.To != withdrawal.From).Aggregate(BigInteger.Zero, (s, o) => s + o.Value);
            return new BridgeSettlement(fee, gas, tx.Hash, tx.Height);
        }
        return null;
    }

    // The destination gas the bridge kept in recent withdrawals of a coin to a network (newest first).
    public static IReadOnlyList<BigInteger> RecentGas(IReadOnlyList<BridgeTx> activity, uint coin, BridgeNetwork network) =>
        activity.Where(t => t.Type == (int)TxType.Send && t.Coin == coin && ReadPayload(t.Payload)?.Network == network)
            .Select(w => SettlementOf(w, activity)?.GasFee).OfType<BigInteger>().ToList();
}
