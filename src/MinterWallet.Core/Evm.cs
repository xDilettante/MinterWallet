using System.Globalization;
using System.Numerics;
using System.Text;
using Minter.Protocol;

namespace MinterWallet.Core;

// An Ethereum or BSC transaction of the legacy kind with the chain id in the signature (EIP-155): both networks take it,
// and a signature for one chain is no use on the other. The account's own key signs it: the 20 bytes of a Minter
// address are its address there too.
public sealed record EvmTx(ulong Nonce, BigInteger GasPrice, ulong Gas, byte[] To, BigInteger Value, byte[] Data)
{
    private byte[][] Fields => [Rlp.Uint(Nonce), Rlp.Big(GasPrice), Rlp.Uint(Gas), Rlp.Bytes(To), Rlp.Big(Value), Rlp.Bytes(Data)];

    public byte[] SigningHash(ulong chainId) => Keccak.Hash256(Rlp.List([.. Fields, Rlp.Uint(chainId), Rlp.Uint(0), Rlp.Uint(0)]));

    // The raw transaction for eth_sendRawTransaction.
    public byte[] Sign(PrivateKey key, ulong chainId)
    {
        var signature = key.Sign(SigningHash(chainId));
        return Rlp.List([.. Fields, Rlp.Uint(chainId * 2 + 35 + signature.RecoveryId),
            Rlp.Big(new BigInteger(signature.R, isUnsigned: true, isBigEndian: true)), Rlp.Big(new BigInteger(signature.S, isUnsigned: true, isBigEndian: true))]);
    }

    public static string HashOf(byte[] raw) => "0x" + Convert.ToHexStringLower(Keccak.Hash256(raw));
}

// Calls of contracts with static arguments only (address, bytes32, uint256), as the Solidity ABI lays them out.
public static class Abi
{
    public static byte[] Selector(string signature) => Keccak.Hash256(Encoding.ASCII.GetBytes(signature))[..4];

    public static byte[] Address(string hex) => Word(Convert.FromHexString(hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex[2..] : hex), left: true);
    public static byte[] Address(Address minter) => Word(minter.Span, left: true);
    public static byte[] Uint(BigInteger value) => value.Sign < 0 ? throw new ArgumentOutOfRangeException(nameof(value)) : Word(value.IsZero ? [] : value.ToByteArray(isUnsigned: true, isBigEndian: true), left: true);
    // bytes32 of a short text (ethers encodeBytes32String): the bytes, then zeros.
    public static byte[] Text32(string text) => Word(Encoding.UTF8.GetBytes(text), left: false);

    public static byte[] Call(string signature, params byte[][] arguments) => [.. Selector(signature), .. arguments.SelectMany(a => a)];

    public static BigInteger ReadUint(ReadOnlySpan<byte> result, int index = 0) =>
        result.Length < 32 * (index + 1) ? throw new FormatException("A contract answered with fewer bytes than a number.") : new BigInteger(result.Slice(32 * index, 32), isUnsigned: true, isBigEndian: true);

    private static byte[] Word(ReadOnlySpan<byte> value, bool left)
    {
        if (value.Length > 32) throw new ArgumentException("More than 32 bytes.");
        var word = new byte[32];
        value.CopyTo(left ? word.AsSpan(32 - value.Length) : word);
        return word;
    }
}

// Amounts of tokens with their own number of decimals (USDT on Ethereum has 6, most have 18).
public static class EvmUnits
{
    public static BigInteger Parse(string text, int decimals)
    {
        text = text.Trim().Replace(',', '.');
        var parts = text.Split('.');
        if (parts.Length > 2 || parts[0].Length == 0 && (parts.Length == 1 || parts[1].Length == 0) || !parts.All(p => p.All(char.IsAsciiDigit)))
            throw new FormatException("Сумма — число с точкой или запятой.");
        var fraction = parts.Length == 2 ? parts[1] : "";
        if (fraction.Length > decimals) throw new FormatException($"У этого токена не больше {decimals} знаков после точки.");
        return BigInteger.Parse((parts[0].Length == 0 ? "0" : parts[0]) + fraction.PadRight(decimals, '0'), CultureInfo.InvariantCulture);
    }

    public static string Format(BigInteger value, int decimals)
    {
        var digits = value.ToString(CultureInfo.InvariantCulture).PadLeft(decimals + 1, '0');
        var whole = digits[..^decimals];
        var fraction = decimals == 0 ? "" : digits[^decimals..].TrimEnd('0');
        return fraction.Length == 0 ? whole : $"{whole}.{fraction}";
    }

    // The same amount in Minter's 18 decimals (a bridged coin always has 18 there).
    public static BigInteger ToMinter(BigInteger value, int decimals) => decimals <= 18 ? value * BigInteger.Pow(10, 18 - decimals) : value / BigInteger.Pow(10, decimals - 18);
}
