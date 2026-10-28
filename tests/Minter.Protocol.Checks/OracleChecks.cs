using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace Minter.Protocol.Checks;

// Byte-for-byte agreement with the upstream Go node (go-expected.json) and the official JS libraries (js-expected.json).
internal static class OracleChecks
{
    private static IEnumerable<(JsonElement Case, JsonElement Go)> TxPairs()
    {
        var cases = Fixtures.Cases.GetProperty("transactions").EnumerateArray().ToArray();
        var go = Fixtures.Go.GetProperty("transactions").EnumerateArray().ToArray();
        Assert.Equal(cases.Length, go.Length, "case count");
        for (var i = 0; i < cases.Length; i++)
        {
            Assert.Equal(cases[i].GetProperty("name").GetString(), go[i].GetProperty("name").GetString(), "case order");
            yield return (cases[i], go[i]);
        }
    }

    public static Transaction Build(JsonElement c)
    {
        var type = (TxType)c.GetProperty("type").GetByte();
        var multisig = c.TryGetProperty("multisig", out var m) && m.ValueKind == JsonValueKind.Object;
        var tx = Transaction.Create(CaseData.U64(c.GetProperty("nonce")), c.GetProperty("chainId").GetByte(), CaseData.U32(c, "gasPrice"), CaseData.U32(c, "gasCoin"),
            CaseData.Data(type, c.GetProperty("data")), CaseData.Hex(c.GetProperty("payload").GetString()!), CaseData.Hex(c.GetProperty("serviceData").GetString()!),
            multisig ? SignatureType.Multi : SignatureType.Single);
        if (!multisig)
        {
            using var key = PrivateKey.FromHex(c.GetProperty("key").GetString()!);
            tx.Sign(key);
            return tx;
        }
        tx.SetMultisigAddress(Address.Parse(m.GetProperty("address").GetString()!));
        foreach (var k in m.GetProperty("keys").EnumerateArray())
        {
            using var key = PrivateKey.FromHex(k.GetString()!);
            tx.Sign(key);
        }
        return tx;
    }

    public static void Transactions()
    {
        var types = new HashSet<TxType>();
        foreach (var (c, go) in TxPairs())
        {
            var name = c.GetProperty("name").GetString();
            var tx = Build(c);
            Assert.Equal(go.GetProperty("hex").GetString(), tx.ToHex(), $"{name} bytes");
            Assert.Equal(go.GetProperty("hash").GetString(), tx.Hash, $"{name} hash");
            Assert.Equal(go.GetProperty("sender").GetString(), tx.Sender().ToString(), $"{name} sender");
            if (go.TryGetProperty("signers", out var signers))
                Assert.Equal(string.Join(",", signers.EnumerateArray().Select(s => s.GetString())), string.Join(",", tx.MultisigSigners()), $"{name} signers");
            types.Add(tx.Type);
        }
        Assert.Equal(TxData.Types.Count, types.Count, "transaction types covered");
        Assert.Equal(37, TxData.Types.Count, "V3 transaction types");
    }

    public static void Commissions()
    {
        var prices = CaseData.Prices(Fixtures.Cases.GetProperty("prices"));
        foreach (var (c, go) in TxPairs())
        {
            var expected = BigInteger.Parse(go.GetProperty("price").GetString()!, CultureInfo.InvariantCulture);
            Assert.Equal(expected, Commission.Price(Transaction.FromHex(go.GetProperty("hex").GetString()!), prices), $"{c.GetProperty("name").GetString()} commission");
        }
    }

    // Bytes produced by Go decode into our records and re-encode to the same bytes.
    public static void DecodeRoundTrip()
    {
        foreach (var (c, go) in TxPairs())
        {
            var name = c.GetProperty("name").GetString();
            var hex = go.GetProperty("hex").GetString()!;
            var tx = Transaction.FromHex(hex);
            Assert.Equal(hex, tx.ToHex(), $"{name} envelope round trip");
            var data = tx.DecodeData();
            Assert.True(data.Encode().AsSpan().SequenceEqual(tx.Data), $"{name} data round trip");
            Assert.True(data.Encode().AsSpan().SequenceEqual(CaseData.Data(tx.Type, c.GetProperty("data")).Encode()), $"{name} decoded fields");
            Assert.Equal(go.GetProperty("sender").GetString(), tx.Sender().ToString(), $"{name} decoded sender");
        }
    }

    public static void Multisig()
    {
        var cases = Fixtures.Cases.GetProperty("multisigAddresses").EnumerateArray().ToArray();
        var go = Fixtures.Go.GetProperty("multisigAddresses").EnumerateArray().ToArray();
        Assert.Equal(cases.Length, go.Length, "multisig address count");
        for (var i = 0; i < cases.Length; i++)
            Assert.Equal(go[i].GetString(), Address.Multisig(Address.Parse(cases[i].GetProperty("owner").GetString()!), CaseData.U64(cases[i].GetProperty("nonce"))).ToString(), $"multisig address {i}");
    }

