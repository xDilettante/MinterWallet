using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Minter.Protocol;
using MinterWallet.Core;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;

namespace MinterWallet.Checks;

internal static class KeystoreChecks
{
    public const string Abandon = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";
    // Fast parameters for most checks; one check runs the production 64 MiB / 3 passes.
    private static Keystore.KdfParameters Fast() => new(8 * 1024, 1, 1, RandomNumberGenerator.GetBytes(16));

    public static Task Argon2()
    {
        // RFC 9106 5.3 (Argon2id, 32 KiB, 3 passes, 4 lanes, secret and associated data); Node.js crypto.argon2Sync agrees.
        var rfc = new Argon2Parameters.Builder(Argon2Parameters.Argon2id).WithVersion(Argon2Parameters.Version13).WithIterations(3).WithMemoryAsKB(32)
            .WithParallelism(4).WithSalt(Fill(16, 2)).WithSecret(Fill(8, 3)).WithAdditional(Fill(12, 4)).Build();
        var generator = new Argon2BytesGenerator(); generator.Init(rfc);
        var tag = new byte[32]; generator.GenerateBytes(Fill(32, 1), tag);
        Assert.Equal("0d640df58d78766c08c037a34a8b53c9d01ef0452d75b65eb52520e96b01e659", Convert.ToHexStringLower(tag), "RFC 9106 Argon2id");
        // The wallet's parameters (64 MiB, 3 passes, 1 lane), reference from Node.js crypto.argon2Sync.
        var key = Keystore.DeriveKey("correct horse battery staple", new Keystore.KdfParameters(64 * 1024, 3, 1, Encoding.ASCII.GetBytes("0123456789abcdef")));
        Assert.Equal("94c86f541abdb3d9aabfea59aa03963549483e9c0b1a79336e76b54cee6c917e", Convert.ToHexStringLower(key), "wallet Argon2id parameters");
        return Task.CompletedTask;
    }

