using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Minter.Protocol;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;

namespace MinterWallet.Core;

public enum SecretKind { Mnemonic, PrivateKey }

public sealed record WalletAccount(string Id, string Name, Address Address, string? SecretId, uint Index, DateTimeOffset Created)
{
    public bool WatchOnly => SecretId == null;
}

public sealed class WrongPasswordException() : Exception("Неверный пароль.");

// keystore.json, format 1. One password for the whole store: Argon2id (64 MiB, 3 passes, 1 lane, 16-byte salt) gives
// a 32-byte key; every secret (a BIP39 mnemonic or a raw private key) is sealed with AES-256-GCM under a fresh 12-byte
// nonce, with the secret id and kind as associated data. A fixed verifier record tells a wrong password apart from a
// damaged file. Addresses are stored in the clear (they are public) and are re-derived from the secret before every
// signature, so an edited address field cannot redirect a signature.
public sealed class Keystore
{
    public const string FileName = "keystore.json";
    private const string Format = "minterwallet-keystore";
    private static readonly byte[] VerifierText = "minterwallet-keystore-v1"u8.ToArray();

    public sealed record KdfParameters(int MemoryKiB, int Iterations, int Parallelism, byte[] Salt)
    {
        public static KdfParameters Default() => new(64 * 1024, 3, 1, RandomNumberGenerator.GetBytes(16));
    }

    private sealed record Sealed(string Nonce, string Ciphertext, string Tag);
    private sealed record SecretRecord(string Id, SecretKind Kind, Sealed Box);

    private readonly string path;
    private KdfParameters kdf;
    private Sealed verifier;
    private readonly List<SecretRecord> secrets = [];
    private readonly List<WalletAccount> accounts = [];
    // The name of an account as a whole — a phrase with all its addresses, or a key — kept in the clear like the
    // addresses' own names. A store written before it had names gets the name of each secret's first address.
    private readonly Dictionary<string, string> secretNames = [];
    public IReadOnlyList<WalletAccount> Accounts => accounts;
    public string Path => path;

    private Keystore(string path, KdfParameters kdf, Sealed verifier) { this.path = path; this.kdf = kdf; this.verifier = verifier; }

    public static string DefaultPath(string dataDirectory) => System.IO.Path.Combine(dataDirectory, FileName);
    public static bool Exists(string path) => File.Exists(path);

    // A new store with its first account from a mnemonic (index 0), a private key, or only an address.
    public static (Keystore Store, KeystoreSession Session) Create(string path, string password, KdfParameters? kdf = null)
    {
        if (File.Exists(path)) throw new IOException($"{path} already exists.");
        ValidatePassword(password);
        kdf ??= KdfParameters.Default();
        var key = DeriveKey(password, kdf);
        var store = new Keystore(path, kdf, Seal(key, VerifierText, "verifier"));
        store.Save();
        return (store, new KeystoreSession(store, key));
    }

    public static Keystore Load(string path)
    {
        var root = JsonNode.Parse(File.ReadAllBytes(path)) as JsonObject ?? throw new InvalidDataException("keystore.json is not an object.");
        if ((string?)root["format"] != Format || (int?)root["version"] != 1) throw new InvalidDataException("Unsupported key store format.");
        var k = root["kdf"]!;
        if ((string?)k["algorithm"] != "argon2id" || (int?)k["version"] != 19) throw new InvalidDataException("Unsupported key derivation.");
        var kdf = new KdfParameters((int)k["memoryKiB"]!, (int)k["iterations"]!, (int)k["parallelism"]!, Convert.FromBase64String((string)k["salt"]!));
        if (kdf.MemoryKiB < 8 * 1024 || kdf.Iterations < 1 || kdf.Parallelism < 1 || kdf.Salt.Length < 16) throw new InvalidDataException("Key derivation parameters are too weak.");
        var store = new Keystore(path, kdf, ReadSealed(root["verifier"]!));
        foreach (var s in root["secrets"]!.AsArray())
        {
            store.secrets.Add(new SecretRecord((string)s!["id"]!, Enum.Parse<SecretKind>((string)s["kind"]!), ReadSealed(s)));
            if ((string?)s["name"] is { Length: > 0 } name) store.secretNames[(string)s["id"]!] = name;
        }
        foreach (var a in root["accounts"]!.AsArray())
            store.accounts.Add(new WalletAccount((string)a!["id"]!, (string)a["name"]!, Address.Parse((string)a["address"]!),
                (string?)a["secret"], (uint)a["index"]!, DateTimeOffset.Parse((string)a["created"]!, System.Globalization.CultureInfo.InvariantCulture)));
        foreach (var secret in store.secrets.Where(x => !store.secretNames.ContainsKey(x.Id)))
            store.secretNames[secret.Id] = store.accounts.Where(a => a.SecretId == secret.Id).OrderBy(a => a.Index).FirstOrDefault()?.Name ?? "Счёт";
        return store;
    }

