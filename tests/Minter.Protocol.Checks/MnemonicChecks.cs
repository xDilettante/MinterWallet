namespace Minter.Protocol.Checks;

// BIP39/BIP32 against minterjs-wallet (the derivation the official Minter wallets use) and bip39.
internal static class MnemonicChecks
{
    public static void Run()
    {
        var js = Fixtures.Js;
        var count = 0;
        foreach (var m in js.GetProperty("mnemonics").EnumerateArray())
        {
            var entropy = Convert.FromHexString(m.GetProperty("entropy").GetString()!);
            var mnemonic = m.GetProperty("mnemonic").GetString()!;
            Assert.Equal(mnemonic, Mnemonic.FromEntropy(entropy), "entropy to mnemonic");
            Assert.True(Mnemonic.IsValid(mnemonic), $"valid mnemonic: {mnemonic}");
            Assert.Equal(m.GetProperty("seed").GetString(), Convert.ToHexStringLower(Mnemonic.ToSeed(mnemonic)), "seed");
            Assert.Equal(m.GetProperty("seedTrezor").GetString(), Convert.ToHexStringLower(Mnemonic.ToSeed(mnemonic, "TREZOR")), "seed with passphrase");
            foreach (var a in m.GetProperty("accounts").EnumerateArray())
            {
                using var key = HdKey.FromMnemonic(mnemonic, a.GetProperty("index").GetUInt32());
                Assert.Equal(a.GetProperty("privateKey").GetString(), Convert.ToHexStringLower(key.ExportBytes()), "derived private key");
                Assert.Equal(a.GetProperty("address").GetString(), key.Address.ToString(), "derived address");
            }
            count++;
        }
        Assert.True(count >= 10, "mnemonic vectors present");
        foreach (var m in js.GetProperty("invalid").EnumerateArray())
            Assert.Equal(m.GetProperty("valid").GetBoolean(), Mnemonic.IsValid(m.GetProperty("mnemonic").GetString()!), $"validity of '{m.GetProperty("mnemonic").GetString()}'");

        // Canonical BIP39 vector (Trezor): "abandon … about" with passphrase TREZOR.
        Assert.Equal("c55257c360c07c72029aebc1b53c05ed0362ada38ead3e3e9efa3708e53495531f09a6987599d18264c1e1c92f2cf141630c7a3c4ab7c81b2f001698e7463b04",
            Convert.ToHexStringLower(Mnemonic.ToSeed("abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about", "TREZOR")), "Trezor vector");
        foreach (var words in new[] { 12, 15, 18, 21, 24 })
        {
            var generated = Mnemonic.Generate(words);
            Assert.Equal(words, Mnemonic.Split(generated).Length, "generated word count");
            Assert.True(Mnemonic.IsValid(generated), "generated mnemonic is valid");
        }
        Assert.True(Mnemonic.IsValid("  Abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon ABOUT "), "case and spaces ignored");
    }
}
