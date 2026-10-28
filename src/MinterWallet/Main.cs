using System.Collections.ObjectModel;
using System.Globalization;
using System.Numerics;
using System.Text;
using Avalonia.Threading;
using Minter.Protocol;
using MinterWallet.Api;
using MinterWallet.Core;

namespace MinterWallet;

public enum Section { Overview, Send, Swap, Staking, Tokens, Multisig, History, Accounts, Settings }

public sealed record AccountItem(WalletAccount Account, string Kind, string Glyph)
{
    public string Name => Account.Name;
    public string Short => History.Short(Account.Address);
    public string Caption => $"{Short} · {Kind}";
    public string AddressText => Display.Address(Account.Address);
    public override string ToString() => $"{Name} · {Short}";
}

public sealed record BalanceItem(CoinRef Coin, BigInteger Value, BigInteger BipValue, string BaseSymbol, string Usd = "")
{
    public string Symbol => Coin.Symbol;
    public string Id => Coin.Id.ToString(CultureInfo.InvariantCulture);
    public string IdText => $"id {Id}";
    public string Amount => Units.FormatCoins(Value);
    public string AmountShort => Display.Amount(Value);
    public string Bip => Units.FormatCoins(BipValue);
    // A token with no pool to the base coin has no estimate (the source says 0): said so rather than "≈ 0 · $0".
    public string BipText => Coin.Id == 0 ? Usd.Length > 0 ? $"≈ {Usd}" : ""
        : BipValue.IsZero && !Value.IsZero ? $"нет оценки в {BaseSymbol}"
        : $"≈ {Display.Amount(BipValue)} {BaseSymbol}{(Usd.Length > 0 ? $" · {Usd}" : "")}";
    public string? BipTip => Coin.Id == 0 || BipValue.IsZero ? null : $"≈ {Bip} {BaseSymbol}";
    public string Initial => Symbol.Length > 0 ? Symbol[..1] : "?";
    public override string ToString() => $"{Symbol} (id {Id}) — {Amount}";
}

public sealed record DelegationItem(Delegation Delegation, string BaseSymbol)
{
    public string Symbol => Delegation.Coin.Symbol;
    public string Initial => Symbol.Length > 0 ? Symbol[..1] : "?";
    public string Amount => Units.FormatCoins(Delegation.Value);
    public string AmountShort => Display.Amount(Delegation.Value);
    public string BipText => $"≈ {Display.Amount(Delegation.BipValue)} {BaseSymbol}";
    public string BipTip => $"≈ {Units.FormatCoins(Delegation.BipValue)} {BaseSymbol}";
}

public sealed record FrozenItem(string Amount, string AmountShort, string Caption);

public sealed record ContactItem(AddressBookEntry Entry)
{
    public string Name => Entry.Name.Length > 0 ? Entry.Name : "Без имени";
    public string Address => Entry.Address;
    public string AddressText => Minter.Protocol.Address.TryParse(Entry.Address, out var a) ? Display.Address(a) : Entry.Address;
    public string Initial => Entry.Name.Length > 0 ? Entry.Name[..1].ToUpperInvariant() : "?";
}

public sealed class MainViewModel : ScreenModel
{
    private readonly Shell shell;
    private readonly DispatcherTimer autoLock;
    private AccountItem? selected;
    private Section section;
    private string sourceName, networkText = "подключение…", blockText = "", updated = "", totalText = "—", totalCaption = "", totalUsd = "", copyLabel = "Копировать", updateText = "";
    private bool connected, showReceive;
    private Uri? updatePage;
    private AccountState? state;
    private IReadOnlyList<CoinListing>? catalog;
    private Dictionary<uint, string> catalogSymbols = [];
    private object? catalogFrom;

    public MainViewModel(Shell shell)
    {
        this.shell = shell;
        sourceName = shell.Settings.Source.Name;
        Refresh = new AsyncCommand(RefreshAsync, e => { Connected = false; NetworkText = "нет связи"; BlockText = ""; Fail(e); if (e is HttpRequestException or TaskCanceledException) _ = FallbackAsync(); });
        LockNow = new Command(shell.Lock);
        CopyAddress = new AsyncCommand(CopyAddressAsync, Fail);
        OpenSend = new Command(() => Section = Section.Send);
        OpenSwap = new Command(() => Section = Section.Swap);
        OpenStaking = new Command(() => Section = Section.Staking);
        AddAddress = new Command(() => { AccountsPage!.StartAdding(); Section = Section.Accounts; });
        ToggleReceive = new Command(() => ShowReceive = !ShowReceive);
        OpenUpdate = new AsyncCommand(async () => { if (updatePage != null && shell.OpenUrl != null) await shell.OpenUrl(updatePage); }, Fail);
        RemoveContact = new RelayCommand(item => { if (item is ContactItem c && Minter.Protocol.Address.TryParse(c.Address, out var a)) { shell.AddressBook.Remove(a); LoadContacts(); } });
        Send = new SendViewModel(this);
        Request = new PaymentRequestViewModel(this);
        Multisend = new MultisendViewModel(this);
        Swap = new SwapViewModel(this);
        Staking = new StakingViewModel(this);
        Tokens = new TokensViewModel(this);
        Multisig = new MultisigViewModel(this);
        Validator = new ValidatorViewModel(this);
        History = new HistoryViewModel(this);
        AccountsPage = new AccountsViewModel(this);
        Settings = new SettingsViewModel(this);
        IssueCheck = new CheckIssueViewModel(this);
        Liquidity = new LiquidityViewModel(this);
        Orders = new OrdersViewModel(this);
        RedeemCheck = new CheckRedeemViewModel(this);
        LoadAccounts(shell.Settings.LastAccountId);
        autoLock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        autoLock.Tick += (_, _) => { if (DateTime.UtcNow - LastActivity > TimeSpan.FromMinutes(Math.Max(1, shell.Settings.AutoLockMinutes))) { autoLock.Stop(); shell.Lock(); } };
        autoLock.Start();
        LoadContacts();
        Refresh.Execute(null);
        if (shell.Settings.CheckUpdates) _ = CheckUpdatesAsync();
    }

    public ObservableCollection<ContactItem> Contacts { get; } = [];
    public bool HasContacts => Contacts.Count > 0;
    public bool ShowReceive { get => showReceive; set => Set(ref showReceive, value); }
    public string TotalUsd { get => totalUsd; set { Set(ref totalUsd, value); Changed(nameof(HasUsd)); } }
    public bool HasUsd => TotalUsd.Length > 0;
    public string UpdateText { get => updateText; set { Set(ref updateText, value); Changed(nameof(HasUpdate)); } }
    public bool HasUpdate => UpdateText.Length > 0;
    public Command ToggleReceive { get; }
    public AsyncCommand OpenUpdate { get; }
    public RelayCommand RemoveContact { get; }

    // Recipients the user has paid or saved, most recent first.
    public void LoadContacts()
    {
        Contacts.Clear();
        foreach (var e in shell.AddressBook.Entries.OrderByDescending(e => e.LastUsed)) Contacts.Add(new ContactItem(e));
        Changed(nameof(HasContacts));
    }

    private async Task CheckUpdatesAsync()
    {
        try
        {
            using var updates = new UpdateClient();
            if (await updates.LatestAsync() is { } latest && UpdateClient.IsNewer(latest.Version, Version.Parse(Program.Version)))
            {
                updatePage = latest.Page;
                UpdateText = $"Вышла версия {latest.Version.ToString(3)}. Сейчас у вас {Program.Version}.";
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException) { }
    }

    // Dollars per base coin from the mainnet pool with USDTE (coin 1993), in pip; null elsewhere or without the pool.
    private async Task<BigInteger?> UsdPerCoinAsync(ChainStatus s)
    {
        if (!s.Network.StartsWith("minter-mainnet", StringComparison.Ordinal) || shell.Backend == null) return null;
        try { return await shell.Backend.SwapPoolAsync(0, 1993) is ({ Sign: > 0 } bip, var usd) ? usd * Units.PipPerCoin / bip : null; }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or ChainApiException) { return null; }
    }

    public Shell Shell => shell;
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;
    public ObservableCollection<AccountItem> Accounts { get; } = [];
    public ObservableCollection<BalanceItem> Balances { get; } = [];
    public ObservableCollection<DelegationItem> Delegations { get; } = [];
    public ObservableCollection<FrozenItem> Frozen { get; } = [];

    public Section Section
    {
        get => section;
        set
        {
            if (!Set(ref section, value)) return;
            foreach (var name in new[] { nameof(SectionIndex), nameof(IsOverview), nameof(IsSend), nameof(IsSwap), nameof(IsStaking), nameof(IsTokens), nameof(IsMultisig), nameof(IsHistory), nameof(IsAccounts), nameof(IsSettings) }) Changed(name);
            if (value == Section.History && History.Items.Count == 0) History.Load.Execute(null);
            if (value == Section.Tokens && !Tokens.HasMine) Tokens.Load.Execute(null);
        }
    }
    public int SectionIndex { get => (int)section; set { if (value >= 0) Section = (Section)value; } }
    public bool IsOverview => section == Section.Overview;
    public bool IsSend => section == Section.Send;
    public bool IsSwap => section == Section.Swap;
    public bool IsStaking => section == Section.Staking;
    public bool IsTokens => section == Section.Tokens;
    public bool IsMultisig => section == Section.Multisig;
    public bool IsHistory => section == Section.History;
    public bool IsAccounts => section == Section.Accounts;
    public bool IsSettings => section == Section.Settings;

