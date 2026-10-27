using System.Numerics;
using System.Security.Cryptography;

namespace Minter.Protocol.Checks;

internal static class RlpChecks
{
    public static void Run()
    {
        // Ethereum RLP reference examples.
        Same("83646f67", Rlp.String("dog"));
        Same("c88363617483646f67", Rlp.List(Rlp.String("cat"), Rlp.String("dog")));
        Same("80", Rlp.Bytes([]));
        Same("c0", Rlp.List());
        Same("80", Rlp.Uint(0));
        Same("0f", Rlp.Uint(15));
        Same("820400", Rlp.Uint(1024));
        Same("00", Rlp.Bytes([0]));
        Same("c7c0c1c0c3c0c1c0", Rlp.List(Rlp.List(), Rlp.List(Rlp.List()), Rlp.List(Rlp.List(), Rlp.List(Rlp.List()))));
        Same("b8384c6f72656d20697073756d20646f6c6f722073697420616d65742c20636f6e7365637465747572206164697069736963696e6720656c6974",
            Rlp.String("Lorem ipsum dolor sit amet, consectetur adipisicing elit"));
        Same("a0ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", Rlp.Big(BigInteger.Pow(2, 256) - 1));
        Same("01", Rlp.Bool(true));
        Same("80", Rlp.Bool(false));
        var big = new byte[70000]; RandomNumberGenerator.Fill(big);
        Assert.True(Rlp.Decode(Rlp.Bytes(big)).Span.SequenceEqual(big), "long string round trip");
        var nested = Rlp.List(Rlp.Uint(ulong.MaxValue), Rlp.List(Enumerable.Range(0, 1000).Select(i => Rlp.Uint((ulong)i))), Rlp.Big(BigInteger.Pow(10, 40)));
        var item = Rlp.Decode(nested);
        Assert.Equal(ulong.MaxValue, item[0].UInt64(), "uint64 round trip");
        Assert.Equal(1000, item[1].Count, "long list");
        Assert.Equal(BigInteger.Pow(10, 40), item[2].Big(), "big round trip");
    }

    private static void Same(string hex, byte[] actual) => Assert.Equal(hex, Convert.ToHexStringLower(actual), $"rlp {hex}");
}

internal static class PrimitiveChecks
{
    public static void Run()
    {
        Assert.Equal(Units.PipPerCoin, Units.ParseCoins("1"), "1 coin");
        Assert.Equal(BigInteger.One, Units.ParseCoins("0.000000000000000001"), "1 pip");
        Assert.Equal(BigInteger.Parse("1500000000000000000"), Units.ParseCoins("1,5"), "comma");
        Assert.Equal("1.5", Units.FormatCoins(BigInteger.Parse("1500000000000000000")), "format");
        Assert.Equal("0.000000000000000001", Units.FormatCoins(1), "format pip");
        Assert.Equal("115792089237316195423570985008687907853269984665640564039457.584007913129639935", Units.FormatCoins(BigInteger.Pow(2, 256) - 1), "format max");
        Assert.Throws<FormatException>(() => Units.ParseCoins("0.0000000000000000001"), "19 decimals");
        Assert.Throws<FormatException>(() => Units.ParseCoins("-1"), "negative");
        Assert.Throws<FormatException>(() => Units.ParseCoins("1e3"), "exponent");

        var address = Address.Parse("MX7633980C000139DD3BD24A3F54E06474FA941E16");
        Assert.Equal("Mx7633980c000139dd3bd24a3f54e06474fa941e16", address.ToString(), "address text");
        Assert.True(!Address.TryParse("Mx7633980c000139dd3bd24a3f54e06474fa941e1", out _), "short address");
        Assert.True(!Address.TryParse("0x7633980c000139dd3bd24a3f54e06474fa941e16", out _), "0x address");
        Assert.True(!Address.TryParse("Mx7633980c000139dd3bd24a3f54e06474fa941e1g", out _), "non-hex address");
        Assert.True(CoinSymbol.IsValidTicker("BIP") && CoinSymbol.IsValidTicker("USDTE") && CoinSymbol.IsValidTicker("A123456789"), "valid tickers");
        Assert.True(!CoinSymbol.IsValidTicker("AB") && !CoinSymbol.IsValidTicker("1ABC") && !CoinSymbol.IsValidTicker("abc") && !CoinSymbol.IsValidTicker("ABCDEFGHIJK"), "invalid tickers");
        Assert.Equal("TESTCOIN", CoinSymbol.FromString("TESTCOIN").ToString(), "symbol text");
    }
}

