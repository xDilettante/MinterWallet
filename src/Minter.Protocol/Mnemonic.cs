using System.Security.Cryptography;
using System.Text;

namespace Minter.Protocol;

// BIP39 with the English word list (Resources/bip39-english.txt, SHA-256 2f5eed53…dbda as in bitcoin/bips).
public static class Mnemonic
{
    private static readonly Lazy<string[]> WordList = new(Load);
    private static readonly Lazy<Dictionary<string, int>> Index = new(() => WordList.Value.Select((w, i) => (w, i)).ToDictionary(x => x.w, x => x.i));
    public static IReadOnlyList<string> Words => WordList.Value;

    public static string Generate(int words = 12)
    {
        if (words is not (12 or 15 or 18 or 21 or 24)) throw new ArgumentOutOfRangeException(nameof(words));
        var entropy = RandomNumberGenerator.GetBytes(words * 4 / 3);
        try { return FromEntropy(entropy); }
        finally { CryptographicOperations.ZeroMemory(entropy); }
    }

    public static string FromEntropy(ReadOnlySpan<byte> entropy)
    {
        if (entropy.Length is < 16 or > 32 || entropy.Length % 4 != 0) throw new ArgumentException("Entropy must be 16..32 bytes, a multiple of 4.");
        var checksumBits = entropy.Length / 4;
        var hash = SHA256.HashData(entropy);
        var bits = entropy.Length * 8 + checksumBits;
        var words = new string[bits / 11];
        for (var w = 0; w < words.Length; w++)
        {
            var index = 0;
            for (var b = 0; b < 11; b++)
            {
                var bit = w * 11 + b;
                var source = bit < entropy.Length * 8 ? entropy[bit / 8] : hash[(bit - entropy.Length * 8) / 8];
                var position = bit < entropy.Length * 8 ? bit % 8 : (bit - entropy.Length * 8) % 8;
                index = (index << 1) | ((source >> (7 - position)) & 1);
            }
            words[w] = WordList.Value[index];
        }
        return string.Join(' ', words);
    }

    public static string[] Split(string mnemonic) =>
        Nfkd(mnemonic).ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    // ASCII text is already NFKD; anything else needs the platform normaliser (ICU on Linux and macOS). The wallet runs
    // with invariant globalization, so a non-ASCII passphrase is refused there instead of being hashed unnormalised.
    private static string Nfkd(string text)
    {
        if (System.Text.Ascii.IsValid(text)) return text;
        try { return text.Normalize(NormalizationForm.FormKD); }
        catch (PlatformNotSupportedException) { throw new NotSupportedException("A non-ASCII mnemonic or passphrase needs Unicode normalisation (ICU), which is unavailable here."); }
    }

    // Word count, known words and the checksum.
    public static bool IsValid(string mnemonic)
    {
        var words = Split(mnemonic);
        if (words.Length is not (12 or 15 or 18 or 21 or 24)) return false;
        var indices = new int[words.Length];
        for (var i = 0; i < words.Length; i++) if (!Index.Value.TryGetValue(words[i], out indices[i])) return false;
        var totalBits = words.Length * 11;
        var checksumBits = totalBits / 33;
        var entropy = new byte[(totalBits - checksumBits) / 8];
        for (var bit = 0; bit < entropy.Length * 8; bit++)
            if (((indices[bit / 11] >> (10 - bit % 11)) & 1) != 0) entropy[bit / 8] |= (byte)(0x80 >> (bit % 8));
        var hash = SHA256.HashData(entropy);
        CryptographicOperations.ZeroMemory(entropy);
        for (var i = 0; i < checksumBits; i++)
        {
            var bit = entropy.Length * 8 + i;
            var expected = (hash[i / 8] >> (7 - i % 8)) & 1;
            if (((indices[bit / 11] >> (10 - bit % 11)) & 1) != expected) return false;
        }
        return true;
    }

