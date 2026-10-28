using System.Numerics;
using System.Text;
using System.Text.Json;
using Minter.Protocol;

namespace MinterWallet.Core;

public enum BridgeNetwork { Ethereum, Bsc }

// A Minter coin the bridge carries out to a network, as the token at Contract there (null: the network's own coin).
public sealed record BridgeToken(uint MinterId, string Denom, BridgeNetwork Network, string? Contract);

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

    // github.com/MinterTeam/minter-hub-tokens tokens.json: the coins that go out to Ethereum and BSC.
    public static readonly IReadOnlyList<BridgeToken> Tokens =
    [
        new(1993, "usdt", BridgeNetwork.Ethereum, "0xdac17f958d2ee523a2206206994597c13d831ec7"),
        new(3494, "usdtb", BridgeNetwork.Bsc, "0x55d398326f99059ff775485246999027b3197955"),
        new(1994, "usdc", BridgeNetwork.Ethereum, "0xa0b86991c6218b36c1d19d4a2e9eb0ce3606eb48"),
        new(3485, "usdcbsc", BridgeNetwork.Bsc, "0x8ac76a51cc950d9822d68b83fe1ad97b32cd580d"),
        new(1996, "dai", BridgeNetwork.Ethereum, "0x6b175474e89094c44da98b954eedeac495271d0f"),
        new(3489, "daibsc", BridgeNetwork.Bsc, "0x1af3f329e8be154074d8769d1ffa4ee058b1dbc3"),
        new(2065, "eth", BridgeNetwork.Ethereum, null),
        new(2065, "ethbsc", BridgeNetwork.Bsc, "0x2170ed0880ac9a755fd29b2688956bd959f933f8"),
        new(2064, "btc", BridgeNetwork.Ethereum, "0x2260fac5e5542a773aa44fbcfedf7c193bc2c599"),
        new(3474, "btcbsc", BridgeNetwork.Bsc, null),
        new(1995, "busd", BridgeNetwork.Ethereum, "0x4fabb145d64652a948d72533023f6e7a623c7c53"),
        new(3491, "busdb", BridgeNetwork.Bsc, "0xe9e7cea3dedca5984780bafc599bd69add087d56"),
        new(3224, "ton", BridgeNetwork.Ethereum, "0x582d872a1b094fc48f5de31d3b73f2d9be47def1"),
        new(3324, "tonbsc", BridgeNetwork.Bsc, "0x76a797a59ba2c17726896976b7b3747bfd1d220f"),
        new(2598, "oneinch", BridgeNetwork.Ethereum, "0x111111111117dc0aa78b770fa6a738034120c302"),
        new(3258, "shib", BridgeNetwork.Ethereum, "0x95ad61b0a150d79219dcf64e1e6cc01f0b64c4ce"),
        new(3478, "dogebsc", BridgeNetwork.Bsc, "0xba2ae424d960c26247dd6c32edc70b295c744c43"),
        new(3482, "ltcbsc", BridgeNetwork.Bsc, "0x4338665cbb7b2485a8855a139b75d5e34ab0db94"),
        new(3667, "xrpbsc", BridgeNetwork.Bsc, "0x1d2f0da169ceb9fc7b3144628db156f3f6c60dbe"),
        new(3665, "adabsc", BridgeNetwork.Bsc, "0x3ee2200efb3400fabb9aacf31297cbdd1d435d47"),
        new(3778, "solbsc", BridgeNetwork.Bsc, "0x570a5d26f7765ecb712c0924e4de545b89fd43df"),
        new(3483, "dotbsc", BridgeNetwork.Bsc, "0x7083609fce4d1d8dc0c979aab8c869ea2c873402"),
        new(3666, "linkbsc", BridgeNetwork.Bsc, "0xf8a0bf9cf54bb92f17374d9e9a321e6a111a51bd"),
        new(3607, "cakebsc", BridgeNetwork.Bsc, "0x0e09fabb73bd3ade0a17ecc321fd13a19e81ce82"),
    ];

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
