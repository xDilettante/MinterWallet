using System.Globalization;
using System.Numerics;
using System.Text;

namespace Minter.Protocol;

// Account address: 20 bytes, text "Mx" + 40 hex. Minter addresses have no checksum: a typo is a valid address.
public readonly struct Address : IEquatable<Address>
{
    public const int Length = 20;
    private readonly byte[]? bytes;
    public Address(ReadOnlySpan<byte> value)
    {
        if (value.Length != Length) throw new ArgumentException("An address is 20 bytes.");
        bytes = value.ToArray();
    }
    public ReadOnlySpan<byte> Span => bytes ?? new byte[Length];
    public byte[] ToArray() => Span.ToArray();

    public static Address Parse(string text) => TryParse(text, out var address) ? address : throw new FormatException("Address must be Mx followed by 40 hex characters.");
    public static bool TryParse(string? text, out Address address)
    {
        address = default;
        text = text?.Trim();
        if (text is not { Length: 42 } || !text.StartsWith("Mx", StringComparison.OrdinalIgnoreCase) || !Hex.IsHex(text.AsSpan(2))) return false;
        address = new Address(Convert.FromHexString(text.AsSpan(2)));
        return true;
    }
    public static Address FromPublicKey(ReadOnlySpan<byte> uncompressed)
    {
        if (uncompressed.Length != 65 || uncompressed[0] != 4) throw new ArgumentException("Expected an uncompressed public key.");
        return new Address(Keccak.Hash256(uncompressed[1..])[12..]);
    }
    // state/accounts CreateMultisigAddress: keccak256(rlp([owner, nonce]))[12:].
    public static Address Multisig(Address owner, ulong nonce) => new(Keccak.Hash256(Rlp.List(Rlp.Bytes(owner.Span), Rlp.Uint(nonce)))[12..]);

    public override string ToString() => "Mx" + Convert.ToHexStringLower(Span);
    public bool Equals(Address other) => Span.SequenceEqual(other.Span);
    public override bool Equals(object? obj) => obj is Address other && Equals(other);
    public override int GetHashCode() { var h = new HashCode(); h.AddBytes(Span); return h.ToHashCode(); }
    public static bool operator ==(Address a, Address b) => a.Equals(b);
    public static bool operator !=(Address a, Address b) => !a.Equals(b);
}

// Validator (Tendermint Ed25519) public key: 32 bytes, text "Mp" + 64 hex.
public readonly struct Pubkey : IEquatable<Pubkey>
{
    public const int Length = 32;
    private readonly byte[]? bytes;
    public Pubkey(ReadOnlySpan<byte> value)
    {
        if (value.Length != Length) throw new ArgumentException("A validator public key is 32 bytes.");
        bytes = value.ToArray();
    }
    public ReadOnlySpan<byte> Span => bytes ?? new byte[Length];
    public static Pubkey Parse(string text)
    {
        text = text.Trim();
        if (text.Length != 66 || !text.StartsWith("Mp", StringComparison.OrdinalIgnoreCase) || !Hex.IsHex(text.AsSpan(2)))
            throw new FormatException("Public key must be Mp followed by 64 hex characters.");
        return new Pubkey(Convert.FromHexString(text.AsSpan(2)));
    }
    public override string ToString() => "Mp" + Convert.ToHexStringLower(Span);
    public bool Equals(Pubkey other) => Span.SequenceEqual(other.Span);
    public override bool Equals(object? obj) => obj is Pubkey other && Equals(other);
    public override int GetHashCode() { var h = new HashCode(); h.AddBytes(Span); return h.ToHashCode(); }
}

// Coin ticker: up to 10 ASCII bytes, stored zero-padded (types.CoinSymbol). Rules of state/coins: 3..10 chars of
// A-Z and 0-9, starting with a letter; the text form of a recreated coin is "SYMBOL-version" and is not a ticker.
public readonly struct CoinSymbol : IEquatable<CoinSymbol>
{
    public const int Length = 10;
    private readonly byte[]? bytes;
    private CoinSymbol(byte[] value) => bytes = value;
    public ReadOnlySpan<byte> Span => bytes ?? new byte[Length];

    public static CoinSymbol FromString(string text)
    {
        if (text.Length > Length || text.Any(c => c > 0x7f)) throw new FormatException("A ticker is at most 10 ASCII characters.");
        var value = new byte[Length];
        Encoding.ASCII.GetBytes(text).CopyTo(value, 0);
        return new CoinSymbol(value);
    }
    public static CoinSymbol FromBytes(ReadOnlySpan<byte> value)
    {
        if (value.Length != Length) throw new FormatException("A ticker is 10 bytes on the wire.");
        return new CoinSymbol(value.ToArray());
    }
    public static bool IsValidTicker(string text) =>
        text.Length is >= 3 and <= Length && text[0] is >= 'A' and <= 'Z' && text.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9');
    public override string ToString() => Encoding.ASCII.GetString(Span).Trim('\0');
    public bool Equals(CoinSymbol other) => Span.SequenceEqual(other.Span);
    public override bool Equals(object? obj) => obj is CoinSymbol other && Equals(other);
    public override int GetHashCode() { var h = new HashCode(); h.AddBytes(Span); return h.ToHashCode(); }
}

public static class Hex
{
    public static bool IsHex(ReadOnlySpan<char> text) { foreach (var c in text) if (!char.IsAsciiHexDigit(c)) return false; return true; }
    public static byte[] Parse(string text, string? prefix = null)
    {
        if (prefix != null && text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) text = text[prefix.Length..];
        else if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
        return Convert.FromHexString(text);
    }
}

// Amounts on the wire are integers in pip: 1 coin = 10^18 pip.
public static class Units
{
    public const int Decimals = 18;
    public static readonly BigInteger PipPerCoin = BigInteger.Pow(10, Decimals);

    public static BigInteger ParseCoins(string text)
    {
        text = text.Trim().Replace(',', '.');
        if (text.Length == 0 || text.StartsWith('-') || text.StartsWith('+')) throw new FormatException("Amount must be a non-negative decimal.");
        var dot = text.IndexOf('.');
        var whole = dot < 0 ? text : text[..dot];
        var fraction = dot < 0 ? "" : text[(dot + 1)..];
        if (fraction.Length > Decimals) throw new FormatException("At most 18 decimal places.");
        if ((whole + fraction).Length == 0 || !(whole + fraction).All(char.IsAsciiDigit)) throw new FormatException("Amount must be a decimal number.");
        return BigInteger.Parse((whole.Length == 0 ? "0" : whole) + fraction.PadRight(Decimals, '0'), CultureInfo.InvariantCulture);
    }
    public static string FormatCoins(BigInteger pip)
    {
        if (pip.Sign < 0) return "-" + FormatCoins(-pip);
        var whole = BigInteger.DivRem(pip, PipPerCoin, out var rest);
        var fraction = rest.ToString(CultureInfo.InvariantCulture).PadLeft(Decimals, '0').TrimEnd('0');
        return fraction.Length == 0 ? whole.ToString(CultureInfo.InvariantCulture) : $"{whole.ToString(CultureInfo.InvariantCulture)}.{fraction}";
    }
}