    public AccountItem? Selected
    {
        get => selected;
        set
        {
            if (!Set(ref selected, value) || value == null) return;
            foreach (var name in new[] { nameof(Address), nameof(AddressText), nameof(CanSign), nameof(IsWatchOnly), nameof(AccountName) }) Changed(name);
            Request?.Update();
            shell.Settings.LastAccountId = value.Account.Id; shell.Settings.Save(shell.DataDirectory);
            History.Reset(); Tokens?.Reset(); Multisig?.AccountChanged(); Validator?.Reset();
            AccountsPage.Reload();
            Refresh.Execute(null);
        }
    }
    public string Address => Selected?.Account.Address.ToString() ?? "";
    public string AddressText => Selected == null ? "" : Display.Address(Selected.Account.Address);
    public string AccountName => Selected?.Name ?? "";
    public bool CanSign => Selected is { Account.WatchOnly: false };
    public bool IsWatchOnly => Selected is { Account.WatchOnly: true };
    public string BaseSymbol => Symbol(0);
    public string SourceName { get => sourceName; set => Set(ref sourceName, value); }
    public string NetworkText { get => networkText; set => Set(ref networkText, value); }
    public string BlockText { get => blockText; set => Set(ref blockText, value); }
    public string SourceTip => $"{shell.ActiveSource.Url}\nОтветы источника кошелёк не проверяет: выбирайте тот, которому доверяете.{(updated.Length > 0 ? $"\nОбновлено {updated}." : "")}";
    public bool Connected { get => connected; set { Set(ref connected, value); Changed(nameof(CertificateUnchecked)); } }
    public bool CertificateUnchecked => shell.ActiveSource.SkipCertificateCheck && shell.ActiveSource.Url.StartsWith("https", StringComparison.OrdinalIgnoreCase);
    public string TotalText { get => totalText; set => Set(ref totalText, value); }
    public string TotalCaption { get => totalCaption; set => Set(ref totalCaption, value); }
    public string CopyLabel { get => copyLabel; set => Set(ref copyLabel, value); }
    public bool HasBalances => Balances.Count > 0;
    public bool HasNoBalances => state != null && Balances.Count == 0;
    public bool HasDelegations => Delegations.Count > 0;
    public bool HasFrozen => Frozen.Count > 0;
    public AccountState? State => state;

    // The contact or own account an unknown address only looks like (AddressBook.LooksLike), as «name» Mx1234…abcd.
    public string? LookalikeOf(Address to)
    {
        if (shell.AddressBook.IsKnown(to) || Accounts.Any(a => a.Account.Address == to)) return null;
        foreach (var a in Accounts)
            if (AddressBook.LooksLike(to, a.Account.Address)) return $"«{a.Name}» {Core.History.Short(a.Account.Address)}";
        foreach (var e in shell.AddressBook.Entries)
            if (Minter.Protocol.Address.TryParse(e.Address, out var known) && AddressBook.LooksLike(to, known))
                return e.Name.Length > 0 ? $"«{e.Name}» {Core.History.Short(known)}" : Core.History.Short(known);
        return null;
    }

    public static string LookalikeWarning(string like) =>
        $"Адрес только похож на {like}: совпадают начало и конец, середина другая. Так выглядит подмена — мошенники присылают такие адреса мелкими переводами, чтобы их скопировали из истории. Проверьте, откуда вы взяли адрес.";
    public string? TotalExact => state == null ? null : $"{Units.FormatCoins(state.TotalBipValue)} {BaseSymbol}";

    public AsyncCommand Refresh { get; }
    public Command LockNow { get; }
    public AsyncCommand CopyAddress { get; }
    public Command OpenSend { get; }
    public Command OpenSwap { get; }
    public Command OpenStaking { get; }
    public Command AddAddress { get; }
    public AccountsViewModel AccountsPage { get; }
    public CheckIssueViewModel IssueCheck { get; }
    public LiquidityViewModel Liquidity { get; }
    public OrdersViewModel Orders { get; }
    // The trade page: swap now, a limit order, or liquidity.
    public int TradeModeIndex
    {
        get => tradeMode;
        set
        {
            if (value < 0 || !Set(ref tradeMode, value)) return;
            Changed(nameof(IsSwapNow)); Changed(nameof(IsOrders)); Changed(nameof(IsLiquidity));
            if (value == 1) Orders.Load.Execute(null);
            if (value == 2) Liquidity.Load.Execute(null);
        }
    }
    private int tradeMode;
    public bool IsSwapNow => tradeMode == 0;
    public bool IsOrders => tradeMode == 1;
    public bool IsLiquidity => tradeMode == 2;
    public CheckRedeemViewModel RedeemCheck { get; }
    public SendViewModel Send { get; }
    public PaymentRequestViewModel Request { get; }
    public MultisendViewModel Multisend { get; }
    public SwapViewModel Swap { get; }
    public StakingViewModel Staking { get; }
    public TokensViewModel Tokens { get; }
    public MultisigViewModel Multisig { get; }
    public ValidatorViewModel Validator { get; }
    public HistoryViewModel History { get; }
    public SettingsViewModel Settings { get; }

    public void LoadAccounts(string? select = null)
    {
        Accounts.Clear();
        foreach (var a in shell.Store!.Accounts)
            Accounts.Add(a.WatchOnly ? new AccountItem(a, "просмотр", "eye") : shell.Store.KindOf(a) != SecretKind.Mnemonic ? new AccountItem(a, "ключ", "key")
                : new AccountItem(a, a.Index == 0 ? "фраза" : $"фраза №{a.Index + 1}", "phrase"));
        Selected = Accounts.FirstOrDefault(a => a.Account.Id == select) ?? Accounts.FirstOrDefault();
        AccountsPage.Reload();
    }

    // Another source was chosen: everything read from the old one is dropped.
    public void Disconnect()
    {
        shell.Disconnect();
        catalog = null; catalogFrom = null; catalogSymbols = [];
        Connected = false; SourceName = shell.Settings.Source.Name; NetworkText = "подключение…"; BlockText = "";
        Staking.Reset(); History.Reset(); Tokens.Reset(); Swap.Buy.Selected = null; Liquidity.Reset(); Orders.Reset();
    }

    // Coins to offer for a swap: the source's list when it is a MinterGate, otherwise the coins on the account.
    public bool HasCatalog => catalog != null;

    public IReadOnlyList<CoinOption> CoinOptions(uint? exclude)
    {
        var owned = Balances.GroupBy(b => b.Coin.Id).ToDictionary(g => g.Key, g => g.First());
        string Note(uint id) => owned.TryGetValue(id, out var b) ? $"у вас {b.AmountShort}" : "";
        var list = catalog?.Select(c => new CoinOption(c.Id, c.Symbol, c.Name, c.Liquidity, Note(c.Id)))
            ?? Balances.Select(b => new CoinOption(b.Coin.Id, b.Symbol, "", b.BipValue, Note(b.Coin.Id)));
        return list.Where(c => c.Id != exclude).ToList();
    }

    // The source's history; on mainnet, behind a gate that started its history at some height, the older part from
    // the explorer (when the user keeps that on).
    public HistoryCursor HistoryCursor(WalletService wallet, Address me, int perPage)
    {
        Task<IReadOnlyList<TxRecord>> Source(int page) => wallet.Backend.HistoryAsync(me, page, perPage);
        var useExplorer = shell.Settings.HistoryFromExplorer && shell.GateIndexFrom > 1 && shell.Status?.Network.StartsWith("minter-mainnet", StringComparison.Ordinal) == true;
        return useExplorer ? new HistoryCursor(Source, perPage, page => shell.Explorer.HistoryAsync(me, page), shell.GateIndexFrom) : new HistoryCursor(Source, perPage);
    }

    public async Task<CoinOption?> LookupCoinAsync(string text)
    {
        var wallet = await WalletAsync();
        var coin = uint.TryParse(text, out var id) ? await wallet.Backend.CoinAsync(id) : await wallet.Backend.CoinAsync(text.ToUpperInvariant());
        return coin == null ? null : new CoinOption(coin.Id, coin.Symbol, coin.Name, coin.Reserve, "");
    }

    private async Task LoadCatalogAsync()
    {
        if (shell.Backend == null || ReferenceEquals(catalogFrom, shell.Backend)) return;
        catalogFrom = shell.Backend;
        try { catalog = await shell.Backend.CoinListAsync(); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or ChainApiException or System.Text.Json.JsonException) { catalog = null; }
        catalogSymbols = catalog?.ToDictionary(c => c.Id, c => c.Symbol) ?? [];
        Swap.Buy.Refresh();
    }

    // Tickers the wallet has seen first, then the gate's coin list: history never shows a bare id when the source knows the coin.
    public string Symbol(uint coin) => shell.Coins.Symbol(coin) is ['#', ..] unknown ? catalogSymbols.GetValueOrDefault(coin, unknown) : shell.Coins.Symbol(coin);

    public async Task<WalletService> WalletAsync()
    {
        if (shell.Wallet != null) return shell.Wallet;
        var s = await shell.ConnectAsync();
        ShowSource(s);
        return shell.Wallet!;
    }