internal static class CryptoChecks
{
    public static void Run()
    {
        // go-ethereum / Ethereum: the key 0x...01 has address 0x7E5F4552091A69125d5DfCb7b8C2659029395Bdf.
        var one = new byte[32]; one[31] = 1;
        using (var key = new PrivateKey(one)) Assert.Equal("Mx7e5f4552091a69125d5dfcb7b8c2659029395bdf", key.Address.ToString(), "address of key 1");
        var half = BigInteger.Parse("7FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF5D576E7357A4501DDFE92F46681B20A0", System.Globalization.NumberStyles.HexNumber);
        var random = new Random(7);
        for (var i = 0; i < 300; i++)
        {
            var keyBytes = new byte[32]; random.NextBytes(keyBytes);
            if (!Secp256k1.IsValidPrivateKey(keyBytes)) continue;
            using var key = new PrivateKey(keyBytes);
            var hash = new byte[32]; random.NextBytes(hash);
            var signature = key.Sign(hash);
            Assert.True(new BigInteger(signature.S, true, true) <= half, "low S");
            Assert.True(Secp256k1.Recover(hash, signature).AsSpan().SequenceEqual(key.PublicKey), "recover");
            Assert.True(key.Sign(hash).ToBytes65().AsSpan().SequenceEqual(signature.ToBytes65()), "deterministic");
        }
        Assert.True(!Secp256k1.IsValidPrivateKey(new byte[32]), "zero key rejected");
        Assert.True(!Secp256k1.IsValidPrivateKey(Convert.FromHexString("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364141")), "key = N rejected");
        var disposed = PrivateKey.Generate(); disposed.Dispose();
        Assert.Throws<ObjectDisposedException>(() => disposed.Sign(new byte[32]), "disposed key");
    }
}

internal static class RejectionChecks
{
    public static void Run()
    {
        Assert.Throws<FormatException>(() => Rlp.Decode(Convert.FromHexString("8100")), "single byte in string header");
        Assert.Throws<FormatException>(() => Rlp.Decode(Convert.FromHexString("b80161")), "long form for a short string");
        Assert.Throws<FormatException>(() => Rlp.Decode(Convert.FromHexString("8361")), "string longer than data");
        Assert.Throws<FormatException>(() => Rlp.Decode(Convert.FromHexString("0101")), "trailing bytes");
        Assert.Throws<FormatException>(() => Rlp.Decode(Convert.FromHexString("820001")).UInt32(), "integer with a leading zero");
        Assert.Throws<FormatException>(() => Rlp.Decode(Convert.FromHexString("850102030405")).UInt32(), "uint32 overflow");
        Assert.Throws<FormatException>(() => Rlp.Decode(Convert.FromHexString("02")).Bool(), "bool 2");

        var send = Fixtures.Go.GetProperty("transactions")[0].GetProperty("hex").GetString()!;
        var tx = Transaction.FromHex(send);
        Assert.Throws<FormatException>(() => TxData.Decode(TxType.Send, Rlp.List(Rlp.Uint(0), Rlp.Bytes(new byte[20]))), "too few data fields");
        Assert.Throws<FormatException>(() => TxData.Decode(TxType.Send, Rlp.List(Rlp.Uint(0), Rlp.Bytes(new byte[19]), Rlp.Uint(1))), "short address");
        Assert.Throws<FormatException>(() => TxData.Decode((TxType)0x13, Rlp.List()), "PriceVote is not V3");
        Assert.Throws<FormatException>(() => Transaction.Decode(Rlp.List(Rlp.Uint(1))), "short envelope");
        Assert.Throws<ArgumentException>(() => Transaction.Create(1, 1, 1, 0, new SendData(0, tx.Sender(), 1), new byte[Transaction.MaxPayload + 1]), "payload limit");
        Assert.Throws<ArgumentException>(() => Transaction.Create(1, 1, 1, 0, new SendData(0, tx.Sender(), 1), serviceData: new byte[Transaction.MaxServiceData + 1]), "service data limit");

        // A changed field changes the recovered sender: the signature covers it.
        var tampered = new Transaction { Nonce = tx.Nonce + 1, ChainId = tx.ChainId, GasPrice = tx.GasPrice, GasCoin = tx.GasCoin, Type = tx.Type, Data = tx.Data, Payload = tx.Payload, ServiceData = tx.ServiceData };
        var raw = Rlp.Decode(Transaction.FromHex(send).Encode());
        var resigned = Transaction.Decode(Rlp.List(Rlp.Uint(tampered.Nonce), Rlp.Uint(1), Rlp.Uint(1), Rlp.Uint(0), Rlp.Uint(1), Rlp.Bytes(tx.Data), Rlp.Bytes([]), Rlp.Bytes([]), Rlp.Uint(1), Rlp.Bytes(raw[9].Span)));
        Assert.True(resigned.Sender() != tx.Sender(), "tampered nonce changes the sender");

        using var key = PrivateKey.Generate();
        var check = Check.Issue(key, "secret", [1], ChainId.Mainnet, 100, 0, 5, 0);
        var forged = Check.Decode(Rlp.List(Rlp.Bytes([1]), Rlp.Uint(1), Rlp.Uint(100), Rlp.Uint(0), Rlp.Uint(6), Rlp.Uint(0), Rlp.Big(check.Lock), Rlp.Big(check.V), Rlp.Big(check.R), Rlp.Big(check.S)));
        Assert.True(forged.Issuer() != key.Address, "a changed check value changes the issuer");
        Assert.True(!forged.AcceptsProof(key.Address, Check.Proof(key.Address, "secret")), "a changed check value breaks its lock");
        Assert.Throws<ArgumentException>(() => Check.Issue(key, "secret", new byte[17], ChainId.Mainnet, 1, 0, 1, 0), "check nonce limit");
    }
}
