using System.Numerics;

namespace Minter.Protocol;

public enum SignatureType : byte { Single = 0x01, Multi = 0x02 }

public static class ChainId
{
    public const byte Mainnet = 0x01;
    public const byte Testnet = 0x02;
}

// coreV2/transaction Transaction: the 10-field RLP envelope. The signing hash covers the first nine fields (no
// signature); a single signature is rlp([V, R, S]); a multisignature is rlp([multisig address, [[V, R, S], ...]]) and
// every participant signs the same hash.
public sealed class Transaction
{
    public const int MaxPayload = 10000, MaxServiceData = 128;

    public ulong Nonce { get; init; }
    public byte ChainId { get; init; } = Protocol.ChainId.Mainnet;
    public uint GasPrice { get; init; } = 1;
    public uint GasCoin { get; init; }
    public TxType Type { get; init; }
    public byte[] Data { get; init; } = [];
    public byte[] Payload { get; init; } = [];
    public byte[] ServiceData { get; init; } = [];
    public SignatureType SignatureType { get; init; } = SignatureType.Single;
    public byte[] SignatureData { get; private set; } = [];

    public static Transaction Create(ulong nonce, byte chainId, uint gasPrice, uint gasCoin, ITxData data, byte[]? payload = null,
        byte[]? serviceData = null, SignatureType signatureType = SignatureType.Single)
    {
        payload ??= []; serviceData ??= [];
        if (payload.Length > MaxPayload) throw new ArgumentException($"Payload is limited to {MaxPayload} bytes.");
        if (serviceData.Length > MaxServiceData) throw new ArgumentException($"Service data is limited to {MaxServiceData} bytes.");
        return new Transaction
        {
            Nonce = nonce, ChainId = chainId, GasPrice = gasPrice, GasCoin = gasCoin, Type = data.Type,
            Data = data.Encode(), Payload = payload, ServiceData = serviceData, SignatureType = signatureType
        };
    }

    public ITxData DecodeData() => TxData.Decode(Type, Data);

    public byte[] SigningHash() => Keccak.Hash256(Rlp.List(
        Rlp.Uint(Nonce), Rlp.Uint(ChainId), Rlp.Uint(GasPrice), Rlp.Uint(GasCoin), Rlp.Uint((byte)Type),
        Rlp.Bytes(Data), Rlp.Bytes(Payload), Rlp.Bytes(ServiceData), Rlp.Uint((byte)SignatureType)));

    public void Sign(PrivateKey key)
    {
        var signature = key.Sign(SigningHash());
        if (SignatureType == SignatureType.Single) SignatureData = EncodeSignature(signature);
        else AddMultisigSignature(signature);
    }

    public void SetMultisigAddress(Address multisig)
    {
        if (SignatureType != SignatureType.Multi) throw new InvalidOperationException("Not a multisignature transaction.");
        var (_, signatures) = SignatureData.Length == 0 ? (default, new List<RecoverableSignature>()) : Multisignature();
        SignatureData = EncodeMultisig(multisig, signatures);
    }

    public void AddMultisigSignature(RecoverableSignature signature)
    {
        if (SignatureType != SignatureType.Multi || SignatureData.Length == 0) throw new InvalidOperationException("Set the multisig address first.");
        var (address, signatures) = Multisignature();
        signatures.Add(signature);
        SignatureData = EncodeMultisig(address, signatures);
    }

    public (Address Multisig, List<RecoverableSignature> Signatures) Multisignature()
    {
        if (SignatureType != SignatureType.Multi) throw new InvalidOperationException("Not a multisignature transaction.");
        var v = Rlp.Decode(SignatureData).ExpectCount(2);
        return (TxData.Addr(v[0]), v[1].Items.Select(DecodeSignature).ToList());
    }

    public RecoverableSignature Signature()
    {
        if (SignatureType != SignatureType.Single) throw new InvalidOperationException("Not a single-signature transaction.");
        return DecodeSignature(Rlp.Decode(SignatureData));
    }

    // Single: the address recovered from the signature; multi: the multisig address (signatures are checked by state).
    public Address Sender() => SignatureType switch
    {
        SignatureType.Single => Address.FromPublicKey(Secp256k1.Recover(SigningHash(), Signature())),
        SignatureType.Multi => Multisignature().Multisig,
        _ => throw new FormatException("Unknown signature type.")
    };

    // Addresses that signed a multisignature transaction, in signature order.
    public IReadOnlyList<Address> MultisigSigners()
    {
        var hash = SigningHash();
        return Multisignature().Signatures.Select(s => Address.FromPublicKey(Secp256k1.Recover(hash, s))).ToList();
    }

    public byte[] Encode() => Rlp.List(
        Rlp.Uint(Nonce), Rlp.Uint(ChainId), Rlp.Uint(GasPrice), Rlp.Uint(GasCoin), Rlp.Uint((byte)Type),
        Rlp.Bytes(Data), Rlp.Bytes(Payload), Rlp.Bytes(ServiceData), Rlp.Uint((byte)SignatureType), Rlp.Bytes(SignatureData));

    public string ToHex() => Convert.ToHexStringLower(Encode());

    // Tendermint transaction hash: SHA-256 of the encoded bytes, shown as "Mt" + hex.
    public string Hash => "Mt" + Convert.ToHexStringLower(Sha256.Hash(Encode()));

    public static Transaction Decode(ReadOnlySpan<byte> raw)
    {
        var v = Rlp.Decode(raw).ExpectCount(10);
        var type = v[8].Byte();
        if (type is not (1 or 2)) throw new FormatException("Unknown signature type.");
        var tx = new Transaction
        {
            Nonce = v[0].UInt64(), ChainId = v[1].Byte(), GasPrice = v[2].UInt32(), GasCoin = v[3].UInt32(), Type = (TxType)v[4].Byte(),
            Data = v[5].ToBytes(), Payload = v[6].ToBytes(), ServiceData = v[7].ToBytes(), SignatureType = (SignatureType)type
        };
        tx.SignatureData = v[9].ToBytes();
        return tx;
    }
    public static Transaction FromHex(string hex) => Decode(Hex.Parse(hex));

    // A partially signed multisignature transaction travels between participants as its encoded bytes.
    public Transaction Copy() => Decode(Encode());

    private static byte[] EncodeSignature(RecoverableSignature s) =>
        Rlp.List(Rlp.Uint(s.V), Rlp.Big(new BigInteger(s.R, true, true)), Rlp.Big(new BigInteger(s.S, true, true)));

    private static RecoverableSignature DecodeSignature(RlpItem v)
    {
        v.ExpectCount(3);
        var vv = v[0].Big();
        if (vv != 27 && vv != 28) throw new FormatException("Signature V must be 27 or 28.");
        return new RecoverableSignature(Fixed32(v[1].Big()), Fixed32(v[2].Big()), (byte)(vv - 27));
    }

    private static byte[] EncodeMultisig(Address multisig, IEnumerable<RecoverableSignature> signatures) =>
        Rlp.List(Rlp.Bytes(multisig.Span), Rlp.List(signatures.Select(EncodeSignature)));

    internal static byte[] Fixed32(BigInteger value)
    {
        var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (bytes.Length > 32) throw new FormatException("Signature value exceeds 32 bytes.");
        var result = new byte[32];
        bytes.CopyTo(result, 32 - bytes.Length);
        return result;
    }
}