    public static Task Store()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, Keystore.FileName);
        var (store, session) = Keystore.Create(path, "пароль-кошелька", Fast());
        var js = Fixtures.Js.GetProperty("mnemonics")[0];
        var first = session.AddMnemonic("  Abandon " + string.Join(' ', Abandon.Split(' ').Skip(1)) + " ", "Основной");
        Assert.Equal(js.GetProperty("accounts")[0].GetProperty("address").GetString(), first.Address.ToString(), "mnemonic account 0");
        var second = session.AddNextIndex(first.SecretId!, "Второй");
        Assert.Equal(js.GetProperty("accounts")[1].GetProperty("address").GetString(), second.Address.ToString(), "next index = minterjs-wallet index 1");
        Assert.Equal(1u, second.Index, "index 1");
        var imported = session.AddPrivateKey(TestKeys.Bytes(1), "Ключ");
        Assert.Equal(TestKeys.Address(1), imported.Address, "private key account");
        var watch = session.AddWatchOnly(TestKeys.Address(2), "Наблюдение");
        Assert.Throws<InvalidOperationException>(() => session.AddWatchOnly(TestKeys.Address(2), "Ещё раз"), "duplicate address");
        Assert.Throws<ArgumentException>(() => session.AddMnemonic("abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon", "Плохая"), "checksum");
        Assert.Throws<ArgumentException>(() => Keystore.Create(Path.Combine(temp.Path, "short.json"), "1234567", Fast()), "short password");

        var text = File.ReadAllText(path);
        Assert.True(!text.Contains("abandon") && !text.Contains(Convert.ToHexStringLower(TestKeys.Bytes(1))), "no secret in the clear");
        Assert.True(File.Exists(path + ".bak") && !File.Exists(path + ".tmp"), "atomic write keeps a backup");

        // The phrase is one account with a name of its own, apart from its addresses' names.
        session.RenameSecret(first.SecretId!, "Семейный");
        Assert.True(store.NameOf(first.SecretId!) == "Семейный" && store.Accounts.First(a => a.Id == first.Id).Name == "Основной", "the phrase renamed, its address keeps its name");

        // Reload from disk: same accounts and names, keys sign, wrong password refused.
        var loaded = Keystore.Load(path);
        Assert.Equal(4, loaded.Accounts.Count, "accounts reloaded");
        Assert.True(loaded.NameOf(first.SecretId!) == "Семейный" && loaded.NameOf(imported.SecretId!) == "Ключ", "account names reloaded");
        // A store written before accounts had names: each phrase is named after its first address.
        var old = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        foreach (var secretNode in old["secrets"]!.AsArray()) secretNode!.AsObject().Remove("name");
        var oldPath = Path.Combine(temp.Path, "old-" + Keystore.FileName);
        File.WriteAllText(oldPath, old.ToJsonString());
        Assert.Equal("Основной", Keystore.Load(oldPath).NameOf(first.SecretId!), "an old store's phrase named after its first address");
        Assert.Throws<WrongPasswordException>(() => loaded.Unlock("не тот пароль"), "wrong password");
        using (var reopened = loaded.Unlock("пароль-кошелька"))
        {
            foreach (var account in loaded.Accounts.Where(a => !a.WatchOnly))
            {
                using var key = reopened.OpenKey(account);
                Assert.Equal(account.Address, key.Address, $"key of {account.Name}");
            }
            Assert.Throws<InvalidOperationException>(() => reopened.OpenKey(loaded.Accounts.Single(a => a.WatchOnly)), "watch-only has no key");
            Assert.Equal(Abandon, reopened.RevealMnemonic(first.SecretId!), "mnemonic backup");

            reopened.ChangePassword("пароль-кошелька", "новый-пароль-2026");
            using var again = reopened.OpenKey(first);
            Assert.Equal(first.Address, again.Address, "key after password change");
            reopened.Lock();
            Assert.Throws<InvalidOperationException>(() => reopened.OpenKey(first), "locked session");
        }
        var changed = Keystore.Load(path);
        Assert.Throws<WrongPasswordException>(() => changed.Unlock("пароль-кошелька"), "old password after change");
        using (var s = changed.Unlock("новый-пароль-2026")) { using var k = s.OpenKey(changed.Accounts[2]); Assert.Equal(TestKeys.Address(1), k.Address, "imported key after change"); }

        // The production parameters work end to end.
        using var temp2 = new TempDirectory();
        var (_, real) = Keystore.Create(Path.Combine(temp2.Path, Keystore.FileName), "пароль-кошелька");
        var a = real.AddMnemonic(Abandon, "Основной");
        using (var key = real.OpenKey(a)) Assert.Equal(first.Address, key.Address, "64 MiB store");
        using (Keystore.Load(Path.Combine(temp2.Path, Keystore.FileName)).Unlock("пароль-кошелька")) { }
        _ = (store, watch);
        return Task.CompletedTask;
    }

    public static Task Tampering()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, Keystore.FileName);
        var (_, session) = Keystore.Create(path, "пароль-кошелька", Fast());
        session.AddPrivateKey(TestKeys.Bytes(1), "Ключ");
        session.AddMnemonic(Abandon, "Основной");

        // An edited address is caught before signing.
        var root = JsonNode.Parse(File.ReadAllText(path))!;
        root["accounts"]![0]!["address"] = TestKeys.Address(3).ToString();
        File.WriteAllText(path, root.ToJsonString());
        var edited = Keystore.Load(path);
        using (var s = edited.Unlock("пароль-кошелька"))
            Assert.Throws<InvalidDataException>(() => s.OpenKey(edited.Accounts[0]), "edited address refused");

        // A changed ciphertext fails authentication.
        root = JsonNode.Parse(File.ReadAllText(path))!;
        var cipher = Convert.FromBase64String((string)root["secrets"]![1]!["ciphertext"]!);
        cipher[0] ^= 1;
        root["secrets"]![1]!["ciphertext"] = Convert.ToBase64String(cipher);
        File.WriteAllText(path, root.ToJsonString());
        var broken = Keystore.Load(path);
        using (var s = broken.Unlock("пароль-кошелька"))
            Assert.Throws<AuthenticationTagMismatchException>(() => s.OpenKey(broken.Accounts[1]), "changed ciphertext refused");

        // A swapped secret record (other id) fails too: the id and kind are associated data.
        root = JsonNode.Parse(File.ReadAllText(path))!;
        root["secrets"]![0]!["id"] = "s0000000000000000";
        root["accounts"]![0]!["secret"] = "s0000000000000000";
        File.WriteAllText(path, root.ToJsonString());
        var swapped = Keystore.Load(path);
        using (var s = swapped.Unlock("пароль-кошелька"))
            Assert.Throws<AuthenticationTagMismatchException>(() => s.OpenKey(swapped.Accounts[0]), "moved secret refused");

        // Weak derivation parameters in the file are refused at load.
        root["kdf"]!["memoryKiB"] = 1024;
        File.WriteAllText(path, root.ToJsonString());
        Assert.Throws<InvalidDataException>(() => Keystore.Load(path), "weak kdf refused");
        return Task.CompletedTask;
    }

    private static byte[] Fill(int length, byte value) => Enumerable.Repeat(value, length).ToArray();
}

internal static class TestKeys
{
    public static byte[] Bytes(int n) => SHA256.HashData(Encoding.UTF8.GetBytes($"minterwallet-test-{n}"));
    public static Address Address(int n) { using var k = new PrivateKey(Bytes(n)); return k.Address; }
}

internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("minterwallet-checks-").FullName;
    public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
}

internal static class Fixtures
{
    private static string File(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
    public static JsonElement Js => JsonDocument.Parse(System.IO.File.ReadAllText(File("js-expected.json"))).RootElement;
    public static JsonElement Go => JsonDocument.Parse(System.IO.File.ReadAllText(File("go-expected.json"))).RootElement;
}

internal static class Assert
{
    public static void True(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    public static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"{what}: expected {expected}, got {actual}");
    }
    public static void Throws<TException>(Action action, string what) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        catch (Exception other) { throw new InvalidOperationException($"{what}: expected {typeof(TException).Name}, got {other.GetType().Name}: {other.Message}"); }
        throw new InvalidOperationException($"{what}: expected {typeof(TException).Name}");
    }
    public static async Task ThrowsAsync<TException>(Func<Task> action, string what) where TException : Exception
    {
        try { await action(); }
        catch (TException) { return; }
        throw new InvalidOperationException($"{what}: expected {typeof(TException).Name}");
    }
}
