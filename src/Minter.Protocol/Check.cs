using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Minter.Protocol;

// coreV2/check Check: an offline promise of the issuer, redeemable once before DueBlock by whoever knows the password.
// Lock = signature of HashWithoutLock by the key sha256(password); the issuer signs Hash (which includes Lock).
// The redeemer proves knowledge of the password with Proof = signature of keccak(rlp([redeemer address])) by that key.
public sealed class Check
{
    public byte[] Nonce { get; init; } = [];
    public byte ChainId { get; init; } = Protocol.ChainId.Mainnet;
    public ulong DueBlock { get; init; }
    public uint Coin { get; init; }
    public BigInteger Value { get; init; }
    public uint GasCoin { get; init; }
    public BigInteger Lock { get; private set; }
    public BigInteger V { get; private set; }
    public BigInteger R { get; private set; }
    public BigInteger S { get; private set; }

    public static Check Issue(PrivateKey issuer, string password, byte[] nonce, byte chainId, ulong dueBlock, uint coin, BigInteger value, uint gasCoin)
    {
        if (nonce.Length > 16) throw new ArgumentException("A check nonce is at most 16 bytes.");
        var check = new Check { Nonce = nonce, ChainId = chainId, DueBlock = dueBlock, Coin = coin, Value = value, GasCoin = gasCoin };
        using (var lockKey = PasswordKey(password))
            check.Lock = new BigInteger(lockKey.Sign(check.HashWithoutLock()).ToBytes65(), isUnsigned: true, isBigEndian: true);
        var signature = issuer.Sign(check.Hash());
        check.V = signature.V;
        check.R = new BigInteger(signature.R, true, true);
        check.S = new BigInteger(signature.S, true, true);
        return check;
    }

    public static byte[] Proof(Address redeemer, string password)
    {
        using var lockKey = PasswordKey(password);
        return lockKey.Sign(Keccak.Hash256(Rlp.List(Rlp.Bytes(redeemer.Span)))).ToBytes65();
    }

    public byte[] HashWithoutLock() => Keccak.Hash256(Rlp.List(
        Rlp.Bytes(Nonce), Rlp.Uint(ChainId), Rlp.Uint(DueBlock), Rlp.Uint(Coin), Rlp.Big(Value), Rlp.Uint(GasCoin)));

    public byte[] Hash() => Keccak.Hash256(Rlp.List(
        Rlp.Bytes(Nonce), Rlp.Uint(ChainId), Rlp.Uint(DueBlock), Rlp.Uint(Coin), Rlp.Big(Value), Rlp.Uint(GasCoin), Rlp.Big(Lock)));

    public Address Issuer()
    {
        if (V != 27 && V != 28) throw new FormatException("Check signature V must be 27 or 28.");
        var signature = new RecoverableSignature(Transaction.Fixed32(R), Transaction.Fixed32(S), (byte)(V - 27));
        return Address.FromPublicKey(Secp256k1.Recover(Hash(), signature));
    }

    // True when the proof was made with the same password as the check's lock (the network compares the two keys).
    public bool AcceptsProof(Address redeemer, ReadOnlySpan<byte> proof)
    {
        var lockBytes = Lock.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (lockBytes.Length > 65) return false;
        var padded = new byte[65];
        lockBytes.CopyTo(padded, 65 - lockBytes.Length);
        try
        {
            var lockKey = Secp256k1.Recover(HashWithoutLock(), RecoverableSignature.FromBytes65(padded));
            var proofKey = Secp256k1.Recover(Keccak.Hash256(Rlp.List(Rlp.Bytes(redeemer.Span))), RecoverableSignature.FromBytes65(proof));
            return lockKey.AsSpan().SequenceEqual(proofKey);
        }
        catch (FormatException) { return false; }
    }

    public RedeemCheckData Redeem(Address redeemer, string password) => new(Encode(), Proof(redeemer, password));

    public byte[] Encode() => Rlp.List(
        Rlp.Bytes(Nonce), Rlp.Uint(ChainId), Rlp.Uint(DueBlock), Rlp.Uint(Coin), Rlp.Big(Value), Rlp.Uint(GasCoin),
        Rlp.Big(Lock), Rlp.Big(V), Rlp.Big(R), Rlp.Big(S));

    public override string ToString() => "Mc" + Convert.ToHexStringLower(Encode());

    public static Check Decode(ReadOnlySpan<byte> raw)
    {
        var v = Rlp.Decode(raw).ExpectCount(10);
        return new Check
        {
            Nonce = v[0].ToBytes(), ChainId = v[1].Byte(), DueBlock = v[2].UInt64(), Coin = v[3].UInt32(), Value = v[4].Big(), GasCoin = v[5].UInt32(),
            Lock = v[6].Big(), V = v[7].Big(), R = v[8].Big(), S = v[9].Big()
        };
    }
    public static Check Parse(string text) => Decode(Hex.Parse(text.Trim(), "Mc"));

    private static PrivateKey PasswordKey(string password)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(password));
        try { return new PrivateKey(hash); }
        finally { CryptographicOperations.ZeroMemory(hash); }
    }
}
