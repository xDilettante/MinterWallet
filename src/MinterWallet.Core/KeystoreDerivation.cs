using System.Security.Cryptography;
using System.Text;
using Minter.Protocol;

namespace MinterWallet.Core;

public sealed partial class Keystore
{
    internal void ImportAddresses(byte[] key, IReadOnlyList<WalletAccount> incoming)
    {
        if (incoming.Count is < 1 or > 1000) throw new InvalidDataException("Неверное число адресов профиля.");
        var previous = accounts.ToList();
        var seeds = new Dictionary<string, byte[]>();
        List<WalletAccount> verified;
        try
        {
            verified = incoming.Select(a =>
            {
                if (a.SecretId == null || KindOf(a) != SecretKind.Mnemonic || a.Name.Length > 64 || HdPath.Parse(a.Path).Index != a.Index)
                    throw new InvalidDataException("Неверный адрес профиля.");
                if (!seeds.TryGetValue(a.SecretId, out var seed))
                {
                    var record = secrets.Single(s => s.Id == a.SecretId);
                    seeds[a.SecretId] = seed = Mnemonic.ToSeed(OpenText(key, record));
                }
                var normalized = HdPath.Parse(a.Path).Text;
                var bytes = HdKey.DerivePrivateKey(seed, normalized);
                try
                {
                    using var derived = new PrivateKey(bytes);
                    if (derived.Address != a.Address) throw new InvalidDataException("Адрес счёта в хранилище не совпадает с ключом: файл изменён.");
                }
                finally { CryptographicOperations.ZeroMemory(bytes); }
                return a with { DerivationPath = normalized };
            }).ToList();
        }
        finally { foreach (var seed in seeds.Values) CryptographicOperations.ZeroMemory(seed); }
        try
        {
            foreach (var a in verified)
            {
                var i = accounts.FindIndex(x => x.SecretId == a.SecretId && x.Path == a.Path);
                if (i >= 0) accounts[i] = accounts[i] with { Name = a.Name };
                else
                {
                    if (accounts.Any(x => x.Address == a.Address || x.Id == a.Id)) throw new InvalidDataException("Адрес профиля уже принадлежит другой записи.");
                    accounts.Add(a);
                }
            }
            Save();
        }
        catch { accounts.Clear(); accounts.AddRange(previous); throw; }
    }

    internal Func<List<WalletAccount>> PrepareProfileVerification(byte[] key, string secretId, string rootAddress, IReadOnlyList<(string Path, string Address, string Name)> items)
    {
        if (items.Count is < 1 or > 1000) throw new FormatException("Неизвестный или слишком большой профиль.");
        var record = secrets.Single(s => s.Id == secretId);
        if (record.Kind != SecretKind.Mnemonic) throw new InvalidOperationException("Обычный приватный ключ задаёт один адрес, без HD-деривации.");
        var mnemonic = OpenText(key, record);
        var savedByPath = accounts.Where(a => a.SecretId == secretId).GroupBy(a => a.Path).ToDictionary(g => g.Key, g => g.First());
        var created = accounts.FirstOrDefault(a => a.SecretId == secretId)?.Created ?? DateTimeOffset.UnixEpoch;
        return () =>
        {
            var seed = Mnemonic.ToSeed(mnemonic);
            try
            {
                var rootBytes = HdKey.DerivePrivateKey(seed, HdPath.Minter().Text);
                try
                {
                    using var rootKey = new PrivateKey(rootBytes);
                    if (rootKey.Address.ToString() != rootAddress) throw new InvalidDataException("Профиль принадлежит другой фразе восстановления.");
                }
                finally { CryptographicOperations.ZeroMemory(rootBytes); }

                var verified = new List<WalletAccount>(items.Count);
                foreach (var (rawPath, expectedAddress, name) in items)
                {
                    if (name == null || name.Length > 64 || rawPath == null) throw new FormatException("Слишком длинное название в профиле.");
                    var target = HdPath.Parse(rawPath);
                    WalletAccount account;
                    if (savedByPath.TryGetValue(target.Text, out var saved)) account = saved;
                    else
                    {
                        var bytes = HdKey.DerivePrivateKey(seed, target.Text);
                        try
                        {
                            using var derived = new PrivateKey(bytes);
                            var id = "v" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secretId + ":" + target.Text)))[..24];
                            account = new WalletAccount(id, $"Адрес № {target.Index + 1}", derived.Address, secretId, target.Index, created) { DerivationPath = target.Text };
                        }
                        finally { CryptographicOperations.ZeroMemory(bytes); }
                    }
                    if (account.Address.ToString() != expectedAddress) throw new InvalidDataException("Путь в профиле не соответствует адресу. Импорт отменён.");
                    verified.Add(account with { Name = name });
                }
                return verified;
            }
            finally { CryptographicOperations.ZeroMemory(seed); }
        };
    }
    internal WalletAccount PreviewAddress(byte[] key, string secretId, string path)
        => PreviewAddresses(key, secretId, path, HdPath.Parse(path).Index, 1)[0];

    internal IReadOnlyList<WalletAccount> PreviewAddresses(byte[] key, string secretId, string path, uint from, int count)
    {
        if (count is < 1 or > 50 || (ulong)from + (uint)count - 1 > HdPath.MaxIndex) throw new ArgumentOutOfRangeException(nameof(count));
        var coordinate = HdPath.Parse(path);
        var record = secrets.Single(s => s.Id == secretId);
        if (record.Kind != SecretKind.Mnemonic) throw new InvalidOperationException("Обычный приватный ключ задаёт один адрес, без HD-деривации.");
        var seed = Mnemonic.ToSeed(OpenText(key, record));
        try
        {
            var result = new List<WalletAccount>(count);
            for (var i = 0; i < count; i++)
            {
                var target = coordinate.WithIndex(from + (uint)i);
                if (accounts.FirstOrDefault(a => a.SecretId == secretId && a.Path == target.Text) is { } saved) { result.Add(saved); continue; }
                var bytes = HdKey.DerivePrivateKey(seed, target.Text);
                try
                {
                    using var derived = new PrivateKey(bytes);
                    var id = "v" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secretId + ":" + target.Text)))[..24];
                    result.Add(new WalletAccount(id, $"Адрес № {target.Index + 1}", derived.Address, secretId, target.Index,
                        accounts.FirstOrDefault(a => a.SecretId == secretId)?.Created ?? DateTimeOffset.UnixEpoch) { DerivationPath = target.Text });
                }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            return result;
        }
        finally { CryptographicOperations.ZeroMemory(seed); }
    }

    internal WalletAccount TrackAddress(byte[] key, WalletAccount account)
    {
        if (accounts.FirstOrDefault(a => a.Id == account.Id) is { } saved) return saved;
        if (HdPath.Parse(account.Path).Index != account.Index) throw new InvalidDataException("Индекс не соответствует пути адреса.");
        using var verify = OpenKey(key, account);
        if (KindOf(account) != SecretKind.Mnemonic) throw new InvalidOperationException("Этот адрес не принадлежит HD-кошельку.");
        if (accounts.FirstOrDefault(a => a.SecretId == account.SecretId && a.Path == account.Path) is { } tracked) return tracked;
        try { return AddAccount(account with { DerivationPath = HdPath.Parse(account.Path).Text }); }
        catch { accounts.RemoveAll(a => a.Id == account.Id); throw; }
    }
}
