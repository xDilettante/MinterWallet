using System.Security.Cryptography;
using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.EC;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Math.EC;
using Org.BouncyCastle.Math.EC.Multiplier;
using BcBig = Org.BouncyCastle.Math.BigInteger;

namespace Minter.Protocol;

public static class Keccak
{
    public static byte[] Hash256(ReadOnlySpan<byte> data)
    {
        var digest = new KeccakDigest(256);
        digest.BlockUpdate(data);
        var result = new byte[32];
        digest.DoFinal(result, 0);
        return result;
    }
}

// A recoverable secp256k1 signature as go-ethereum crypto.Sign returns it: R, S (low S) and the recovery id 0/1.
// Minter stores V = 27 + RecoveryId in transactions and checks, and R || S || RecoveryId in a check lock and proof.
public readonly record struct RecoverableSignature(byte[] R, byte[] S, byte RecoveryId)
{
    public byte V => (byte)(27 + RecoveryId);
    public byte[] ToBytes65() => [.. R, .. S, RecoveryId];
    public static RecoverableSignature FromBytes65(ReadOnlySpan<byte> sig)
    {
        if (sig.Length != 65 || sig[64] > 1) throw new FormatException("Expected a 65-byte signature with recovery id 0 or 1.");
        return new(sig[..32].ToArray(), sig[32..64].ToArray(), sig[64]);
    }
}

public static class Secp256k1
{
    private static readonly X9ECParameters Curve = CustomNamedCurves.GetByName("secp256k1");
    private static readonly FixedPointCombMultiplier BaseMultiplier = new();
    private static readonly BcBig HalfOrder = Curve.N.ShiftRight(1);
    public static BcBig Order => Curve.N;

    public static bool IsValidPrivateKey(ReadOnlySpan<byte> key)
    {
        if (key.Length != 32) return false;
        var d = new BcBig(1, key);
        return d.SignValue > 0 && d.CompareTo(Curve.N) < 0;
    }

    // 65 bytes: 0x04 || X || Y.
    public static byte[] PublicKey(ReadOnlySpan<byte> privateKey, bool compressed = false)
    {
        if (!IsValidPrivateKey(privateKey)) throw new ArgumentException("Invalid secp256k1 private key.");
        return BaseMultiplier.Multiply(Curve.G, new BcBig(1, privateKey)).Normalize().GetEncoded(compressed);
    }

    public static byte[] Compress(ReadOnlySpan<byte> publicKey) => Curve.Curve.DecodePoint(publicKey.ToArray()).GetEncoded(true);

    // RFC 6979 deterministic k with HMAC-SHA256 (as libsecp256k1 and btcec), S normalised to the lower half.
    public static RecoverableSignature Sign(ReadOnlySpan<byte> hash, ReadOnlySpan<byte> privateKey)
    {
        if (hash.Length != 32) throw new ArgumentException("Expected a 32-byte hash.");
        if (!IsValidPrivateKey(privateKey)) throw new ArgumentException("Invalid secp256k1 private key.");
        var n = Curve.N;
        var d = new BcBig(1, privateKey);
        var e = new BcBig(1, hash);
        var message = hash.ToArray();
        var k = new HMacDsaKCalculator(new Sha256Digest());
        k.Init(n, d, message);
        while (true)
        {
            var kv = k.NextK();
            var point = BaseMultiplier.Multiply(Curve.G, kv).Normalize();
            var x = point.AffineXCoord.ToBigInteger();
            var r = x.Mod(n);
            if (r.SignValue == 0) continue;
            var s = kv.ModInverse(n).Multiply(e.Add(d.Multiply(r))).Mod(n);
            if (s.SignValue == 0) continue;
            var recid = (point.AffineYCoord.TestBitZero() ? 1 : 0) | (x.CompareTo(n) >= 0 ? 2 : 0);
            if (s.CompareTo(HalfOrder) > 0) { s = n.Subtract(s); recid ^= 1; }
            if (recid > 1) continue;   // R.x >= N (probability ~2^-128): Minter accepts only V 27/28
            return new(Fixed32(r), Fixed32(s), (byte)recid);
        }
    }

    // Public key (65 bytes, uncompressed) that produced the signature; go-ethereum crypto.Ecrecover with the
    // homestead rule of ValidateSignatureValues (R, S in [1, N), S <= N/2, recovery id 0/1).
    public static byte[] Recover(ReadOnlySpan<byte> hash, RecoverableSignature signature)
    {
        if (hash.Length != 32) throw new ArgumentException("Expected a 32-byte hash.");
        var n = Curve.N;
        var r = new BcBig(1, signature.R);
        var s = new BcBig(1, signature.S);
        if (signature.RecoveryId > 1 || r.SignValue <= 0 || s.SignValue <= 0 || r.CompareTo(n) >= 0 || s.CompareTo(n) >= 0 || s.CompareTo(HalfOrder) > 0)
            throw new FormatException("Invalid signature values.");
        var compressed = new byte[33];
        compressed[0] = (byte)(0x02 | signature.RecoveryId);
        Fixed32(r).CopyTo(compressed, 1);
        Org.BouncyCastle.Math.EC.ECPoint rPoint;
        try { rPoint = Curve.Curve.DecodePoint(compressed); }
        catch (ArgumentException) { throw new FormatException("Signature R is not on the curve."); }
        var e = new BcBig(1, hash);
        var rInverse = r.ModInverse(n);
        var u1 = n.Subtract(e).Mod(n).Multiply(rInverse).Mod(n);
        var u2 = s.Multiply(rInverse).Mod(n);
        var q = ECAlgorithms.SumOfTwoMultiplies(Curve.G, u1, rPoint, u2).Normalize();
        if (q.IsInfinity) throw new FormatException("Signature recovers the point at infinity.");
        return q.GetEncoded(false);
    }

    // BIP32 helpers: (a + b) mod N, and the public point of a private key added to a public key.
    internal static byte[] AddPrivate(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => Fixed32(new BcBig(1, a).Add(new BcBig(1, b)).Mod(Curve.N));
    internal static bool InRange(ReadOnlySpan<byte> value) { var v = new BcBig(1, value); return v.SignValue > 0 && v.CompareTo(Curve.N) < 0; }

    private static byte[] Fixed32(BcBig value)
    {
        var bytes = value.ToByteArrayUnsigned();
        if (bytes.Length == 32) return bytes;
        var result = new byte[32];
        bytes.CopyTo(result, 32 - bytes.Length);
        return result;
    }
}

public static class Sha256
{
    public static byte[] Hash(ReadOnlySpan<byte> data) => SHA256.HashData(data);
}
