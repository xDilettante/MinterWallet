using Minter.Protocol;
using MinterWallet.Api;
using MinterWallet.Core;

namespace MinterWallet;

// Application state and navigation: settings, key store, the unlocked session and the current source. Pages are view
// models; MainWindow picks the view for each through its DataTemplates.
public sealed class Shell : Observable
{
    private object page = null!;
    public object Page { get => page; private set => Set(ref page, value); }

    public string DataDirectory { get; }
    public WalletSettings Settings { get; }
    public CoinRegistry Coins { get; }
    public AddressBook AddressBook { get; }
    public Keystore? Store { get; private set; }
    public KeystoreSession? Session { get; private set; }
    public NodeApiClient? Backend { get; private set; }
    public WalletService? Wallet { get; private set; }
    public ChainStatus? Status { get; private set; }
    // The first height of the gate's own transaction history (0: not a gate, or a gate without a history index).
    public long GateIndexFrom { get; private set; }
    public ExplorerClient Explorer => explorer ??= new ExplorerClient();
    private ExplorerClient? explorer;

    // Asks the user to confirm and re-enter the password; set by the window (null in tests).
    public Func<ConfirmRequest, Task<string?>>? Confirm { get; set; }
    public Func<string, Task>? CopyText { get; set; }
    public Func<Task<string?>>? PasteText { get; set; }
    public Func<Uri, Task>? OpenUrl { get; set; }

    public Shell(string? dataDirectory = null)
    {
        DataDirectory = WalletPaths.DataDirectory(dataDirectory);
        Settings = WalletSettings.Load(DataDirectory);
        Coins = new CoinRegistry(Path.Combine(DataDirectory, "coins.json"));
        AddressBook = new AddressBook(DataDirectory);
        var path = Keystore.DefaultPath(DataDirectory);
        if (Keystore.Exists(path)) { Store = Keystore.Load(path); Page = new UnlockViewModel(this); }
        else Page = new WelcomeViewModel(this);
    }

    public string KeystorePath => Keystore.DefaultPath(DataDirectory);

    public void Show(object next) => Page = next;

    // The views of the current page built again (another language): the page's model and its state stay.
    public void Redraw()
    {
        var current = page;
        Page = new object();
        Page = current;
    }
    public void ShowWelcome() => Page = new WelcomeViewModel(this);

    // A forgotten password: the key store is set aside under a dated name (never deleted), and the first-run screen
    // opens so the wallet can be restored from its phrase or key. Returns the name the old file got.
    public string StartOver()
    {
        var aside = $"{KeystorePath}.old-{DateTime.Now:yyyyMMdd-HHmmss}";
        File.Move(KeystorePath, aside);
        if (File.Exists(KeystorePath + ".bak")) File.Move(KeystorePath + ".bak", aside + ".bak");
        Store = null;
        ShowWelcome();
        return Path.GetFileName(aside);
    }

    public void Opened(Keystore store, KeystoreSession session)
    {
        Store = store; Session = session;
        Page = new MainViewModel(this);
    }

    public void Lock()
    {
        Session?.Lock();
        Session = null; Wallet = null;
        Page = Store == null ? new WelcomeViewModel(this) : new UnlockViewModel(this);
    }

    public void Disconnect()
    {
        Backend?.Dispose();
        Backend = null; Wallet = null; Status = null;
    }

    // (Re)connects to the selected source; the network must match the one seen before (or is remembered now).
    // The source in use: the selected one, or another of the same network while the selected one is unreachable.
    public SourceSettings ActiveSource => active ?? Settings.Source;
    private SourceSettings? active;

    public async Task<ChainStatus> ConnectAsync(SourceSettings? fallback = null)
    {
        Backend?.Dispose();
        active = fallback;
        var source = ActiveSource;
        Backend = new NodeApiClient(new Uri(source.Url), source.Name);
        var status = await Backend.StatusAsync();
        var gate = await Backend.GateAsync();
        var kind = gate != null ? "gate" : "node";
        GateIndexFrom = gate?.IndexFrom ?? 0;
        if (source.Kind != kind) { source.Kind = kind; Settings.Save(DataDirectory); }
        if (source.ExpectedNetwork == null) { source.ExpectedNetwork = status.Network; Settings.Save(DataDirectory); }
        else if (source.ExpectedNetwork != status.Network)
        {
            Wallet = null; Status = null;
            throw new InvalidOperationException($"Источник «{source.Name}» теперь сообщает сеть {status.Network}, а раньше была {source.ExpectedNetwork}. Отправка с него отключена; проверьте адрес или подтвердите новую сеть в настройках.");
        }
        var baseCoin = await Backend.CoinAsync(0);
        if (baseCoin != null) Coins.Observe(new CoinRef(0, baseCoin.Symbol));
        Status = status;
        Wallet = new WalletService(Backend, Session ?? throw new InvalidOperationException("Кошелёк заблокирован."), WalletService.ChainIdOf(status.Network),
            new GasPolicy { Cap = Settings.GasPriceCap });
        return status;
    }

    // Password re-entry before a signature: checked against the store, never kept.
    public async Task<bool> CheckPasswordAsync(string password)
    {
        if (Store == null) return false;
        try { using var check = await Task.Run(() => Store.Unlock(password)); return true; }
        catch (WrongPasswordException) { return false; }
    }
}

public sealed record ConfirmRequest(string Title, IReadOnlyList<(string Label, string Value)> Rows, IReadOnlyList<string> Warnings, string Action = "Подписать и отправить");