    // The selected source does not answer: another one of the same network, for this session only. The selected one
    // stays selected and is tried again at the next start or "Переподключиться".
    private bool fallingBack;
    private async Task FallbackAsync()
    {
        if (fallingBack || shell.ActiveSource != shell.Settings.Source) return;
        fallingBack = true;
        try
        {
            var failed = shell.Settings.Source;
            foreach (var other in shell.Settings.Sources.Where(x => x != failed && x.ExpectedNetwork != null && x.ExpectedNetwork == failed.ExpectedNetwork))
            {
                try { await shell.ConnectAsync(other); }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or ChainApiException) { continue; }
                Say($"«{failed.Name}» не отвечает — пока подключились к «{other.Name}». Вернуться можно в настройках: «Переподключиться».", Tone.Warning);
                Refresh.Execute(null);
                return;
            }
            shell.Disconnect();
        }
        finally { fallingBack = false; }
    }

    private void ShowSource(ChainStatus s)
    {
        SourceName = shell.ActiveSource.Name;
        NetworkText = s.Network;
        BlockText = $"блок {Display.Integer(s.Height)}";
        Connected = true;
        updated = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        Changed(nameof(SourceTip));
    }

    private async Task CopyAddressAsync()
    {
        if (Selected == null || shell.CopyText == null) return;
        await shell.CopyText(Selected.Account.Address.ToString());
        CopyLabel = "Скопировано";
        DispatcherTimer.RunOnce(() => CopyLabel = "Копировать", TimeSpan.FromSeconds(2));
    }

    private async Task RefreshAsync()
    {
        var wallet = await WalletAsync();
        var s = await wallet.Backend.StatusAsync();
        ShowSource(s);
        _ = LoadCatalogAsync();
        if (Selected == null) return;
        state = await wallet.Backend.AccountAsync(Selected.Account.Address);
        Changed(nameof(State));
        var conflicts = new List<string>();
        var usdPerCoin = await UsdPerCoinAsync(s);
        string Usd(BigInteger bip) => usdPerCoin is { } p ? Display.Usd(bip * p / Units.PipPerCoin) : "";
        Balances.Clear();
        foreach (var b in state.Balances)
        {
            if (shell.Coins.Observe(b.Coin) is { } c) conflicts.Add($"{c.Symbol}: раньше id {c.KnownId}, теперь id {c.NewId}");
            Balances.Add(new BalanceItem(b.Coin, b.Value, b.BipValue, BaseSymbol, Usd(b.BipValue)));
        }
        TotalUsd = usdPerCoin == null ? "" : "≈ " + Usd(state.TotalBipValue);
        Delegations.Clear();
        foreach (var d in state.Delegated) Delegations.Add(new DelegationItem(d, BaseSymbol));
        TotalText = Display.Amount(state.TotalBipValue);
        Changed(nameof(TotalExact));
        TotalCaption = $"≈ всего в {BaseSymbol} по оценке источника · отправлено транзакций: {Display.Integer((long)state.Nonce)}";
        Changed(nameof(BaseSymbol));
        var frozenFunds = await wallet.Backend.FrozenAsync(Selected.Account.Address);
        Frozen.Clear();
        foreach (var f in frozenFunds)
            Frozen.Add(new FrozenItem($"{Units.FormatCoins(f.Value)} {f.Coin.Symbol}", $"{Display.Amount(f.Value)} {f.Coin.Symbol}",
                $"до блока {Display.Integer(f.UnlockHeight)}, ≈ {EstimateDate(s, f.UnlockHeight)}{(f.Candidate is { } k ? $" · из стейка у {Core.History.Short(k)}" : "")}"));
        foreach (var name in new[] { nameof(HasBalances), nameof(HasNoBalances), nameof(HasDelegations), nameof(HasFrozen) }) Changed(name);
        if (conflicts.Count > 0) Say("Тикер указывает на другой id монеты, чем раньше: " + string.Join("; ", conflicts) + ". Проверьте монету перед отправкой.", Tone.Warning);
        else if (Tone == Tone.Error) Say("");
        Send.BalancesChanged(); Request.BalancesChanged(); Swap.BalancesChanged(); Staking.BalancesChanged(); Validator.BalancesChanged(); Tokens.BalancesChanged(); IssueCheck.BalancesChanged(); Liquidity.BalancesChanged(); Orders.BalancesChanged();
    }

    private static string EstimateDate(ChainStatus s, long height) => s.BlockTime.AddSeconds((height - s.Height) * 5).ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);

    // Preview through the source, confirmation with the password, then the per-sender queue. Every value shown in the
    // confirmation comes from the data being signed, not from the source. Progress goes to the screen that asked.
    public async Task<TxOutcome?> ConfirmAndSendAsync(string title, ITxData data, uint gasCoin, byte[] payload, IEnumerable<(string, string)> rows,
        IEnumerable<string> warnings, ScreenModel screen)
    {
        var account = Selected?.Account ?? throw new InvalidOperationException("Выберите счёт.");
        if (account.WatchOnly) throw new InvalidOperationException("Этот счёт только для просмотра: подписать нечем.");
        var wallet = await WalletAsync();
        screen.Say("Расчёт комиссии…", Tone.Progress);
        var preview = await wallet.PreviewAsync(account, data, gasCoin, payload);
        var allRows = new List<(string, string)> { ("Операция", Core.History.Describe(data, account.Address, account.Address, Symbol)), ("Со счёта", $"{account.Name}\n{Display.Address(account.Address)}") };
        allRows.AddRange(rows);
        allRows.Add(("Комиссия", $"{Units.FormatCoins(preview.Commission)} {preview.CommissionCoin.Symbol} (id {preview.CommissionCoin.Id}), gas price {preview.GasPrice}"));
        if (payload.Length > 0) allRows.Add(("Сообщение", Encoding.UTF8.GetString(payload)));
        allRows.Add(("Сеть", $"{shell.Status?.Network} (chain ID {wallet.ChainId}) · {wallet.Backend.Name}"));
        var allWarnings = warnings.ToList();
        if (preview.GasPrice > 1) allWarnings.Add($"Сеть загружена: gas price {preview.GasPrice}, комиссия умножена на него.");
        var password = shell.Confirm == null ? null : await shell.Confirm(new ConfirmRequest(title, allRows, allWarnings));
        if (password == null) { screen.Say("Отменено — ничего не отправлено.", Tone.Info); return null; }
        screen.Say("Проверка пароля…", Tone.Progress);
        if (!await shell.CheckPasswordAsync(password)) { screen.Say("Неверный пароль — ничего не отправлено.", Tone.Error); return null; }
        screen.Say("Подпись и отправка…", Tone.Progress);
        var outcome = await wallet.SendAsync(account, preview, new Progress<TxOutcome>(o => screen.Say($"Отправлено {o.Hash}, ждём блок…", Tone.Progress)));
        switch (outcome.State)
        {
            case TxState.Included: screen.Say($"Готово: блок {Display.Integer(outcome.Height)}, {outcome.Hash}", Tone.Success); break;
            case TxState.FailedInBlock: screen.Say($"Транзакция попала в блок {Display.Integer(outcome.Height)}, но не выполнена (код {outcome.Code}): {outcome.Log}. Комиссия списана.", Tone.Error); break;
            case TxState.Rejected: screen.Say($"Сеть отклонила (код {outcome.Code}): {outcome.Log}. Ничего не списано.", Tone.Error); break;
            default: screen.Say(outcome.Log, Tone.Warning); break;
        }
        Refresh.Execute(null);
        return outcome;
    }

    public static byte[] Utf8(string text) => string.IsNullOrEmpty(text) ? [] : Encoding.UTF8.GetBytes(text);

    // Amount fields: a clear message instead of a parser exception.
    public static bool TryAmount(string text, ScreenModel screen, out BigInteger value)
    {
        value = default;
        try { value = Units.ParseCoins(text); }
        catch (FormatException) { screen.Say("Сумма — число с точкой или запятой, не больше 18 знаков после неё.", Tone.Error); return false; }
        if (value.Sign > 0) return true;
        screen.Say("Сумма должна быть больше нуля.", Tone.Error);
        return false;
    }
}

public sealed class SendViewModel : ScreenModel
{
    private readonly MainViewModel main;
    private string recipient = "", amount = "", payload = "", recipientName = "";
    private BalanceItem? coin, gasCoin;
    private int modeIndex;

    public SendViewModel(MainViewModel main)
    {
        this.main = main;
        SendNow = new AsyncCommand(SendAsync, Fail);
        All = new AsyncCommand(AllAsync, Fail);
        Paste = new AsyncCommand(PasteAsync, Fail);
        PickContact = new RelayCommand(item => { if (item is ContactItem c) Recipient = c.Address; });
        main.Contacts.CollectionChanged += (_, _) => Changed(nameof(HasContacts));
    }

    public bool HasContacts => main.Contacts.Count > 0;

    public ObservableCollection<BalanceItem> Coins { get; } = [];
    public ObservableCollection<ContactItem> Contacts => main.Contacts;
    public RelayCommand PickContact { get; }
    public string RecipientName { get => recipientName; set => Set(ref recipientName, value); }
    public bool IsNewRecipient => Minter.Protocol.Address.TryParse(Recipient.Trim(), out var a) && !main.Shell.AddressBook.IsKnown(a);
    public int ModeIndex { get => modeIndex; set { if (value >= 0 && Set(ref modeIndex, value)) { Changed(nameof(Multiple)); Changed(nameof(Single)); Changed(nameof(ByCheck)); } } }
    public bool Multiple { get => modeIndex == 1; set => ModeIndex = value ? 1 : 0; }
    public bool Single => modeIndex == 0;
    public bool ByCheck { get => modeIndex == 2; set => ModeIndex = value ? 2 : 0; }
    public string Recipient
    {
        get => recipient;
        set
        {
            Set(ref recipient, value); Changed(nameof(RecipientCheck)); Changed(nameof(RecipientTone)); Changed(nameof(IsNewRecipient));
            if (MinterLink.Parse(value) is { } link) Dispatcher.UIThread.Post(() => OpenLink(link));
        }
    }
    public BalanceItem? Coin { get => coin; set { Set(ref coin, value); Changed(nameof(Available)); } }
    public BalanceItem? GasCoin { get => gasCoin; set => Set(ref gasCoin, value); }
    public string Amount { get => amount; set => Set(ref amount, value); }
    public string Payload { get => payload; set => Set(ref payload, value); }
    public string Available => Coin == null ? "" : $"Доступно: {Coin.AmountShort} {Coin.Symbol}";
    public AsyncCommand SendNow { get; }
    public AsyncCommand All { get; }
    public AsyncCommand Paste { get; }