    public KeystoreSession Unlock(string password)
    {
        var key = DeriveKey(password, kdf);
        try { Open(key, verifier, "verifier"); }
        catch (CryptographicException) { CryptographicOperations.ZeroMemory(key); throw new WrongPasswordException(); }
        return new KeystoreSession(this, key);
    }

    // ---- changes (need an unlocked session) ----

    internal WalletAccount AddMnemonic(byte[] key, string mnemonic, string name, uint index)
    {
        if (!Mnemonic.IsValid(mnemonic)) throw new ArgumentException("Мнемоника не прошла проверку: неизвестное слово или контрольная сумма.");
        var normalized = string.Join(' ', Mnemonic.Split(mnemonic));
        var existing = FindMnemonic(key, normalized);
        var secretId = existing ?? NewId("s");
        if (existing == null)
        {
            secrets.Add(new SecretRecord(secretId, SecretKind.Mnemonic, Seal(key, Encoding.UTF8.GetBytes(normalized), Aad(secretId, SecretKind.Mnemonic))));
            secretNames[secretId] = name;
        }
        using var derived = HdKey.FromMnemonic(normalized, index);
        return AddAccount(new WalletAccount(NewId("a"), name, derived.Address, secretId, index, DateTimeOffset.UtcNow));
    }

    internal WalletAccount AddPrivateKey(byte[] key, ReadOnlySpan<byte> privateKey, string name)
    {
        using var parsed = new PrivateKey(privateKey);
        var secretId = NewId("s");
        secrets.Add(new SecretRecord(secretId, SecretKind.PrivateKey, Seal(key, privateKey, Aad(secretId, SecretKind.PrivateKey))));
        secretNames[secretId] = name;
        return AddAccount(new WalletAccount(NewId("a"), name, parsed.Address, secretId, 0, DateTimeOffset.UtcNow));
    }

    internal WalletAccount AddWatchOnly(Address address, string name) => AddAccount(new WalletAccount(NewId("a"), name, address, null, 0, DateTimeOffset.UtcNow));

    // Next unused index of an existing mnemonic.
    internal WalletAccount AddNextIndex(byte[] key, string secretId, string name)
    {
        var record = secrets.Single(s => s.Id == secretId);
        if (record.Kind != SecretKind.Mnemonic) throw new InvalidOperationException("Новые счета выводятся только из мнемоники.");
        var next = accounts.Where(a => a.SecretId == secretId).Select(a => a.Index + 1).DefaultIfEmpty(0u).Max();
        var mnemonic = OpenText(key, record);
        using var derived = HdKey.FromMnemonic(mnemonic, next);
        return AddAccount(new WalletAccount(NewId("a"), name, derived.Address, secretId, next, DateTimeOffset.UtcNow));
    }