    // PBKDF2-HMAC-SHA512, 2048 iterations, salt "mnemonic" + passphrase, both NFKD.
    public static byte[] ToSeed(string mnemonic, string passphrase = "")
    {
        var password = Encoding.UTF8.GetBytes(string.Join(' ', Split(mnemonic)));
        var salt = Encoding.UTF8.GetBytes("mnemonic" + Nfkd(passphrase));
        try { return Rfc2898DeriveBytes.Pbkdf2(password, salt, 2048, HashAlgorithmName.SHA512, 64); }
        finally { CryptographicOperations.ZeroMemory(password); }
    }

    private static string[] Load()
    {
        using var stream = typeof(Mnemonic).Assembly.GetManifestResourceStream("bip39-english.txt") ?? throw new InvalidOperationException("BIP39 word list is missing.");
        using var reader = new StreamReader(stream);
        var words = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length != 2048) throw new InvalidDataException("BIP39 word list must have 2048 words.");
        return words;
    }
}

// BIP32 private derivation for secp256k1. Minter wallets use the Ethereum path m/44'/60'/0'/0/index.
public static class HdKey
{
    public const string MinterPath = "m/44'/60'/0'/0/0";
    public static string MinterAccountPath(uint index) => $"m/44'/60'/0'/0/{index}";

    public static byte[] DerivePrivateKey(ReadOnlySpan<byte> seed, string path)
    {
        var (key, chain) = Master(seed);
        try
        {
            foreach (var index in ParsePath(path))
            {
                var (childKey, childChain) = Child(key, chain, index);
                CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(chain);
                key = childKey; chain = childChain;
            }
            return key;
        }
        finally { CryptographicOperations.ZeroMemory(chain); }
    }

    public static PrivateKey FromMnemonic(string mnemonic, uint index = 0, string passphrase = "")
    {
        var seed = Mnemonic.ToSeed(mnemonic, passphrase);
        var key = DerivePrivateKey(seed, MinterAccountPath(index));
        try { return new PrivateKey(key); }
        finally { CryptographicOperations.ZeroMemory(seed); CryptographicOperations.ZeroMemory(key); }
    }

    private static (byte[] Key, byte[] Chain) Master(ReadOnlySpan<byte> seed)
    {
        if (seed.Length is < 16 or > 64) throw new ArgumentException("Seed must be 16..64 bytes.");
        var i = HMACSHA512.HashData("Bitcoin seed"u8, seed);
        var key = i[..32]; var chain = i[32..];
        CryptographicOperations.ZeroMemory(i);
        if (!Secp256k1.InRange(key)) throw new InvalidOperationException("Invalid master key; use another seed.");
        return (key, chain);
    }

    private static (byte[] Key, byte[] Chain) Child(byte[] key, byte[] chain, uint index)
    {
        Span<byte> data = stackalloc byte[37];
        if (index >= 0x80000000u) { data[0] = 0; key.CopyTo(data[1..]); }
        else Secp256k1.PublicKey(key, compressed: true).CopyTo(data);
        data[33] = (byte)(index >> 24); data[34] = (byte)(index >> 16); data[35] = (byte)(index >> 8); data[36] = (byte)index;
        var i = HMACSHA512.HashData(chain, data);
        CryptographicOperations.ZeroMemory(data);
        try
        {
            if (!Secp256k1.InRange(i.AsSpan(0, 32))) throw new InvalidOperationException($"Invalid child {index}; use the next index.");
            var child = Secp256k1.AddPrivate(i.AsSpan(0, 32), key);
            if (!Secp256k1.InRange(child)) throw new InvalidOperationException($"Invalid child {index}; use the next index.");
            return (child, i[32..]);
        }
        finally { CryptographicOperations.ZeroMemory(i); }
    }

    private static IEnumerable<uint> ParsePath(string path)
    {
        var parts = path.Split('/');
        if (parts[0] != "m") throw new FormatException("Derivation path must start with m.");
        foreach (var part in parts.Skip(1))
        {
            var hardened = part.EndsWith('\'') || part.EndsWith('h');
            if (!uint.TryParse(hardened ? part[..^1] : part, out var index) || index >= 0x80000000u) throw new FormatException($"Bad path element '{part}'.");
            yield return hardened ? index | 0x80000000u : index;
        }
    }
}