    public string RecipientCheck => !Minter.Protocol.Address.TryParse(Recipient.Trim(), out var a) ? (Recipient.Length == 0 ? "" : "Адрес — Mx и 40 шестнадцатеричных символов.")
        : main.LookalikeOf(a) is { } like ? MainViewModel.LookalikeWarning(like)
        : main.Shell.AddressBook.NameOf(a) is { Length: > 0 } n ? $"Из адресной книги: {n}" : main.Shell.AddressBook.IsKnown(a) ? "Вы уже отправляли на этот адрес."
        : "Новый получатель: в подтверждении сверьте адрес целиком, а не только выделенные начало и конец.";
    public Tone RecipientTone => !Minter.Protocol.Address.TryParse(Recipient.Trim(), out var a) ? (Recipient.Length == 0 ? Tone.None : Tone.Error)
        : main.LookalikeOf(a) != null ? Tone.Error : main.Shell.AddressBook.IsKnown(a) ? Tone.Success : Tone.Warning;

    public void BalancesChanged()
    {
        var keep = Coin?.Coin.Id; var keepGas = GasCoin?.Coin.Id;
        Coins.Clear();
        foreach (var b in main.Balances) Coins.Add(b);
        Coin = Coins.FirstOrDefault(c => c.Coin.Id == keep) ?? Coins.FirstOrDefault();
        GasCoin = Coins.FirstOrDefault(c => c.Coin.Id == keepGas) ?? Coins.FirstOrDefault(c => c.Coin.Id == 0) ?? Coins.FirstOrDefault();
    }

    private async Task PasteAsync()
    {
        var text = main.Shell.PasteText == null ? null : await main.Shell.PasteText();
        if (string.IsNullOrWhiteSpace(text)) { Say("В буфере обмена нет текста.", Tone.Warning); return; }
        Recipient = text.Trim();
    }

    // A Minter Link (bip.to): a transfer fills the form to be checked and sent; a check goes to the receive panel.
    public void OpenLink(MinterLink link)
    {
        if (link.Type == TxType.Send && link.DecodeData() is SendData send)
        {
            ModeIndex = 0;
            Recipient = send.To.ToString();
            Coin = Coins.FirstOrDefault(c => c.Coin.Id == send.Coin);
            Amount = Units.FormatCoins(send.Value);
            Payload = Encoding.UTF8.GetString(link.Payload);
            if (link.GasCoin is { } g && Coins.FirstOrDefault(c => c.Coin.Id == g) is { } gas) GasCoin = gas;
            if (Coin == null) Say($"По ссылке просят {Units.FormatCoins(send.Value)} {main.Symbol(send.Coin)} (id {send.Coin}), а этой монеты на счёте нет.", Tone.Warning);
            else Say($"Заполнено по ссылке: {Units.FormatCoins(send.Value)} {Coin.Symbol} на {Core.History.Short(send.To)}. Проверьте и отправьте.", Tone.Info);
        }
        else if (link.Type == TxType.RedeemCheck)
        {
            Recipient = "";
            main.RedeemCheck.Text = link.ToString();
            main.Section = Section.Overview;
            main.ShowReceive = true;
        }
        else
        {
            Recipient = "";
            Say($"Ссылка на операцию другого типа ({(int)link.Type}): по ссылкам кошелёк открывает переводы и чеки.", Tone.Warning);
        }
    }

    // Everything of the coin; when the commission is paid in the same coin it is subtracted first.
    private async Task AllAsync()
    {
        if (Coin == null) { Say("Выберите монету.", Tone.Warning); return; }
        var gas = GasCoin?.Coin.Id ?? 0;
        var value = Coin.Value;
        if (gas == Coin.Coin.Id)
        {
            var to = Minter.Protocol.Address.TryParse(Recipient.Trim(), out var a) ? a : main.Selected!.Account.Address;
            var wallet = await main.WalletAsync();
            var preview = await wallet.PreviewAsync(main.Selected!.Account, new SendData(Coin.Coin.Id, to, value), gas, MainViewModel.Utf8(Payload));
            value -= preview.Commission;
            if (value.Sign <= 0) { Say("Баланса не хватает даже на комиссию.", Tone.Warning); return; }
        }
        Amount = Units.FormatCoins(value);
    }

    private async Task SendAsync()
    {
        if (!Minter.Protocol.Address.TryParse(Recipient.Trim(), out var to)) { Say("Укажите адрес получателя: Mx и 40 шестнадцатеричных символов.", Tone.Error); return; }
        if (Coin == null) { Say("Выберите монету.", Tone.Error); return; }
        if (!MainViewModel.TryAmount(Amount, this, out var value)) return;
        var data = new SendData(Coin.Coin.Id, to, value);
        var warnings = new List<string>();
        if (main.LookalikeOf(to) is { } like) warnings.Add(MainViewModel.LookalikeWarning(like));
        if (!main.Shell.AddressBook.IsKnown(to)) warnings.Add("Новый получатель. У адресов Minter нет контрольной суммы: опечатка или подмена в буфере обмена дадут другой действительный адрес. Сверьте адрес целиком: мошенники подбирают адреса с теми же началом и концом.");
        if (to == main.Selected!.Account.Address) warnings.Add("Перевод самому себе.");
        var outcome = await main.ConfirmAndSendAsync("Перевод", data, GasCoin?.Coin.Id ?? 0, MainViewModel.Utf8(Payload),
            [("Получатель", Display.Address(to)), ("Сумма", $"{Units.FormatCoins(value)} {Coin.Coin.Symbol} (id {Coin.Coin.Id})")], warnings, this);
        if (outcome?.Succeeded == true)
        {
            main.Shell.AddressBook.Remember(to, RecipientName.Trim());
            main.LoadContacts();
            Amount = ""; Payload = ""; RecipientName = "";
            Changed(nameof(RecipientCheck)); Changed(nameof(RecipientTone)); Changed(nameof(IsNewRecipient));
        }
    }
}

public sealed class MultisendViewModel : ScreenModel
{
    private readonly MainViewModel main;
    private string lines = "";

    public MultisendViewModel(MainViewModel main)
    {
        this.main = main;
        SendNow = new AsyncCommand(SendAsync, Fail);
        Paste = new AsyncCommand(PasteAsync, Fail);
    }

    public string Lines { get => lines; set { Set(ref lines, value); Changed(nameof(Summary)); Changed(nameof(SummaryTone)); } }
    public AsyncCommand SendNow { get; }
    public AsyncCommand Paste { get; }

    // Read while typing: how many recipients and how much of each coin, or the first line that does not parse.
    public string Summary => Parse(out var items) is { } problem ? problem
        : items.Count == 0 ? "" : $"Получателей: {items.Count} · " + string.Join(", ", items.GroupBy(i => i.Coin).Select(g => $"{Display.Amount(g.Aggregate(BigInteger.Zero, (s, i) => s + i.Value))} {main.Symbol(g.Key)}"));
    public Tone SummaryTone => Lines.Trim().Length == 0 ? Tone.None : Parse(out _) == null ? Tone.Success : Tone.Warning;