    // Addresses of a mnemonic at indices from, from + 1, …, not added: the seed is derived once for all of them.
    internal IReadOnlyList<Address> AddressesOf(byte[] key, string secretId, uint from, int count)
    {
        var record = secrets.Single(s => s.Id == secretId);
        if (record.Kind != SecretKind.Mnemonic) throw new InvalidOperationException("Адреса выводятся только из мнемоники.");
        var seed = Mnemonic.ToSeed(OpenText(key, record));
        try
        {
            var result = new List<Address>(count);
            for (var i = 0; i < count; i++)
            {
                var child = HdKey.DerivePrivateKey(seed, HdKey.MinterAccountPath(from + (uint)i));
                try { using var k = new PrivateKey(child); result.Add(k.Address); }
                finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(child); }
            }
            return result;
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(seed); }
    }

    // A chosen index of a mnemonic, such as one used in another wallet; an index already here is refused.
    internal WalletAccount AddIndex(byte[] key, string secretId, uint index, string name)
    {
        var record = secrets.Single(s => s.Id == secretId);
        if (record.Kind != SecretKind.Mnemonic) throw new InvalidOperationException("Адреса выводятся только из мнемоники.");
        if (accounts.Any(a => a.SecretId == secretId && a.Index == index)) throw new InvalidOperationException("Этот адрес фразы уже в кошельке.");
        using var derived = HdKey.FromMnemonic(OpenText(key, record), index);
        return AddAccount(new WalletAccount(NewId("a"), name, derived.Address, secretId, index, DateTimeOffset.UtcNow));
    }

    internal void Rename(string accountId, string name) { var i = accounts.FindIndex(a => a.Id == accountId); accounts[i] = accounts[i] with { Name = name }; Save(); }

    // The account a secret makes (a phrase with its addresses, a key); a watched address is its own account.
    public string NameOf(string secretId) => secretNames.GetValueOrDefault(secretId, "Счёт");
    internal void RenameSecret(string secretId, string name) { if (secrets.Any(x => x.Id == secretId)) { secretNames[secretId] = name; Save(); } }

    // The whole account: every address of the secret, and the secret itself.
    internal void RemoveSecret(string secretId)
    {
        accounts.RemoveAll(a => a.SecretId == secretId);
        secrets.RemoveAll(x => x.Id == secretId);
        secretNames.Remove(secretId);
        Save();
    }

    internal void Remove(string accountId)
    {
        var account = accounts.Single(a => a.Id == accountId);
        accounts.Remove(account);
        if (account.SecretId != null && accounts.All(a => a.SecretId != account.SecretId)) { secrets.RemoveAll(s => s.Id == account.SecretId); secretNames.Remove(account.SecretId); }
        Save();
    }

    internal byte[] ChangePassword(byte[] oldKey, string newPassword)
    {
        ValidatePassword(newPassword);
        var newKdf = KdfParameters.Default();
        var newKey = DeriveKey(newPassword, newKdf);
        var reopened = secrets.Select(s => (s, Plain: Open(oldKey, s.Box, Aad(s.Id, s.Kind)))).ToList();
        secrets.Clear();
        foreach (var (s, plain) in reopened)
        {
            secrets.Add(s with { Box = Seal(newKey, plain, Aad(s.Id, s.Kind)) });
            CryptographicOperations.ZeroMemory(plain);
        }
        kdf = newKdf;
        verifier = Seal(newKey, VerifierText, "verifier");
        Save();
        return newKey;
    }

    // The signing key of an account: decrypted, re-derived and checked against the stored address.
    internal PrivateKey OpenKey(byte[] key, WalletAccount account)
    {
        if (account.SecretId == null) throw new InvalidOperationException("Счёт только для просмотра: ключа нет.");
        var record = secrets.Single(s => s.Id == account.SecretId);
        PrivateKey result;
        if (record.Kind == SecretKind.Mnemonic) result = HdKey.FromMnemonic(OpenText(key, record), account.Index);
        else
        {
            var raw = Open(key, record.Box, Aad(record.Id, record.Kind));
            try { result = new PrivateKey(raw); }
            finally { CryptographicOperations.ZeroMemory(raw); }
        }
        if (result.Address != account.Address) { result.Dispose(); throw new InvalidDataException("Адрес счёта в хранилище не совпадает с ключом: файл изменён."); }
        return result;
    }

    internal string RevealMnemonic(byte[] key, string secretId)
    {
        var record = secrets.Single(s => s.Id == secretId);
        if (record.Kind != SecretKind.Mnemonic) throw new InvalidOperationException("Это не мнемоника.");
        return OpenText(key, record);
    }

    public SecretKind? KindOf(WalletAccount account) => account.SecretId == null ? null : secrets.Single(s => s.Id == account.SecretId).Kind;

    public void Save()
    {
        var root = new JsonObject
        {
            ["format"] = Format, ["version"] = 1,
            ["kdf"] = new JsonObject { ["algorithm"] = "argon2id", ["version"] = 19, ["memoryKiB"] = kdf.MemoryKiB, ["iterations"] = kdf.Iterations, ["parallelism"] = kdf.Parallelism, ["salt"] = Convert.ToBase64String(kdf.Salt) },
            ["verifier"] = WriteSealed(verifier),
            ["secrets"] = new JsonArray(secrets.Select(s => (JsonNode)new JsonObject { ["id"] = s.Id, ["kind"] = s.Kind.ToString(), ["name"] = NameOf(s.Id), ["nonce"] = s.Box.Nonce, ["ciphertext"] = s.Box.Ciphertext, ["tag"] = s.Box.Tag }).ToArray()),
            ["accounts"] = new JsonArray(accounts.Select(a => (JsonNode)new JsonObject
            {
                ["id"] = a.Id, ["name"] = a.Name, ["address"] = a.Address.ToString(), ["secret"] = a.SecretId, ["index"] = a.Index,
                ["created"] = a.Created.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
            }).ToArray()),
        };
        WalletPaths.WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(root, KeystoreJson.Default.JsonObject), secret: true);
    }

    // ---- primitives ----

    public static byte[] DeriveKey(string password, KdfParameters p)
    {
        var parameters = new Argon2Parameters.Builder(Argon2Parameters.Argon2id).WithVersion(Argon2Parameters.Version13)
            .WithIterations(p.Iterations).WithMemoryAsKB(p.MemoryKiB).WithParallelism(p.Parallelism).WithSalt(p.Salt).Build();
        var generator = new Argon2BytesGenerator();
        generator.Init(parameters);
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        var key = GC.AllocateArray<byte>(32, pinned: true);
        try { generator.GenerateBytes(passwordBytes, key); }
        finally { CryptographicOperations.ZeroMemory(passwordBytes); }
        return key;
    }

    public static void ValidatePassword(string password)
    {
        if (password.Length < 8) throw new ArgumentException("Пароль должен быть не короче 8 символов.");
    }

    private WalletAccount AddAccount(WalletAccount account)
    {
        if (accounts.Any(a => a.Address == account.Address)) throw new InvalidOperationException($"Счёт {account.Address} уже есть в кошельке.");
        accounts.Add(account);
        Save();
        return account;
    }

    private string? FindMnemonic(byte[] key, string normalized) =>
        secrets.Where(s => s.Kind == SecretKind.Mnemonic).FirstOrDefault(s => OpenText(key, s) == normalized)?.Id;

    private static string OpenText(byte[] key, SecretRecord record)
    {
        var plain = Open(key, record.Box, Aad(record.Id, record.Kind));
        try { return Encoding.UTF8.GetString(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private static string Aad(string id, SecretKind kind) => $"minterwallet/secret/{id}/{kind}";
    private static string NewId(string prefix) => prefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));

    private static Sealed Seal(byte[] key, ReadOnlySpan<byte> plain, string aad)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes(aad));
        return new Sealed(Convert.ToBase64String(nonce), Convert.ToBase64String(cipher), Convert.ToBase64String(tag));
    }

    private static byte[] Open(byte[] key, Sealed box, string aad)
    {
        var cipher = Convert.FromBase64String(box.Ciphertext);
        var plain = GC.AllocateArray<byte>(cipher.Length, pinned: true);
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(Convert.FromBase64String(box.Nonce), cipher, Convert.FromBase64String(box.Tag), plain, Encoding.UTF8.GetBytes(aad));
        return plain;
    }

    private static Sealed ReadSealed(JsonNode node) => new((string)node["nonce"]!, (string)node["ciphertext"]!, (string)node["tag"]!);
    private static JsonObject WriteSealed(Sealed s) => new() { ["nonce"] = s.Nonce, ["ciphertext"] = s.Ciphertext, ["tag"] = s.Tag };
}

