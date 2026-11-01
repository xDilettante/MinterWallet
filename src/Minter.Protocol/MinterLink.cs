using System.Text;
using System.Text.RegularExpressions;

namespace Minter.Protocol;

// Minter Link, as minter-js-sdk prepareLink/decodeLink make them: https://bip.to/tx/<base64url of the RLP list
// [type, data, payload, nonce, gas price, gas coin]>, with ?p=<base64url password> for a check. A request to pay leaves
// nonce, gas price and gas coin to the wallet that opens it: empty strings, the gas coin an empty list.
public sealed partial record MinterLink(TxType Type, byte[] Data, byte[] Payload, ulong? Nonce = null, uint? GasPrice = null, uint? GasCoin = null, string? Password = null)
{
    public const string Host = "https://bip.to";

    public static MinterLink Of(ITxData data, byte[]? payload = null) => new(data.Type, data.Encode(), payload ?? []);

    public override string ToString()
    {
        var rlp = Rlp.List(Rlp.Uint((byte)Type), Rlp.Bytes(Data), Rlp.Bytes(Payload),
            Nonce is { } nonce ? Rlp.Uint(nonce) : Rlp.Bytes([]), GasPrice is { } price ? Rlp.Uint(price) : Rlp.Bytes([]),
            GasCoin is { } coin ? Rlp.Uint(coin) : Rlp.List());
        var link = $"{Host}/tx/{Base64Url(rlp)}";
        return Password is { Length: > 0 } password ? $"{link}?p={Base64Url(Encoding.UTF8.GetBytes(password))}" : link;
    }

    // Any host (bip.to, a wallet's own), "/tx/" and the transaction; null when the text is not such a link.
    public static MinterLink? Parse(string text)
    {
        var match = LinkRegex().Match(text.Trim());
        if (!match.Success) return null;
        try
        {
            var v = Rlp.Decode(FromBase64Url(match.Groups["tx"].Value)).ExpectCount(6);
            var type = (TxType)v[0].Byte();
            if (!Enum.IsDefined(type)) return null;
            return new MinterLink(type, v[1].ToBytes(), v[2].ToBytes(),
                v[3].Span.Length > 0 ? v[3].UInt64() : null, v[4].Span.Length > 0 ? v[4].UInt32() : null,
                v[5].IsList ? null : v[5].UInt32(),
                match.Groups["p"].Success ? Encoding.UTF8.GetString(FromBase64Url(match.Groups["p"].Value)) : null);
        }
        catch (FormatException) { return null; }
    }

    // The transaction data; a check link carries the check alone (the proof needs the redeemer and the password).
    public ITxData DecodeData() => TxData.Decode(Type, Data);
    public byte[] Check() => Type == TxType.RedeemCheck ? Rlp.Decode(Data)[0].ToBytes() : throw new InvalidOperationException("Not a check link.");

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static byte[] FromBase64Url(string text)
    {
        var base64 = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(base64 + new string('=', (4 - base64.Length % 4) % 4));
    }

    [GeneratedRegex(@"^(?:[a-z][a-z0-9+.-]*://)?[^\s/?#]+/tx/(?<tx>[A-Za-z0-9_-]+)/?(?:\?p=(?<p>[A-Za-z0-9_-]*))?$", RegexOptions.IgnoreCase)]
    private static partial Regex LinkRegex();
}
