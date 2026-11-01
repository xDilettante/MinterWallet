using System.Security.Cryptography;

namespace Minter.Protocol;

// A secp256k1 account key. The 32 bytes live in a pinned array and are wiped by Dispose; callers keep the key
// only for the duration of a signature.
public sealed class PrivateKey : IDisposable
{
    private readonly byte[] key = GC.AllocateArray<byte>(32, pinned: true);
    private bool disposed;
    public Address Address { get; }
    public byte[] PublicKey { get; }   // 65 bytes, uncompressed

    public PrivateKey(ReadOnlySpan<byte> privateKey)
    {
        if (!Secp256k1.IsValidPrivateKey(privateKey)) throw new ArgumentException("Invalid secp256k1 private key.");
        privateKey.CopyTo(key);
        PublicKey = Secp256k1.PublicKey(key);
        Address = Protocol.Address.FromPublicKey(PublicKey);
    }
    public static PrivateKey FromHex(string hex) => new(Hex.Parse(hex));
    public static PrivateKey Generate() { Span<byte> buffer = stackalloc byte[32]; do RandomNumberGenerator.Fill(buffer); while (!Secp256k1.IsValidPrivateKey(buffer)); try { return new(buffer); } finally { CryptographicOperations.ZeroMemory(buffer); } }

    public RecoverableSignature Sign(ReadOnlySpan<byte> hash32)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return Secp256k1.Sign(hash32, key);
    }
    public byte[] ExportBytes() { ObjectDisposedException.ThrowIf(disposed, this); return key.ToArray(); }

    public void Dispose() { CryptographicOperations.ZeroMemory(key); disposed = true; }
}