    // "Mx… сумма [ТИКЕР]" per line; spaces, tabs or semicolons between fields; the ticker must be one of the balances.
    private string? Parse(out List<MultisendItem> items)
    {
        items = [];
        var number = 0;
        foreach (var raw in Lines.Split('\n'))
        {
            number++;
            var parts = raw.Split([' ', '\t', ';'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;
            if (parts.Length is < 2 or > 3 || !Minter.Protocol.Address.TryParse(parts[0], out var to)) return $"Строка {number}: нужно «Mx… сумма [монета]».";
            var symbol = parts.Length == 3 ? parts[2].ToUpperInvariant() : main.Symbol(0);
            var balance = main.Balances.FirstOrDefault(b => b.Coin.Symbol == symbol);
            if (balance == null) return $"Строка {number}: монеты {symbol} нет на счёте.";
            BigInteger value;
            try { value = Units.ParseCoins(parts[1]); } catch (FormatException) { return $"Строка {number}: сумма «{parts[1]}» — не число."; }
            if (value.Sign <= 0) return $"Строка {number}: сумма должна быть больше нуля.";
            items.Add(new MultisendItem(balance.Coin.Id, to, value));
        }
        return items.Count > 100 ? "Не больше 100 получателей в одной транзакции." : null;
    }

    private async Task PasteAsync()
    {
        var text = main.Shell.PasteText == null ? null : await main.Shell.PasteText();
        if (string.IsNullOrWhiteSpace(text)) { Say("В буфере обмена нет текста.", Tone.Warning); return; }
        Lines = (Lines.Length > 0 && !Lines.EndsWith('\n') ? Lines + "\n" : Lines) + text.Replace("\r\n", "\n").Trim();
    }

    private async Task SendAsync()
    {
        if (Parse(out var items) is { } problem) { Say(problem, Tone.Error); return; }
        if (items.Count == 0) { Say("Добавьте хотя бы одного получателя.", Tone.Error); return; }
        var warnings = items.Select(i => main.LookalikeOf(i.To) is { } like ? $"{Display.Address(i.To)}: {MainViewModel.LookalikeWarning(like)}" : null).OfType<string>().ToList();
        warnings.AddRange(items.Where(i => !main.Shell.AddressBook.IsKnown(i.To)).Select(i => $"Новый получатель {Display.Address(i.To)}").Take(10));
        var rows = items.Select((i, k) => ($"{k + 1}.", $"{Display.Address(i.To)}\n{Units.FormatCoins(i.Value)} {main.Symbol(i.Coin)} (id {i.Coin})")).ToList();
        var outcome = await main.ConfirmAndSendAsync("Мультисенд", new MultisendData(items), 0, [], rows, warnings, this);
        if (outcome?.Succeeded == true) foreach (var i in items) main.Shell.AddressBook.Remember(i.To);
    }
}

public sealed class SwapViewModel : ScreenModel
{
    private static readonly string[] Presets = ["0.5", "1", "3"];
    private readonly MainViewModel main;
    private readonly DispatcherTimer quoteTimer;
    private BalanceItem? sell;
    private string amount = "", slippage = "1";
    private bool exactInput = true, sellAll;
    private (ITxData Data, string Route, string Expected, string Limit, uint GasCoin, string? Selling)? planned;

    public SwapViewModel(MainViewModel main)
    {
        this.main = main;
        Estimate = new AsyncCommand(EstimateAsync, Fail);
        SwapNow = new AsyncCommand(SwapAsync, Fail, () => planned != null);
        // "Всё": the whole balance, kept whole when the balance changes, until the amount is edited or another coin chosen.
        All = new Command(() => { if (Sell == null) return; ExactInput = true; Amount = Units.FormatCoins(Sell.Value); sellAll = true; });
        Buy = new CoinPickerViewModel(() => main.CoinOptions(Sell?.Coin.Id), () => main.HasCatalog, main.LookupCoinAsync);
        Buy.SelectedChanged += InputChanged;
        // The quote follows the inputs: asked 0.7 s after the last change.
        quoteTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        quoteTimer.Tick += (_, _) => { quoteTimer.Stop(); if (Ready) Estimate.Execute(null); };
    }

    public ObservableCollection<BalanceItem> Coins { get; } = [];
    public CoinPickerViewModel Buy { get; }
    public BalanceItem? Sell
    {
        get => sell;
        set
        {
            if (value?.Coin.Id != sell?.Coin.Id) sellAll = false;
            if (!Set(ref sell, value)) return;
            Changed(nameof(Available));
            if (sellAll && value != null) { amount = Units.FormatCoins(value.Value); Changed(nameof(Amount)); }
            if (value != null && Buy.Selected?.Id == value.Coin.Id) Buy.Selected = null;
            InputChanged();
        }
    }
    public string Amount { get => amount; set { if (!Set(ref amount, value)) return; sellAll = false; InputChanged(); } }
    public string Slippage { get => slippage; set { if (!Set(ref slippage, value)) return; Changed(nameof(SlippageIndex)); InputChanged(); } }
    public int SlippageIndex { get => Array.IndexOf(Presets, Slippage.Replace(',', '.').Trim()); set { if (value >= 0) Slippage = Presets[value]; } }
    public bool ExactInput { get => exactInput; set { if (!Set(ref exactInput, value)) return; sellAll = false; Changed(nameof(ModeIndex)); Changed(nameof(AmountLabel)); InputChanged(); } }
    public int ModeIndex { get => ExactInput ? 0 : 1; set { if (value >= 0) ExactInput = value == 0; } }
    public string AmountLabel => ExactInput ? "Сколько продать" : "Сколько купить";
    public string Available => Sell == null ? "" : $"Доступно: {Sell.AmountShort} {Sell.Symbol}";
    public bool HasQuote => planned != null;
    public string QuoteRoute => planned?.Route ?? "";
    public string QuoteExpectedLabel => ExactInput ? "Получите ≈" : "Заплатите ≈";
    public string QuoteExpected => planned?.Expected ?? "";
    public string QuoteLimit => planned?.Limit ?? "";
    public AsyncCommand Estimate { get; }
    public AsyncCommand SwapNow { get; }
    public Command All { get; }

    private bool Ready
    {
        get
        {
            if (Sell == null || Buy.Selected == null) return false;
            try { return Units.ParseCoins(Amount).Sign > 0; } catch (FormatException) { return false; }
        }
    }

    public void BalancesChanged()
    {
        var keep = Sell?.Coin.Id;
        var all = sellAll;
        Coins.Clear();   // the list drops its selection here (Sell = null), which would end "Всё"
        foreach (var b in main.Balances) Coins.Add(b);
        Sell = Coins.FirstOrDefault(c => c.Coin.Id == keep) ?? Coins.FirstOrDefault();
        if (all && Sell is { } same && same.Coin.Id == keep)
        {
            sellAll = true;
            amount = Units.FormatCoins(same.Value); Changed(nameof(Amount));
            InputChanged();
        }
    }

    private void InputChanged()
    {
        ClearQuote();
        quoteTimer?.Stop();
        if (Ready) quoteTimer?.Start();
    }

    private void ClearQuote()
    {
        planned = null;
        foreach (var name in new[] { nameof(HasQuote), nameof(QuoteRoute), nameof(QuoteExpectedLabel), nameof(QuoteExpected), nameof(QuoteLimit) }) Changed(name);
        SwapNow?.Refresh();
    }

    // Route from the source (best_trade over pools, else the reserve/bancor estimate); the limit comes from the
    // user's slippage, so a wrong quote cannot cost more than the tolerance.
    private async Task EstimateAsync()
    {
        quoteTimer.Stop();
        ClearQuote();
        if (Sell == null) { Say("Выберите, что отдаёте.", Tone.Error); return; }
        if (Buy.Selected is not { } target) { Say("Выберите, что получаете.", Tone.Error); return; }
        if (!MainViewModel.TryAmount(Amount, this, out var value)) return;
        if (!decimal.TryParse(Slippage.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var tolerance) || tolerance is < 0 or > 50)
        { Say("Проскальзывание — от 0 до 50 %.", Tone.Error); return; }
        if (target.Id == Sell.Coin.Id) { Say("Монеты совпадают.", Tone.Error); return; }
        // A quote asked in the background must not wipe what the screen says about a swap just made.
        var quiet = Tone is Tone.Success or Tone.Error;
        if (!quiet) Say("Поиск маршрута…", Tone.Progress);
        var wallet = await main.WalletAsync();
        var warning = main.Shell.Coins.Observe(new CoinRef(target.Id, target.Symbol)) is { } c ? $"Внимание: {c.Symbol} раньше был id {c.KnownId}, теперь id {c.NewId}." : "";
        var bps = (int)(tolerance * 100);
        var trade = await wallet.Backend.BestTradeAsync(Sell.Coin.Id, target.Id, value, ExactInput);
        // The whole balance: a SellAll transaction. The network takes its commission in the coin sold, whatever the gas
        // coin, and swaps it through that coin's pool or reserve right before the sale; the node's sell-all estimate
        // counts both, so the quote and the guarantee come from it.
        var all = sellAll && ExactInput;
        if (all) value = Sell.Value;
        var gas = 0u;
        string? selling = null;
        if (all)
        {
            ITxData probe = trade is { Path.Count: >= 2 } ? new SellAllSwapPoolData(trade.Path, 0) : new SellAllCoinData(Sell.Coin.Id, target.Id, 0);
            if (await CommissionAsync(wallet, probe, Sell.Coin.Id) is not { } commission)
            { Say($"«Всё» платит комиссию самой монетой, а {Sell.Coin.Symbol} её не заплатить: продайте сумму без «Всё».", Tone.Error); return; }
            if (commission >= value) { Say("Баланса не хватает даже на комиссию.", Tone.Warning); return; }
            gas = Sell.Coin.Id;
            selling = $"всё: {Units.FormatCoins(value - commission)} {Sell.Coin.Symbol} и {Units.FormatCoins(commission)} {Sell.Coin.Symbol} на комиссию";
        }
        ITxData data; string route; BigInteger expected, limit;
        if (trade is { Path.Count: >= 2 })
        {
            route = string.Join(" → ", trade.Path.Select(main.Symbol)) + " (пулы)";
            expected = all ? await wallet.Backend.EstimateSellAllAsync(Sell.Coin.Id, target.Id, value, SwapFrom.Pool, trade.Path.Skip(1).SkipLast(1).ToList()) : trade.Result;
            limit = ExactInput ? expected * (10000 - bps) / 10000 : expected * (10000 + bps) / 10000;
            data = all ? new SellAllSwapPoolData(trade.Path, limit)
                : ExactInput ? new SellSwapPoolData(trade.Path, value, limit) : new BuySwapPoolData(trade.Path, value, limit);
        }
        else
        {
            var estimate = ExactInput ? await wallet.Backend.EstimateSellAsync(Sell.Coin.Id, target.Id, value, SwapFrom.Bancor)
                                      : await wallet.Backend.EstimateBuyAsync(Sell.Coin.Id, target.Id, value, SwapFrom.Bancor);
            route = $"{Sell.Coin.Symbol} → {target.Symbol} (резерв)";
            expected = all ? await wallet.Backend.EstimateSellAllAsync(Sell.Coin.Id, target.Id, value, SwapFrom.Bancor) : estimate.Amount;
            limit = ExactInput ? expected * (10000 - bps) / 10000 : expected * (10000 + bps) / 10000;
            data = all ? new SellAllCoinData(Sell.Coin.Id, target.Id, limit)
                : ExactInput ? new SellCoinData(Sell.Coin.Id, value, target.Id, limit) : new BuyCoinData(target.Id, value, Sell.Coin.Id, limit);
        }
        if (all) route += " · продаётся всё";
        var expectedText = $"{Display.Amount(expected, 6)} {(ExactInput ? target.Symbol : Sell.Coin.Symbol)}";
        var limitText = ExactInput ? $"не меньше {Units.FormatCoins(limit)} {target.Symbol}" : $"не больше {Units.FormatCoins(limit)} {Sell.Coin.Symbol}";
        planned = (data, route, expectedText, limitText, gas, selling);
        foreach (var name in new[] { nameof(HasQuote), nameof(QuoteRoute), nameof(QuoteExpectedLabel), nameof(QuoteExpected), nameof(QuoteLimit) }) Changed(name);
        SwapNow.Refresh();
        if (warning.Length > 0) Say(warning, Tone.Warning);
        else if (!quiet) Say("");
    }

    // The commission of a transaction in this coin; null when the coin cannot pay it.
    private async Task<BigInteger?> CommissionAsync(WalletService wallet, ITxData data, uint coin)
    {
        try { return (await wallet.PreviewAsync(main.Selected!.Account, data, coin)).Commission; }
        catch (MinterWallet.Core.ChainApiException) { return null; }
    }

    private async Task SwapAsync()
    {
        if (planned is not { } p) return;
        quoteTimer.Stop();
        var rows = new List<(string, string)> { ("Маршрут", p.Route) };
        if (p.Selling != null) rows.Add(("Продаёте", p.Selling));
        rows.AddRange([("Оценка", $"≈ {p.Expected}"), ("Гарантия", p.Limit)]);
        var outcome = await main.ConfirmAndSendAsync("Обмен", p.Data, p.GasCoin, [], rows, [], this);
        if (outcome?.Succeeded == true) { amount = ""; sellAll = false; Changed(nameof(Amount)); ClearQuote(); }
        else if (outcome != null) ClearQuote();
    }
}

public sealed record CandidateItem(CandidateInfo Info, string Base, string? Name = null)
{
    public string Key => Info.PublicKey.ToString();
    public string Short => Name ?? Core.History.Short(Info.PublicKey);
    public override string ToString() => Short;
    public string Commission => $"{Info.Commission}%";
    public string CommissionText => $"комиссия {Info.Commission} %";
    public string Stake => $"{Display.Amount(Info.TotalStake, 0)} {Base}";
    public string State => Info.Validator ? "валидатор" : Info.Status == 2 ? "кандидат" : "выключен";
    public bool IsValidator => Info.Validator;
}

public sealed class StakingViewModel : ScreenModel
{
    private readonly MainViewModel main;
    private readonly List<CandidateItem> all = [];
    private CandidateItem? candidate;
    private BalanceItem? coin;
    private string amount = "", filter = "", count = "", myStake = "", locked = "";
    private CandidateItem? moveTarget;
    private IReadOnlyDictionary<string, string>? names;

    public StakingViewModel(MainViewModel main)
    {
        this.main = main;
        Load = new AsyncCommand(LoadAsync, Fail);
        DelegateNow = new AsyncCommand(() => StakeAsync(true), Fail);
        Unbond = new AsyncCommand(() => StakeAsync(false), Fail);
        Move = new AsyncCommand(MoveAsync, Fail);
        LockStakes = new AsyncCommand(LockStakesAsync, Fail);
    }

    private int modeIndex;
    public int ModeIndex
    {
        get => modeIndex;
        set
        {
            if (value < 0 || !Set(ref modeIndex, value)) return;
            Changed(nameof(IsDelegation)); Changed(nameof(IsValidatorMode));
            if (value == 1) main.Validator.Load.Execute(null);
        }
    }
    public bool IsDelegation => modeIndex == 0;
    public bool IsValidatorMode => modeIndex == 1;
    public string MyStake { get => myStake; private set => Set(ref myStake, value); }
    public CandidateItem? MoveTarget { get => moveTarget; set => Set(ref moveTarget, value); }
    public string Locked { get => locked; private set => Set(ref locked, value); }
    public AsyncCommand Move { get; }
    public AsyncCommand LockStakes { get; }

    public ObservableCollection<CandidateItem> Candidates { get; } = [];
    public ObservableCollection<BalanceItem> Coins { get; } = [];
    public CandidateItem? Candidate { get => candidate; set { if (!Set(ref candidate, value)) return; Changed(nameof(CandidateTitle)); Changed(nameof(CandidateCaption)); _ = ReadMyStakeAsync(); } }
    public BalanceItem? Coin { get => coin; set { Set(ref coin, value); Changed(nameof(Available)); } }
    public string Amount { get => amount; set => Set(ref amount, value); }
    public string Filter { get => filter; set { Set(ref filter, value); ApplyFilter(); } }
    public string Count { get => count; set => Set(ref count, value); }
    public string CandidateTitle => Candidate?.Short ?? "Выберите валидатора в списке";
    public string CandidateCaption => Candidate == null ? "" : $"{Candidate.State} · {Candidate.CommissionText} · стейк {Candidate.Stake}";
    public string Available => Coin == null ? "" : $"Доступно: {Coin.AmountShort} {Coin.Symbol}";
    public AsyncCommand Load { get; }
    public AsyncCommand DelegateNow { get; }
    public AsyncCommand Unbond { get; }

    public void BalancesChanged()
    {
        var keep = Coin?.Coin.Id;
        Coins.Clear();
        foreach (var b in main.Balances) Coins.Add(b);
        Coin = Coins.FirstOrDefault(c => c.Coin.Id == keep) ?? Coins.FirstOrDefault(c => c.Coin.Id == 0) ?? Coins.FirstOrDefault();
        if (all.Count == 0) Load.Execute(null);
        var until = (long)(main.State?.LockedStakeUntil ?? 0);
        Locked = main.Shell.Status is { } s && until > s.Height
            ? $"Стейки этого счёта заморожены до блока {Display.Integer(until)} (≈ {s.BlockTime.AddSeconds((until - s.Height) * 5).ToLocalTime():dd.MM.yyyy})." : "";
    }

    public void Reset() { all.Clear(); Candidates.Clear(); Candidate = null; Count = ""; }

    private void ApplyFilter()
    {
        var keep = Candidate;
        var text = Filter.Trim();
        Candidates.Clear();
        foreach (var c in all.Where(c => text.Length == 0 || c.Key.Contains(text, StringComparison.OrdinalIgnoreCase) || c.Name?.Contains(text, StringComparison.OrdinalIgnoreCase) == true)) Candidates.Add(c);
        if (keep != null && Candidates.Contains(keep)) Candidate = keep;
    }

    private async Task LoadAsync()
    {
        var wallet = await main.WalletAsync();
        var list = await wallet.Backend.CandidatesAsync();
        // Mainnet validators' own names, from the explorer; the keys alone elsewhere or when it does not answer.
        if (names == null && main.Shell.Status?.Network.StartsWith("minter-mainnet", StringComparison.Ordinal) == true)
            try { names = await main.Shell.Explorer.ValidatorNamesAsync(); } catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { }
        all.Clear();
        all.AddRange(list.OrderByDescending(c => c.Validator).ThenByDescending(c => c.TotalStake)
            .Select(c => new CandidateItem(c, main.Symbol(0), names?.GetValueOrDefault(c.PublicKey.ToString()))));
        ApplyFilter();
        Count = $"Кандидатов: {Display.Integer(all.Count)}, из них валидаторов: {Display.Integer(all.Count(c => c.Info.Validator))}";
    }

    private async Task StakeAsync(bool delegating)
    {
        if (Candidate == null) { Say("Выберите валидатора в списке.", Tone.Error); return; }
        if (Coin == null) { Say("Выберите монету.", Tone.Error); return; }
        if (!MainViewModel.TryAmount(Amount, this, out var value)) return;
        ITxData data = delegating ? new DelegateData(Candidate.Info.PublicKey, Coin.Coin.Id, value) : new UnbondData(Candidate.Info.PublicKey, Coin.Coin.Id, value);
        var warnings = delegating ? new List<string>() : ["Выведенный стейк замораживается примерно на 30 дней (518 400 блоков) и только потом вернётся на счёт."];
        if (delegating && Candidate.Info.Commission >= 50) warnings.Add($"Комиссия валидатора {Candidate.Info.Commission}%: он забирает эту долю наград.");
        var outcome = await main.ConfirmAndSendAsync(delegating ? "Делегирование" : "Вывод из стейка", data, 0, [],
            [("Валидатор", $"{Candidate.Key}\nкомиссия {Candidate.Commission}, {Candidate.State}"), ("Сумма", $"{Units.FormatCoins(value)} {Coin.Coin.Symbol} (id {Coin.Coin.Id})")], warnings, this);
        if (outcome?.Succeeded == true) _ = ReadMyStakeAsync();
    }

    // This account's stake with the chosen validator, read from the candidate's list of stakes.
    private async Task ReadMyStakeAsync()
    {
        MyStake = "";
        if (Candidate is not { } c || main.Selected == null) return;
        try
        {
            var wallet = await main.WalletAsync();
            var stakes = await wallet.Backend.StakesAsync(c.Info.PublicKey);
            if (Candidate != c) return;
            var mine = stakes.Where(s => s.Owner == main.Selected.Account.Address).ToList();
            MyStake = mine.Count == 0 ? "Вашего стейка у этого валидатора нет." : "Ваш стейк здесь: " + string.Join(", ", mine.Select(s => $"{Display.Amount(s.Value)} {s.Coin.Symbol}"));
        }
        catch (Exception e) when (e is HttpRequestException or ChainApiException or TaskCanceledException) { }
    }

    // A stake from the chosen validator to another without the 30-day unbond: it arrives after the move period.
    private async Task MoveAsync()
    {
        if (Candidate == null) { Say("Выберите валидатора в списке — откуда переносить.", Tone.Error); return; }
        if (MoveTarget == null || MoveTarget.Key == Candidate.Key) { Say("Выберите, к какому валидатору перенести.", Tone.Error); return; }
        if (Coin == null) { Say("Выберите монету.", Tone.Error); return; }
        if (!MainViewModel.TryAmount(Amount, this, out var value)) return;
        var outcome = await main.ConfirmAndSendAsync("Перенос стейка", new MoveStakeData(Candidate.Info.PublicKey, MoveTarget.Info.PublicKey, Coin.Coin.Id, value), 0, [],
            [("Откуда", $"{Candidate.Short}\n{Candidate.Key}"), ("Куда", $"{MoveTarget.Short}\n{MoveTarget.Key}\nкомиссия {MoveTarget.Commission}, {MoveTarget.State}"),
             ("Сумма", $"{Units.FormatCoins(value)} {Coin.Coin.Symbol} (id {Coin.Coin.Id})")],
            ["Стейк дойдёт до нового валидатора примерно через неделю (на тестовых сетях — через 15 минут); пока он в пути, наград за него нет. Это быстрее вывода, который длится 30 дней."], this);
        if (outcome?.Succeeded == true) _ = ReadMyStakeAsync();
    }

    // All stakes of the account locked for higher rewards: three years on mainnet, two days on test networks.
    private async Task LockStakesAsync()
    {
        await main.ConfirmAndSendAsync("Заморозка стейков", new LockStakeData(), 0, [],
            [("Что", "все стейки этого счёта, у всех валидаторов"), ("Срок", "3 года (на тестовых сетях — 2 дня)")],
            ["Все стейки счёта будут заморожены на 3 года: ни вывести, ни перенести их до срока нельзя. Взамен они получают повышенные награды. Отменить заморозку нельзя.",
             "Заморозка ставится на весь счёт: новые делегирования тоже не вывести до этого срока, а повторная заморозка продлевает его на 3 года от момента повтора."], this);
    }
}

public sealed record HistoryItem(HistoryEntry Entry, bool FromExplorer = false)
{
    public string Summary => Entry.Summary;
    public string Height => Display.Integer(Entry.Height);
    public string HeightText => $"блок {Display.Integer(Entry.Height)}";
    public string Payload => Entry.Payload.Length > 0 ? $"«{Entry.Payload}»" : "";
    public bool HasPayload => Entry.Payload.Length > 0;
    public bool Failed => !Entry.Success;
    public string FailedText => Entry.Success ? "" : $"Не выполнена: {Entry.Log}";
    public string Hash => Entry.Hash;
    public string HashShort => Entry.Hash.Length > 18 ? $"{Entry.Hash[..10]}…{Entry.Hash[^6..]}" : Entry.Hash;
    public bool Incoming => Entry.Direction == Direction.Incoming && Entry.Success;
    public string Glyph => !Entry.Success ? "error" : Entry.Type switch
    {
        TxType.SellCoin or TxType.SellAllCoin or TxType.BuyCoin or TxType.SellSwapPool or TxType.BuySwapPool or TxType.SellAllSwapPool => "swap",
        TxType.Delegate or TxType.Unbond or TxType.MoveStake or TxType.LockStake => "stake",
        _ => Entry.Direction == Direction.Incoming ? "receive" : "send"
    };
}

// History newest first, a page at a time with "show more": the source's own history, then (a gate on mainnet) the
// older part from the Minter explorer.
public sealed class HistoryViewModel : ScreenModel
{
    private const int PageSize = 30;
    private readonly MainViewModel main;
    private HistoryCursor? cursor;
    private bool loading, loaded, hasMore, olderShown;

    public HistoryViewModel(MainViewModel main)
    {
        this.main = main;
        Load = new AsyncCommand(async () => { cursor = null; Items.Clear(); olderShown = false; await MoreAsync(); }, e => { Loading = false; Fail(e); });
        More = new AsyncCommand(MoreAsync, e => { Loading = false; Fail(e); });
        Export = new AsyncCommand(ExportAsync, Fail, () => main.Shell.SaveText != null);
        CopyHash = new RelayCommand(async hash => { if (hash is string h && main.Shell.CopyText != null) { await main.Shell.CopyText(h); Say("Хеш транзакции скопирован.", Tone.Success); } });
    }

    public ObservableCollection<HistoryItem> Items { get; } = [];
    public bool Loading { get => loading; set { Set(ref loading, value); Changed(nameof(IsEmpty)); } }
    public bool HasMore { get => hasMore; private set => Set(ref hasMore, value); }
    public bool IsEmpty => loaded && !Loading && Items.Count == 0;
    public string EmptyText => "Транзакций пока нет.";
    public string Note => olderShown ? "Старые транзакции — из обозревателя Minter: наш гейт помнит историю с момента своего запуска." : "";
    public bool HasNote => olderShown;
    public AsyncCommand Load { get; }
    public AsyncCommand More { get; }
    public AsyncCommand Export { get; }
    public RelayCommand CopyHash { get; }

    public void Reset()
    {
        cursor = null; loaded = false; olderShown = false;
        Items.Clear();
        HasMore = false;
        Changed(nameof(IsEmpty)); Changed(nameof(Note)); Changed(nameof(HasNote));
        if (main.IsHistory) Load.Execute(null);
    }

    private async Task MoreAsync()
    {
        if (main.Selected == null) return;
        Loading = true;
        var wallet = await main.WalletAsync();
        var me = main.Selected.Account.Address;
        cursor ??= main.HistoryCursor(wallet, me, PageSize);
        foreach (var (record, older) in await cursor.NextAsync())
        {
            HistoryEntry entry;
            try { entry = Core.History.Decode(record, me, main.Symbol); }
            catch (FormatException) { continue; }   // bytes the wallet cannot read (a third-party answer) are skipped
            Items.Add(new HistoryItem(entry, older));
            olderShown |= older;
        }
        loaded = true;
        Loading = false;
        HasMore = cursor.HasMore;
        Changed(nameof(Note)); Changed(nameof(HasNote)); Changed(nameof(IsEmpty));
        if (Tone == Tone.Error) Say("");
    }

    // The whole history (up to ExportLimit transactions) to a CSV file: every page, then the time of each block not
    // dated by the explorer, asked of the source a block at a time and slowly enough for a gate's rate limit.
    private const int ExportLimit = 20000;
    private async Task ExportAsync()
    {
        if (main.Selected == null || main.Shell.SaveText == null) return;
        var wallet = await main.WalletAsync();
        var me = main.Selected.Account.Address;
        var all = main.HistoryCursor(wallet, me, 100);
        var rows = new List<(TxRecord Record, HistoryEntry Entry)>();
        Say("Собираю историю…", Tone.Progress);
        while (true)
        {
            foreach (var (record, _) in await all.NextAsync())
                try { rows.Add((record, Core.History.Decode(record, me, main.Symbol))); } catch (FormatException) { }
            if (!all.HasMore || rows.Count >= ExportLimit) break;
            Say($"Собираю историю: {rows.Count} транзакций…", Tone.Progress);
        }
        var times = new Dictionary<long, DateTimeOffset?>();
        var heights = rows.Where(r => !r.Record.Tags.ContainsKey("timestamp")).Select(r => r.Record.Height).Distinct().ToList();
        for (var i = 0; i < heights.Count; i++)
        {
            if (i % 10 == 0) Say($"Время блоков: {i} из {heights.Count}…", Tone.Progress);
            try { times[heights[i]] = await wallet.Backend.BlockTimeAsync(heights[i]); }
            catch (Exception e) when (e is HttpRequestException or MinterWallet.Core.ChainApiException) { times[heights[i]] = null; }
            await Task.Delay(110);
        }
        DateTimeOffset? TimeOf(TxRecord r) => r.Tags.TryGetValue("timestamp", out var t) && DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? at : times.GetValueOrDefault(r.Height);
        var csv = HistoryTable.Csv(rows.Select(r => new HistoryRow(r.Record, r.Entry, TimeOf(r.Record))), me, main.Symbol, Localization.Text);
        var name = $"minter-{me.ToString()[..8]}-{DateTime.Now:yyyy-MM-dd}.csv";
        Say("");
        if (await main.Shell.SaveText(name, csv))
            Say(rows.Count >= ExportLimit ? $"Сохранено {rows.Count} последних транзакций (больше за раз не выгружается)." : $"Сохранено транзакций: {rows.Count}.", Tone.Success);
    }
}

public sealed record SourceItem(SourceSettings Source)
{
    public string Name => Source.Name;
    public string Url => Source.Url;
    public string Caption => (Source.Kind switch { "gate" => "MinterGate", "node" => "нода Minter", _ => "ещё не проверен" })
        + (Source.ExpectedNetwork is { } n ? $" · {n}" : "") + (Source.IsDefaultGate ? " · по умолчанию" : "");
    public string Glyph => Source.Kind == "node" ? "stake" : "shield";
}

public sealed class SettingsViewModel : ScreenModel
{
    private static readonly string[] Themes = ["system", "dark", "light"];
    private readonly MainViewModel main;
    private string gasCap, autoLock, newName = "", newUrl = "", currentPassword = "", newPassword = "", repeatPassword = "";
    private bool adding, newSkipCertificate;

    public SettingsViewModel(MainViewModel main)
    {
        this.main = main;
        var s = main.Shell.Settings;
        gasCap = s.GasPriceCap.ToString(CultureInfo.InvariantCulture); autoLock = s.AutoLockMinutes.ToString(CultureInfo.InvariantCulture);
        SaveSecurity = new Command(SaveSecurityNow);
        AcceptNetwork = new AsyncCommand(async () => { main.Shell.Settings.Source.ExpectedNetwork = null; main.Shell.Settings.Save(main.Shell.DataDirectory); await ConnectAsync(); }, Fail);
        Reconnect = new AsyncCommand(ConnectAsync, Fail);
        ShowAdd = new Command(() => Adding = !Adding);
        AddSource = new AsyncCommand(AddSourceAsync, Fail);
        RemoveSource = new AsyncCommand(RemoveSourceAsync, Fail);
        ChangePassword = new AsyncCommand(ChangePasswordAsync, Fail);
        LoadSources();
    }

    public ObservableCollection<SourceItem> Sources { get; } = [];
    public int SourceIndex
    {
        get => main.Shell.Settings.SelectedSource;
        set
        {
            var s = main.Shell.Settings;
            if (value < 0 || value >= s.Sources.Count || value == s.SelectedSource) return;
            s.SelectedSource = value;
            s.Save(main.Shell.DataDirectory);
            Changed(); Changed(nameof(CanRemove)); Changed(nameof(SkipCertificate));
            Reconnect.Execute(null);
        }
    }
    public bool CanRemove => !main.Shell.Settings.Source.IsDefaultGate && main.Shell.Settings.Sources.Count > 1;
    public bool NewSkipCertificate { get => newSkipCertificate; set => Set(ref newSkipCertificate, value); }
    public bool SkipCertificate
    {
        get => main.Shell.Settings.Source.SkipCertificateCheck;
        set
        {
            var source = main.Shell.Settings.Source;
            if (value == source.SkipCertificateCheck) return;
            source.SkipCertificateCheck = value;
            main.Shell.Settings.Save(main.Shell.DataDirectory);
            Changed();
            Reconnect.Execute(null);
        }
    }
    public bool Adding { get => adding; set => Set(ref adding, value); }
    public string NewName { get => newName; set => Set(ref newName, value); }
    public string NewUrl { get => newUrl; set => Set(ref newUrl, value); }
    public string GasCap { get => gasCap; set => Set(ref gasCap, value); }
    public string AutoLock { get => autoLock; set => Set(ref autoLock, value); }
    public string CurrentPassword { get => currentPassword; set => Set(ref currentPassword, value); }
    public string NewPassword { get => newPassword; set { Set(ref newPassword, value); Changed(nameof(PasswordHint)); } }
    public string RepeatPassword { get => repeatPassword; set { Set(ref repeatPassword, value); Changed(nameof(PasswordHint)); } }
    public string PasswordHint => NewPassword.Length == 0 ? "" : Passwords.Hint(NewPassword, RepeatPassword);
    public AsyncCommand ChangePassword { get; }
    public bool CheckUpdates
    {
        get => main.Shell.Settings.CheckUpdates;
        set { if (value == main.Shell.Settings.CheckUpdates) return; main.Shell.Settings.CheckUpdates = value; main.Shell.Settings.Save(main.Shell.DataDirectory); Changed(); }
    }
    public bool HistoryFromExplorer
    {
        get => main.Shell.Settings.HistoryFromExplorer;
        set
        {
            if (value == main.Shell.Settings.HistoryFromExplorer) return;
            main.Shell.Settings.HistoryFromExplorer = value;
            main.Shell.Settings.Save(main.Shell.DataDirectory);
            Changed();
            main.History.Reset();
        }
    }
    private static readonly string[] Languages = ["system", "ru", "en"];
    public int LanguageIndex
    {
        get => Math.Max(0, Array.IndexOf(Languages, main.Shell.Settings.Language));
        set
        {
            if (value < 0 || Languages[value] == main.Shell.Settings.Language) return;
            main.Shell.Settings.Language = Languages[value];
            main.Shell.Settings.Save(main.Shell.DataDirectory);
            Localization.Use(Languages[value]);
            Changed();
            Dispatcher.UIThread.Post(main.Shell.Redraw);
        }
    }
    public int AddressIndex
    {
        get => main.Shell.Settings.GroupAddresses ? 1 : 0;
        set { if (value < 0 || (value == 1) == main.Shell.Settings.GroupAddresses) return; main.Shell.Settings.GroupAddresses = value == 1; Restyle(); }
    }
    public int DecimalsIndex
    {
        get => main.Shell.Settings.AllDecimals ? 1 : 0;
        set { if (value < 0 || (value == 1) == main.Shell.Settings.AllDecimals) return; main.Shell.Settings.AllDecimals = value == 1; Restyle(); }
    }

    // Addresses and amounts are put together when shown: the views are drawn again and the balances read again.
    private void Restyle()
    {
        main.Shell.Settings.Save(main.Shell.DataDirectory);
        Display.Use(main.Shell.Settings);
        Changed(nameof(AddressIndex)); Changed(nameof(DecimalsIndex));
        Dispatcher.UIThread.Post(main.Shell.Redraw);
        main.Refresh.Execute(null);
    }
    public int ThemeIndex
    {
        get => Math.Max(0, Array.IndexOf(Themes, main.Shell.Settings.Theme));
        set
        {
            if (value < 0 || Themes[value] == main.Shell.Settings.Theme) return;
            main.Shell.Settings.Theme = Themes[value];
            main.Shell.Settings.Save(main.Shell.DataDirectory);
            App.ApplyTheme(Themes[value]);
            Changed();
        }
    }
    public string DataDirectory => main.Shell.DataDirectory;
    public string Version => $"Minter Wallet {Program.Version}";
    public Command SaveSecurity { get; }
    public AsyncCommand AcceptNetwork { get; }
    public AsyncCommand Reconnect { get; }
    public Command ShowAdd { get; }
    public AsyncCommand AddSource { get; }
    public AsyncCommand RemoveSource { get; }

    private void LoadSources()
    {
        Sources.Clear();
        foreach (var source in main.Shell.Settings.Sources) Sources.Add(new SourceItem(source));
        Changed(nameof(SourceIndex)); Changed(nameof(CanRemove)); Changed(nameof(SkipCertificate));
    }

    // Drops everything read from the previous source and connects to the chosen one.
    private async Task ConnectAsync()
    {
        var source = main.Shell.Settings.Source;
        main.Disconnect();
        main.Say("");
        Say($"Подключение к «{source.Name}»…", Tone.Progress);
        try
        {
            var status = await main.Shell.ConnectAsync();
            Say($"Подключено: {(source.Kind == "gate" ? "MinterGate" : "нода Minter")}, сеть {status.Network}, блок {Display.Integer(status.Height)}.", Tone.Success);
        }
        catch (Exception e) { Fail(e); }
        LoadSources();
        main.Refresh.Execute(null);
    }

    // A new source is asked for its status first, so a typo or a dead address is not saved.
    private async Task AddSourceAsync()
    {
        var text = NewUrl.Trim();
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) { Say("Адрес источника: https://хост:порт или http://хост:порт.", Tone.Error); return; }
        var url = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        if (url.EndsWith("/v2", StringComparison.Ordinal)) url = url[..^3];
        var settings = main.Shell.Settings;
        if (settings.Sources.FindIndex(x => string.Equals(x.Url, url, StringComparison.OrdinalIgnoreCase)) is >= 0 and var existing) { SourceIndex = existing; Adding = false; return; }
        Say("Проверка источника…", Tone.Progress);
        using var probe = new MinterWallet.Api.NodeApiClient(new Uri(url), skipCertificateCheck: NewSkipCertificate);
        var status = await probe.StatusAsync();
        var gate = await probe.IsGateAsync();
        settings.Sources.Add(new SourceSettings { Name = NewName.Trim().Length > 0 ? NewName.Trim() : uri.Host, Url = url, Kind = gate ? "gate" : "node", ExpectedNetwork = status.Network, SkipCertificateCheck = NewSkipCertificate });
        settings.Save(main.Shell.DataDirectory);
        NewName = ""; NewUrl = ""; Adding = false; NewSkipCertificate = false;
        LoadSources();
        SourceIndex = settings.Sources.Count - 1;
    }

    private async Task RemoveSourceAsync()
    {
        var settings = main.Shell.Settings;
        if (!CanRemove) return;
        settings.Sources.RemoveAt(settings.SelectedSource);
        settings.SelectedSource = Math.Max(0, settings.Sources.FindIndex(x => x.IsDefaultGate));
        settings.Save(main.Shell.DataDirectory);
        LoadSources();
        await ConnectAsync();
    }

    // The key store re-encrypted under the new password (Argon2id with a fresh salt); the current one proves the owner.
    private async Task ChangePasswordAsync()
    {
        if (Passwords.Problem(NewPassword, RepeatPassword) is { } problem) { Say(problem, Tone.Error); return; }
        Say("Шифрование ключей новым паролем…", Tone.Progress);
        try { await Task.Run(() => main.Shell.Session!.ChangePassword(CurrentPassword, NewPassword)); }
        catch (WrongPasswordException) { Say("Текущий пароль неверен — пароль не изменён.", Tone.Error); return; }
        CurrentPassword = ""; NewPassword = ""; RepeatPassword = "";
        Say("Пароль изменён. Он нужен при входе и перед каждой подписью.", Tone.Success);
    }

    private void SaveSecurityNow()
    {
        var s = main.Shell.Settings;
        if (!int.TryParse(AutoLock, out var minutes) || minutes < 1) { Say("Автоблокировка — целое число минут, от 1.", Tone.Error); return; }
        if (!uint.TryParse(GasCap, out var cap) || cap < 1) { Say("Потолок gas price — целое число, от 1.", Tone.Error); return; }
        s.AutoLockMinutes = minutes; s.GasPriceCap = cap;
        s.Save(main.Shell.DataDirectory);
        if (main.Shell.Wallet != null) main.Shell.Wallet.Gas.Cap = cap;
        Say("Сохранено.", Tone.Success);
    }
}