// An unlocked store: holds only the derived key (pinned, wiped by Lock); secrets are decrypted per use.
public sealed class KeystoreSession : IDisposable
{
    private byte[]? key;
    public Keystore Store { get; }
    public bool IsLocked => key == null;

    internal KeystoreSession(Keystore store, byte[] key) { Store = store; this.key = key; }

    private byte[] Key => key ?? throw new InvalidOperationException("Кошелёк заблокирован.");

    public WalletAccount AddMnemonic(string mnemonic, string name, uint index = 0) => Store.AddMnemonic(Key, mnemonic, name, index);
    public WalletAccount AddPrivateKey(ReadOnlySpan<byte> privateKey, string name) => Store.AddPrivateKey(Key, privateKey, name);
    public WalletAccount AddWatchOnly(Address address, string name) => Store.AddWatchOnly(address, name);
    public WalletAccount AddNextIndex(string secretId, string name) => Store.AddNextIndex(Key, secretId, name);
    public IReadOnlyList<Address> AddressesOf(string secretId, uint from, int count) => Store.AddressesOf(Key, secretId, from, count);
    public WalletAccount AddIndex(string secretId, uint index, string name) => Store.AddIndex(Key, secretId, index, name);
    public void Rename(string accountId, string name) => Store.Rename(accountId, name);
    public void RenameSecret(string secretId, string name) => Store.RenameSecret(secretId, name);
    public void RemoveSecret(string secretId) => Store.RemoveSecret(secretId);
    public void Remove(string accountId) => Store.Remove(accountId);
    public string RevealMnemonic(string secretId) => Store.RevealMnemonic(Key, secretId);
    public PrivateKey OpenKey(WalletAccount account) => Store.OpenKey(Key, account);

    public void ChangePassword(string currentPassword, string newPassword)
    {
        using (Store.Unlock(currentPassword)) { }   // proves the current password
        var newKey = Store.ChangePassword(Key, newPassword);
        CryptographicOperations.ZeroMemory(Key);
        key = newKey;
    }

    public void Lock()
    {
        if (key != null) CryptographicOperations.ZeroMemory(key);
        key = null;
    }
    public void Dispose() => Lock();
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(WriteIndented = true)]
[System.Text.Json.Serialization.JsonSerializable(typeof(JsonObject))]
internal partial class KeystoreJson : System.Text.Json.Serialization.JsonSerializerContext;
