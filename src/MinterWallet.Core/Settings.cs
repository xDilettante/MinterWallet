using System.Text.Json;
using System.Text.Json.Serialization;
using Minter.Protocol;

namespace MinterWallet.Core;

public sealed class SourceSettings
{
    public string Name { get; set; } = "Своя нода";
    public string Url { get; set; } = "http://127.0.0.1:8843";
    // The network seen at the first successful connection; a source that later reports another network is not used.
    public string? ExpectedNetwork { get; set; }
    // "gate" or "node" as seen at the last connection (a gate answers /gate/v1/info); null until then.
    public string? Kind { get; set; }

    [JsonIgnore] public bool IsDefaultGate => string.Equals(Url, WalletSettings.DefaultGateUrl, StringComparison.OrdinalIgnoreCase);
}

// settings.json: not secret. Sources, gas price cap, auto-lock, theme.
public sealed class WalletSettings
{
    public const string FileName = "settings.json";
    // The project's MinterGate on mainnet: the source a new wallet starts with.
    public const string DefaultGateUrl = "https://188.127.237.145:8851";
    public List<SourceSettings> Sources { get; set; } = [DefaultGate(), new()];
    public int SelectedSource { get; set; }
    public uint GasPriceCap { get; set; } = 10;
    public int AutoLockMinutes { get; set; } = 10;
    // "system", "dark" or "light".
    public string Theme { get; set; } = "system";
    // Older history of mainnet addresses from the Minter explorer, below where a gate's own history begins.
    public bool HistoryFromExplorer { get; set; } = true;
    // Ask GitHub for a newer release at start.
    public bool CheckUpdates { get; set; } = true;
    public string? LastAccountId { get; set; }

    [JsonIgnore] public SourceSettings Source => Sources[Math.Clamp(SelectedSource, 0, Sources.Count - 1)];

    public static WalletSettings Load(string directory)
    {
        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path)) return new WalletSettings();
        WalletSettings settings;
        try { settings = JsonSerializer.Deserialize(File.ReadAllBytes(path), SettingsJson.Default.WalletSettings) ?? new WalletSettings(); }
        catch (JsonException) { return new WalletSettings(); }
        // Settings from before the gate existed: offer it first, keep the source that was in use.
        if (settings.Sources.Count > 0 && !settings.Sources.Any(s => s.IsDefaultGate))
        {
            var selected = settings.Source;
            settings.Sources.Insert(0, DefaultGate());
            settings.SelectedSource = settings.Sources.IndexOf(selected);
        }
        return settings;
    }

    public static SourceSettings DefaultGate() => new() { Name = "MinterGate", Url = DefaultGateUrl, Kind = "gate" };

    public void Save(string directory)
    {
        if (Sources.Count == 0) Sources.Add(new SourceSettings());
        WalletPaths.WriteAtomic(Path.Combine(directory, FileName), JsonSerializer.SerializeToUtf8Bytes(this, SettingsJson.Default.WalletSettings), secret: false);
    }
}

public sealed class AddressBookEntry
{
    public string Address { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTimeOffset LastUsed { get; set; }
}

// addressbook.json: recipients the user has already paid. A recipient that is not here gets a warning before signing:
// Minter addresses have no checksum, so a mistyped or substituted address is still valid.
public sealed class AddressBook
{
    public const string FileName = "addressbook.json";
    private readonly string? directory;
    public List<AddressBookEntry> Entries { get; private set; } = [];

    public AddressBook(string? directory = null)
    {
        this.directory = directory;
        var path = directory == null ? null : Path.Combine(directory, FileName);
        if (path != null && File.Exists(path))
            try { Entries = JsonSerializer.Deserialize(File.ReadAllBytes(path), SettingsJson.Default.ListAddressBookEntry) ?? []; }
            catch (JsonException) { Entries = []; }
    }

    public void Remove(Address address)
    {
        Entries.RemoveAll(e => string.Equals(e.Address, address.ToString(), StringComparison.OrdinalIgnoreCase));
        if (directory != null) WalletPaths.WriteAtomic(Path.Combine(directory, FileName), JsonSerializer.SerializeToUtf8Bytes(Entries, SettingsJson.Default.ListAddressBookEntry), secret: false);
    }

    public bool IsKnown(Address address) => Entries.Any(e => string.Equals(e.Address, address.ToString(), StringComparison.OrdinalIgnoreCase));
    public string? NameOf(Address address) => Entries.FirstOrDefault(e => string.Equals(e.Address, address.ToString(), StringComparison.OrdinalIgnoreCase))?.Name;

    public void Remember(Address address, string? name = null)
    {
        var entry = Entries.FirstOrDefault(e => string.Equals(e.Address, address.ToString(), StringComparison.OrdinalIgnoreCase));
        if (entry == null) Entries.Add(entry = new AddressBookEntry { Address = address.ToString() });
        if (!string.IsNullOrWhiteSpace(name)) entry.Name = name;
        entry.LastUsed = DateTimeOffset.UtcNow;
        if (directory != null) WalletPaths.WriteAtomic(Path.Combine(directory, FileName), JsonSerializer.SerializeToUtf8Bytes(Entries, SettingsJson.Default.ListAddressBookEntry), secret: false);
    }

    // "Mx12 3456 7890 …": the full address in groups of four, for reading it aloud or against another device.
    public static string Grouped(Address address)
    {
        var hex = address.ToString()[2..];
        return "Mx " + string.Join(' ', Enumerable.Range(0, hex.Length / 4).Select(i => hex.Substring(i * 4, 4)));
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(WalletSettings))]
[JsonSerializable(typeof(List<AddressBookEntry>))]
internal partial class SettingsJson : JsonSerializerContext;
