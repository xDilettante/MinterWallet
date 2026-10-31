using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Minter.Protocol;
using MinterWallet.Core;

namespace MinterWallet.Checks;

internal static class DerivationChecks
{
    public static Task PathsAndStorage()
    {
        Assert.Equal(20u, DerivationSafety.Frontier([0, 1000]), "far saved address does not extend recovery gap");
        Assert.Equal(40u, DerivationSafety.Frontier([0, 20]), "continuous activity extends recovery gap");
        Assert.True(DerivationSafety.Recoverable(HdPath.Minter(20), [HdPath.Minter().Text]), "index20 reachable after used index0");
        Assert.True(!DerivationSafety.Recoverable(HdPath.Minter(21), [HdPath.Minter().Text]), "index21 beyond gap");
        Assert.True(!DerivationSafety.Recoverable(HdPath.Minter(0, 1), [HdPath.Minter(1000).Text]), "far index does not activate next account");
        Assert.True(!DerivationSafety.Recoverable(HdPath.Parse("m/44'/60'/0'/1/0"), []), "nonstandard branch needs backup");
        Assert.Equal("m/44'/60'/2'/1/7", HdPath.Parse(" m/44H/60h/2'/1/7 ").Text, "canonical path");
        Assert.Equal("m/0'/9'", HdPath.Parse("m/0'/1'").WithIndex(9).Text, "keep last hardening");
        foreach (var bad in new[] { "", "m", "m//0", "m/-1", "m/+1", "m/2147483648", "m/1.0", "M/0" })
            Assert.Throws<FormatException>(() => HdPath.Parse(bad), "reject " + bad);
        using var temp = new TempDirectory();
        var file = Path.Combine(temp.Path, Keystore.FileName);
        var (store, session) = Keystore.Create(file, "test-password", new(8192, 1, 1, RandomNumberGenerator.GetBytes(16)));
        using (session)
        {
            var first = session.AddMnemonic(KeystoreChecks.Abandon, "Мой кошелёк");
            var before = File.ReadAllText(file);
            var preview = session.PreviewAddress(first.SecretId!, "m/44'/60'/2'/1/7");
            Assert.Equal(before, File.ReadAllText(file), "browsing does not write addresses");
            Assert.Equal(1, store.Accounts.Count, "only tracked addresses stored");
            using var key = session.OpenKey(preview);
            var seed = Mnemonic.ToSeed(KeystoreChecks.Abandon);
            var bytes = HdKey.DerivePrivateKey(seed, preview.Path);
            using var reference = new PrivateKey(bytes);
            Assert.Equal(reference.Address, key.Address, "signing uses full path");
            CryptographicOperations.ZeroMemory(seed); CryptographicOperations.ZeroMemory(bytes);
            var tracked = session.TrackAddress(preview);
            session.Rename(tracked.Id, "Накопления");
            Assert.Equal(2, store.Accounts.Count, "one tracked coordinate");
            Assert.Equal(tracked.Id, session.TrackAddress(preview).Id, "tracking is idempotent");
            var loaded = Keystore.Load(file);
            using var reopened = loaded.Unlock("test-password");
            var restored = loaded.Accounts.Single(a => a.Id == tracked.Id);
            Assert.Equal("m/44'/60'/2'/1/7", restored.Path, "path survives restart");
            Assert.Equal("Накопления", restored.Name, "local name survives restart");
            using var restoredKey = reopened.OpenKey(restored);
            Assert.Equal(key.Address, restoredKey.Address, "same signing key after restart");
            Assert.Throws<InvalidDataException>(() => reopened.OpenKey(restored with { DerivationPath = "m/44'/60'/2'/0/7" }), "path tampering rejected");
            var next = session.PreviewAddress(first.SecretId!, "m/44'/60'/0'/0/2");
            var beforeImport = File.ReadAllText(file);
            Assert.Throws<InvalidDataException>(() => session.ImportAddresses([next, next with { DerivationPath = "m/44'/60'/0'/0/3" }]), "verify entire profile before saving");
            Assert.Equal(beforeImport, File.ReadAllText(file), "failed profile does not change file");
            session.ImportAddresses([next with { Name = "Работа" }]);
            Assert.Equal("Работа", Keystore.Load(file).Accounts.Single(a => a.Path == next.Path).Name, "profile metadata persisted");
        }
        // A real format-1 shape: first mutation backs it up byte for byte.
        var legacy = JsonNode.Parse(File.ReadAllText(file))!;
        legacy["version"] = 1;
        var entries = legacy["accounts"]!.AsArray();
        while (entries.Count > 1) entries.RemoveAt(1);
        entries[0]!.AsObject().Remove("path");
        File.WriteAllText(file, legacy.ToJsonString());
        var original = File.ReadAllBytes(file);
        var migrated = Keystore.Load(file);
        Assert.Equal(HdKey.MinterPath, migrated.Accounts[0].Path, "legacy path preserved");
        using (var unlocked = migrated.Unlock("test-password")) unlocked.Rename(migrated.Accounts[0].Id, "После обновления");
        Assert.True(original.SequenceEqual(File.ReadAllBytes(file + ".v1.backup")), "format-1 backup is exact");
        Assert.Equal(2, (int)JsonNode.Parse(File.ReadAllText(file))!["version"]!, "new schema version");
        return Task.CompletedTask;
    }
}