    public static void Checks()
    {
        var cases = Fixtures.Cases.GetProperty("checks").EnumerateArray().ToArray();
        var go = Fixtures.Go.GetProperty("checks").EnumerateArray().ToArray();
        Assert.Equal(cases.Length, go.Length, "check count");
        for (var i = 0; i < cases.Length; i++)
        {
            var c = cases[i]; var name = c.GetProperty("name").GetString();
            var (check, proof, redeemer, password) = Issue(c);
            Assert.Equal(go[i].GetProperty("check").GetString(), check.ToString(), $"{name} check");
            Assert.Equal(go[i].GetProperty("proof").GetString(), Convert.ToHexStringLower(proof), $"{name} proof");
            Assert.Equal(go[i].GetProperty("issuer").GetString(), check.Issuer().ToString(), $"{name} issuer");
            Assert.True(go[i].GetProperty("proofMatchesLock").GetBoolean() && check.AcceptsProof(redeemer, proof), $"{name} proof accepted");
            Assert.True(!check.AcceptsProof(redeemer, Check.Proof(redeemer, password + "x")), $"{name} wrong password rejected");
            var parsed = Check.Parse(check.ToString());
            Assert.Equal(check.ToString(), parsed.ToString(), $"{name} parse round trip");
            Assert.Equal(check.Issuer(), parsed.Issuer(), $"{name} parsed issuer");
        }
    }

    public static void JsChecks()
    {
        var cases = Fixtures.Cases.GetProperty("checks").EnumerateArray().ToDictionary(c => c.GetProperty("name").GetString()!);
        var js = Fixtures.Js.GetProperty("checks").EnumerateArray().ToArray();
        Assert.True(js.Length >= 2, "js checks present");
        foreach (var j in js)
        {
            var name = j.GetProperty("name").GetString()!;
            var (check, proof, _, _) = Issue(cases[name]);
            Assert.Equal(j.GetProperty("check").GetString(), check.ToString(), $"{name} check vs minter-js-sdk");
            Assert.Equal(j.GetProperty("proof").GetString(), Convert.ToHexStringLower(proof), $"{name} proof vs minter-js-sdk");
        }
    }

    // Minter Links: the same text as minter-js-sdk prepareLink, and read back as decodeLink reads it.
    public static void JsLinks()
    {
        var links = Fixtures.Js.GetProperty("links").EnumerateArray().ToArray();
        Assert.True(links.Length >= 4, "js links present");
        foreach (var l in links)
        {
            var name = l.GetProperty("name").GetString()!;
            uint? Optional(string field) => l.TryGetProperty(field, out var v) ? v.GetUInt32() : null;
            var send = new SendData(l.GetProperty("coin").GetUInt32(), Address.Parse(l.GetProperty("to").GetString()!), Units.ParseCoins(l.GetProperty("value").GetString()!));
            var payload = l.TryGetProperty("payload", out var p) ? System.Text.Encoding.UTF8.GetBytes(p.GetString()!) : [];
            var link = MinterLink.Of(send, payload) with { Nonce = Optional("nonce"), GasPrice = Optional("gasPrice"), GasCoin = Optional("gasCoin") };
            var url = l.GetProperty("url").GetString()!;
            Assert.Equal(url, link.ToString(), $"{name} link vs minter-js-sdk");
            var back = MinterLink.Parse(url) ?? throw new InvalidOperationException($"{name}: link not read");
            Assert.Equal(send, back.DecodeData(), $"{name} send read back");
            Assert.Equal(Convert.ToHexString(payload), Convert.ToHexString(back.Payload), $"{name} payload read back");
            Assert.Equal((link.Nonce, link.GasPrice, link.GasCoin), (back.Nonce, back.GasPrice, back.GasCoin), $"{name} gas settings read back");
        }
        foreach (var c in Fixtures.Js.GetProperty("checkLinks").EnumerateArray())
        {
            var raw = Convert.FromHexString(c.GetProperty("check").GetString()![2..]);
            var password = c.GetProperty("password").GetString()!;
            var link = new MinterLink(TxType.RedeemCheck, Rlp.List(Rlp.Bytes(raw), Rlp.Bytes([])), [], Password: password);
            var url = c.GetProperty("url").GetString()!;
            Assert.Equal(url, link.ToString(), "check link vs minter-js-sdk");
            var back = MinterLink.Parse(url)!;
            Assert.True(back.Check().SequenceEqual(raw) && back.Password == password, "check and password read back");
        }
        Assert.True(MinterLink.Parse("bip.to/tx/" + links[0].GetProperty("url").GetString()!.Split("/tx/")[1]) != null, "a link without the scheme");
        Assert.True(MinterLink.Parse("https://bip.to/tx/not-rlp") == null && MinterLink.Parse("Mx7633980c000139dd3bd24a3f54e06474fa941e16") == null, "not a link");
    }

    private static (Check Check, byte[] Proof, Address Redeemer, string Password) Issue(JsonElement c)
    {
        using var key = PrivateKey.FromHex(c.GetProperty("key").GetString()!);
        var password = c.GetProperty("password").GetString()!;
        var check = Minter.Protocol.Check.Issue(key, password, CaseData.Hex(c.GetProperty("nonce").GetString()!), c.GetProperty("chainId").GetByte(),
            CaseData.U64(c.GetProperty("dueBlock")), CaseData.U32(c, "coin"), BigInteger.Parse(c.GetProperty("value").GetString()!, CultureInfo.InvariantCulture), CaseData.U32(c, "gasCoin"));
        var redeemer = Address.Parse(c.GetProperty("redeemer").GetString()!);
        return (check, Minter.Protocol.Check.Proof(redeemer, password), redeemer, password);
    }
}
