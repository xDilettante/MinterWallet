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
    public string Title => $"Minter Wallet v{Program.Version}";

    public string DataDirectory { get; }
    public WalletSettings Settings { get; }
    public WalletCredentials Credentials { get; }
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
    // Ethereum and BSC nodes for the bridge's deposits, and the bridge's coin list (read once per run).
    public EvmClient EvmOf(BridgeNetwork network) => evm[(int)network] ??= new EvmClient(EvmClient.DefaultEndpoints(network));
    private readonly EvmClient?[] evm = new EvmClient?[2];
    private Task? bridgeTokens;
    public Task LoadBridgeTokensAsync() => bridgeTokens ??= Task.Run(async () =>
    {
        try { using var api = new BridgeApiClient(); Bridge.Use(await api.TokensAsync()); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException) { bridgeTokens = null; }
    });

    // Asks the user to confirm and re-enter the password; set by the window (null in tests).
    public Func<ConfirmRequest, Task<string?>>? Confirm { get; set; }
    public Func<string, Task>? CopyText { get; set; }
    public Func<Task<string?>>? PasteText { get; set; }
    public Func<Task<string?>>? ScanQr { get; set; }
    public Func<Uri, Task>? OpenUrl { get; set; }
    // Saves a text file the user names (suggested name, contents); false when they cancel.
    public Func<string, string, Task<bool>>? SaveText { get; set; }
    public Func<Task<string?>>? OpenText { get; set; }
    // Where the anonymous usage pings go (the project's gate); null: nowhere (MINTERWALLET_USAGE_URL=off, the checks).
    public Uri? UsageEndpoint { get; set; } = UsageClient.Endpoint();
    private Avalonia.Threading.DispatcherTimer? usageTimer;
    private bool sendingUsage;
    private string usageStatus = "Отправка статистики ещё не выполнялась.";
    public string UsageStatus { get => usageStatus; private set => Set(ref usageStatus, value); }
    public UsageClient CreateUsageClient(Uri endpoint) => new(endpoint, skipCertificateCheck: UsageClient.SkipCertificateCheckFor(endpoint, Settings.Sources));

    // A ping now and every 5 minutes while the wallet is open, locked or not, unless turned off in the settings.
    public void StartUsage()
    {
        if (usageTimer != null) return;
        usageTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        usageTimer.Tick += (_, _) => _ = PingUsageAsync();
        usageTimer.Start();
        _ = PingUsageAsync();
    }

    public async Task PingUsageAsync()
    {
        if (!Settings.Telemetry || UsageEndpoint is not { } endpoint) { UsageStatus = "Отправка анонимной статистики отключена."; return; }
        if (sendingUsage) return;
        sendingUsage = true;
        var succeeded = false;
        try
        {
            if (Settings.InstallId is not { Length: 32 } id || !id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                var previous = Settings.InstallId;
                Settings.InstallId = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
                try { Settings.Save(DataDirectory); }
                catch { Settings.InstallId = previous; throw; }
            }
            UsageStatus = "Отправка анонимной статистики…";
            var network = Status?.Network is not { } n ? "unknown" : n.StartsWith("minter-mainnet", StringComparison.Ordinal) ? "mainnet" : "test";
            using var usage = CreateUsageClient(endpoint);
            await usage.PingAsync(Settings.InstallId, Program.Version, UsageClient.Os, Localization.English ? "en" : "ru", network);
            succeeded = true;
            UsageStatus = $"Гейт подтвердил статистику: {DateTime.Now:HH:mm:ss}.";
            if (Page is MainViewModel main) await main.UsageAsync();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        { UsageStatus = $"Статистика не отправлена: {ScreenModel.Describe(e)}"; }
        finally
        {
            sendingUsage = false;
            if (usageTimer != null) usageTimer.Interval = succeeded ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(30);
        }
    }

    // Coins coming to the wallet's addresses, looked for every 5 seconds while the wallet runs (locked too, once a
    // source is connected; only a new block brings more requests). Raised on the UI thread; the tray turns it into a
    // notification of the system.
    public event Action<Arrival>? Arrived;
    // The window is on the screen (not hidden in the tray or minimized): only then the open page reads every few seconds.
    public bool WindowShown { get; set; } = true;
    // The open page reads by itself every few seconds; off in the checks, which drive every read.
    public bool AutoRefresh { get; set; } = true;
    private Avalonia.Threading.DispatcherTimer? incomeTimer;
    private IncomeWatch? income;
    private bool watching;

    public void StartWatching()
    {
        if (incomeTimer != null) return;
        incomeTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        incomeTimer.Tick += (_, _) => _ = WatchAsync();
        incomeTimer.Start();
    }

    public async Task WatchAsync()
    {
        if (watching || !Settings.NotifyIncoming || Store is not { } store || Backend is not { } backend) return;
        if (income?.Backend != backend) income = new IncomeWatch(backend);
        watching = true;
        try
        {
            IReadOnlyList<Arrival> arrivals;
            using (SourceRequestHandler.EnterBackground())
                arrivals = await income.CheckAsync(store.Accounts);
            foreach (var arrival in arrivals)
                if (arrival.Kind != ArrivalKind.Reward || Settings.NotifyRewards) Arrived?.Invoke(arrival);
            // The open account's balances follow at once.
            if (Page is MainViewModel main && arrivals.Any(a => a.Account.Address == main.Selected?.Account.Address)) main.Refresh.Execute(null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or ChainApiException or System.Text.Json.JsonException or FormatException or KeyNotFoundException or ObjectDisposedException) { }
        finally { watching = false; }
    }

    // The notification's two lines, in the interface's language.
    public static (string Title, string Text) Describe(Arrival a)
    {
        var amount = $"{Display.Amount(a.Amount)} {a.Coin.Symbol}";
        return a.Kind switch
        {
            ArrivalKind.Transfer => (Localization.Text($"Пришло {amount}"), Localization.Text($"На счёт «{a.Account.Name}» от {History.Short(a.From!.Value)}")),
            ArrivalKind.Bridge => (Localization.Text($"Пришло {amount}"), Localization.Text($"На счёт «{a.Account.Name}» через мост Minter Hub")),
            ArrivalKind.Reward => (Localization.Text($"Награда {amount}"), Localization.Text($"Счёт «{a.Account.Name}»: награда за стейкинг")),
            _ => (Localization.Text($"Зачислено {amount}"), Localization.Text($"На счёт «{a.Account.Name}»")),
        };
    }

    public Shell(string? dataDirectory = null)
    {
        DataDirectory = WalletPaths.DataDirectory(dataDirectory);
        Settings = WalletSettings.Load(DataDirectory);
        Credentials = new WalletCredentials(this);
        Coins = new CoinRegistry(Path.Combine(DataDirectory, "coins.json"));
        AddressBook = new AddressBook(DataDirectory);
        var path = Keystore.DefaultPath(DataDirectory);
        if (Keystore.Exists(path)) { Store = Keystore.Load(path); Page = new UnlockViewModel(this); }
        else Page = new WelcomeViewModel(this);
    }

    public string KeystorePath => Keystore.DefaultPath(DataDirectory);

    private MinterLink? pendingPayment;
    public bool OpenPaymentLink(string text)
    {
        if (PaymentLinks.Parse(text) is not { } link) return false;
        if (Page is MainViewModel { Send.SendNow.IsRunning: true }) return false;
        pendingPayment = link;
        ApplyPaymentLink();
        return true;
    }

    private void ApplyPaymentLink()
    {
        if (pendingPayment is not { } link || Page is not MainViewModel main) return;
        pendingPayment = null;
        main.Section = Section.Send;
        main.Send.OpenLink(link);
    }

    public void Show(object next) => Page = next;

    // The views of the current page built again (another language): the page's model and its state stay.
    // Raised after the views are drawn again (a new language): the tray menu follows.
    public event Action? Redrawn;

    public void Redraw()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() => Redrawn?.Invoke());
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
        Credentials.StartOver();
        ShowWelcome();
        return Path.GetFileName(aside);
    }

    public void Opened(Keystore store, KeystoreSession session)
    {
        Store = store; Session = session;
        Page = new MainViewModel(this);
        ApplyPaymentLink();
    }

    public void Lock()
    {
        connectionGeneration++; connecting = null;
        if (Page is MainViewModel main) main.Stop();
        Session?.Lock();
        Session = null; Wallet = null;
        Page = Store == null ? new WelcomeViewModel(this) : new UnlockViewModel(this);
    }

    public void StopTimers()
    {
        usageTimer?.Stop(); usageTimer = null;
        incomeTimer?.Stop(); incomeTimer = null;
        if (Page is MainViewModel main) main.Stop();
    }

    public void Disconnect()
    {
        connectionGeneration++; connecting = null; active = null;
        Backend?.Dispose();
        Backend = null; Wallet = null; Status = null;
    }

    // (Re)connects to the selected source; the network must match the one seen before (or is remembered now).
    // The source in use: the selected one, or another of the same network while the selected one is unreachable.
    public SourceSettings ActiveSource => active ?? Settings.Source;
    private SourceSettings? active;
    private int connectionGeneration;
    private Task<ChainStatus>? connecting;

    public async Task<ChainStatus> ConnectAsync(SourceSettings? fallback = null)
    {
        if (connecting != null) return await connecting;
        var task = ConnectCoreAsync(fallback);
        connecting = task;
        try { return await task; }
        finally { if (ReferenceEquals(connecting, task)) connecting = null; }
    }
    private async Task<ChainStatus> ConnectCoreAsync(SourceSettings? fallback)
    {
        var generation = ++connectionGeneration;
        var selected = Settings.Source;
        Backend?.Dispose();
        Wallet = null; Status = null;
        active = fallback;
        var source = ActiveSource;
        var backend = new NodeApiClient(new Uri(source.Url), source.Name, skipCertificateCheck: source.SkipCertificateCheck);
        Backend = backend;
        void EnsureCurrent()
        {
            if (generation != connectionGeneration || !ReferenceEquals(Backend, backend) || !ReferenceEquals(Settings.Source, selected) || Session?.IsLocked != false)
                throw new OperationCanceledException("Источник или кошелёк изменён во время подключения.");
        }
        var statusTask = backend.StatusAsync();
        var gateTask = backend.GateAsync();
        var coinTask = backend.CoinAsync(0);
        _ = gateTask.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        _ = coinTask.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        var status = await statusTask;
        EnsureCurrent();
        var gate = await gateTask;
        EnsureCurrent();
        var kind = gate != null ? "gate" : "node";
        GateIndexFrom = gate?.IndexFrom ?? 0;
        if (source.Kind != kind) { source.Kind = kind; Settings.Save(DataDirectory); }
        if (source.ExpectedNetwork == null) { source.ExpectedNetwork = status.Network; Settings.Save(DataDirectory); }
        else if (source.ExpectedNetwork != status.Network)
        {
            Wallet = null; Status = null;
            throw new InvalidOperationException($"Источник «{source.Name}» теперь сообщает сеть {status.Network}, а раньше была {source.ExpectedNetwork}. Отправка с него отключена; проверьте адрес или подтвердите новую сеть в настройках.");
        }
        var baseCoin = await coinTask;
        EnsureCurrent();
        if (baseCoin != null) Coins.Observe(new CoinRef(0, baseCoin.Symbol));
        Status = status;
        Wallet = new WalletService(backend, Session ?? throw new InvalidOperationException("Кошелёк заблокирован."), WalletService.ChainIdOf(status.Network),
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
