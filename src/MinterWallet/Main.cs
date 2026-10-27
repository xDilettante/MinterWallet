using System.Collections.ObjectModel;
using System.Globalization;
using System.Numerics;
using System.Text;
using Avalonia.Threading;
using Minter.Protocol;
using MinterWallet.Core;

namespace MinterWallet;

public enum Section { Overview, Send, Swap, Staking, History, Accounts, Settings }

public sealed record AccountItem(WalletAccount Account, string Kind, string Glyph)
{
    public string Name => Account.Name;
    public string Short => History.Short(Account.Address);
    public string Caption => $"{Short} · {Kind}";
    public string Grouped => AddressBook.Grouped(Account.Address);
    public override string ToString() => $"{Name} · {Short}";
}

public sealed record BalanceItem(CoinRef Coin, BigInteger Value, BigInteger BipValue, string BaseSymbol)
{
    public string Symbol => Coin.Symbol;
    public string Id => Coin.Id.ToString(CultureInfo.InvariantCulture);
    public string IdText => $"id {Id}";
    public string Amount => Units.FormatCoins(Value);
    public string AmountShort => Display.Amount(Value);
    public string Bip => Units.FormatCoins(BipValue);
    public string BipText => $"≈ {Display.Amount(BipValue)} {BaseSymbol}";
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
}

public sealed record FrozenItem(string Amount, string AmountShort, string Caption);

public sealed class MainViewModel : ScreenModel
{
    private readonly Shell shell;
    private readonly DispatcherTimer autoLock;
    private AccountItem? selected;
    private Section section;
    private string sourceName, networkText = "подключение…", blockText = "", updated = "", totalText = "—", totalCaption = "", copyLabel = "Копировать";
    private bool connected;
    private AccountState? state;
    private IReadOnlyList<CoinListing>? catalog;
    private object? catalogFrom;

    public MainViewModel(Shell shell)
    {
        this.shell = shell;
        sourceName = shell.Settings.Source.Name;
        Refresh = new AsyncCommand(RefreshAsync, e => { Connected = false; NetworkText = "нет связи"; BlockText = ""; Fail(e); });
        LockNow = new Command(shell.Lock);
        CopyAddress = new AsyncCommand(CopyAddressAsync, Fail);
        OpenSend = new Command(() => Section = Section.Send);
        OpenSwap = new Command(() => Section = Section.Swap);
        OpenStaking = new Command(() => Section = Section.Staking);
        AddAddress = new Command(() => { AccountsPage!.StartAdding(); Section = Section.Accounts; });
        Send = new SendViewModel(this);
        Multisend = new MultisendViewModel(this);
        Swap = new SwapViewModel(this);
        Staking = new StakingViewModel(this);
        History = new HistoryViewModel(this);
        AccountsPage = new AccountsViewModel(this);
        Settings = new SettingsViewModel(this);
        LoadAccounts(shell.Settings.LastAccountId);
        autoLock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        autoLock.Tick += (_, _) => { if (DateTime.UtcNow - LastActivity > TimeSpan.FromMinutes(Math.Max(1, shell.Settings.AutoLockMinutes))) { autoLock.Stop(); shell.Lock(); } };
        autoLock.Start();
        Refresh.Execute(null);
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
            foreach (var name in new[] { nameof(SectionIndex), nameof(IsOverview), nameof(IsSend), nameof(IsSwap), nameof(IsStaking), nameof(IsHistory), nameof(IsAccounts), nameof(IsSettings) }) Changed(name);
            if (value == Section.History && History.Items.Count == 0) History.Load.Execute(null);
        }
    }
    public int SectionIndex { get => (int)section; set { if (value >= 0) Section = (Section)value; } }
    public bool IsOverview => section == Section.Overview;
    public bool IsSend => section == Section.Send;
    public bool IsSwap => section == Section.Swap;
    public bool IsStaking => section == Section.Staking;
    public bool IsHistory => section == Section.History;
    public bool IsAccounts => section == Section.Accounts;
    public bool IsSettings => section == Section.Settings;

    public AccountItem? Selected
    {
        get => selected;
        set
        {
            if (!Set(ref selected, value) || value == null) return;
            foreach (var name in new[] { nameof(Address), nameof(GroupedAddress), nameof(CanSign), nameof(IsWatchOnly), nameof(AccountName) }) Changed(name);
            shell.Settings.LastAccountId = value.Account.Id; shell.Settings.Save(shell.DataDirectory);
            History.Reset();
            AccountsPage.Reload();
            Refresh.Execute(null);
        }
    }
    public string Address => Selected?.Account.Address.ToString() ?? "";
    public string GroupedAddress => Selected == null ? "" : AddressBook.Grouped(Selected.Account.Address);
    public string AccountName => Selected?.Name ?? "";
    public bool CanSign => Selected is { Account.WatchOnly: false };
    public bool IsWatchOnly => Selected is { Account.WatchOnly: true };
    public string BaseSymbol => Symbol(0);
    public string SourceName { get => sourceName; set => Set(ref sourceName, value); }
    public string NetworkText { get => networkText; set => Set(ref networkText, value); }
    public string BlockText { get => blockText; set => Set(ref blockText, value); }
    public string SourceTip => $"{shell.Settings.Source.Url}\nОтветы источника кошелёк не проверяет: выбирайте тот, которому доверяете.{(updated.Length > 0 ? $"\nОбновлено {updated}." : "")}";
    public bool Connected { get => connected; set => Set(ref connected, value); }
    public string TotalText { get => totalText; set => Set(ref totalText, value); }
    public string TotalCaption { get => totalCaption; set => Set(ref totalCaption, value); }
    public string CopyLabel { get => copyLabel; set => Set(ref copyLabel, value); }
    public bool HasBalances => Balances.Count > 0;
    public bool HasNoBalances => state != null && Balances.Count == 0;
    public bool HasDelegations => Delegations.Count > 0;
    public bool HasFrozen => Frozen.Count > 0;
    public AccountState? State => state;

    public AsyncCommand Refresh { get; }
    public Command LockNow { get; }
    public AsyncCommand CopyAddress { get; }
    public Command OpenSend { get; }
    public Command OpenSwap { get; }
    public Command OpenStaking { get; }
    public Command AddAddress { get; }
    public AccountsViewModel AccountsPage { get; }
    public SendViewModel Send { get; }
    public MultisendViewModel Multisend { get; }
    public SwapViewModel Swap { get; }
    public StakingViewModel Staking { get; }
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
        catalog = null; catalogFrom = null;
        Connected = false; SourceName = shell.Settings.Source.Name; NetworkText = "подключение…"; BlockText = "";
        Staking.Reset(); History.Reset(); Swap.Buy.Selected = null;
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
        Swap.Buy.Refresh();
    }

    public string Symbol(uint coin) => shell.Coins.Symbol(coin);

    public async Task<WalletService> WalletAsync()
    {
        if (shell.Wallet != null) return shell.Wallet;
        var s = await shell.ConnectAsync();
        ShowSource(s);
        return shell.Wallet!;
    }

    private void ShowSource(ChainStatus s)
    {
        SourceName = shell.Settings.Source.Name;
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
        Balances.Clear();
        foreach (var b in state.Balances)
        {
            if (shell.Coins.Observe(b.Coin) is { } c) conflicts.Add($"{c.Symbol}: раньше id {c.KnownId}, теперь id {c.NewId}");
            Balances.Add(new BalanceItem(b.Coin, b.Value, b.BipValue, BaseSymbol));
        }
        Delegations.Clear();
        foreach (var d in state.Delegated) Delegations.Add(new DelegationItem(d, BaseSymbol));
        TotalText = Display.Amount(state.TotalBipValue);
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
        Send.BalancesChanged(); Swap.BalancesChanged(); Staking.BalancesChanged();
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
        var allRows = new List<(string, string)> { ("Операция", Core.History.Describe(data, account.Address, account.Address, Symbol)), ("Со счёта", $"{account.Name}\n{AddressBook.Grouped(account.Address)}") };
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
    private string recipient = "", amount = "", payload = "";
    private BalanceItem? coin, gasCoin;
    private int modeIndex;

    public SendViewModel(MainViewModel main)
    {
        this.main = main;
        SendNow = new AsyncCommand(SendAsync, Fail);
        All = new AsyncCommand(AllAsync, Fail);
        Paste = new AsyncCommand(PasteAsync, Fail);
    }

    public ObservableCollection<BalanceItem> Coins { get; } = [];
    public int ModeIndex { get => modeIndex; set { if (value >= 0 && Set(ref modeIndex, value)) { Changed(nameof(Multiple)); Changed(nameof(Single)); } } }
    public bool Multiple { get => modeIndex == 1; set => ModeIndex = value ? 1 : 0; }
    public bool Single => modeIndex == 0;
    public string Recipient { get => recipient; set { Set(ref recipient, value); Changed(nameof(RecipientCheck)); Changed(nameof(RecipientTone)); } }
    public BalanceItem? Coin { get => coin; set { Set(ref coin, value); Changed(nameof(Available)); } }
    public BalanceItem? GasCoin { get => gasCoin; set => Set(ref gasCoin, value); }
    public string Amount { get => amount; set => Set(ref amount, value); }
    public string Payload { get => payload; set => Set(ref payload, value); }
    public string Available => Coin == null ? "" : $"Доступно: {Coin.AmountShort} {Coin.Symbol}";
    public AsyncCommand SendNow { get; }
    public AsyncCommand All { get; }
    public AsyncCommand Paste { get; }

    public string RecipientCheck => !Minter.Protocol.Address.TryParse(Recipient.Trim(), out var a) ? (Recipient.Length == 0 ? "" : "Адрес — Mx и 40 шестнадцатеричных символов.")
        : main.Shell.AddressBook.NameOf(a) is { Length: > 0 } n ? $"Из адресной книги: {n}" : main.Shell.AddressBook.IsKnown(a) ? "Вы уже отправляли на этот адрес."
        : "Новый получатель: сверьте адрес по группам символов в подтверждении.";
    public Tone RecipientTone => !Minter.Protocol.Address.TryParse(Recipient.Trim(), out var a) ? (Recipient.Length == 0 ? Tone.None : Tone.Error)
        : main.Shell.AddressBook.IsKnown(a) ? Tone.Success : Tone.Warning;

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
        if (!main.Shell.AddressBook.IsKnown(to)) warnings.Add("Новый получатель. У адресов Minter нет контрольной суммы: опечатка или подмена в буфере обмена дадут другой действительный адрес. Сверьте все группы символов.");
        if (to == main.Selected!.Account.Address) warnings.Add("Перевод самому себе.");
        var outcome = await main.ConfirmAndSendAsync("Перевод", data, GasCoin?.Coin.Id ?? 0, MainViewModel.Utf8(Payload),
            [("Получатель", AddressBook.Grouped(to)), ("Сумма", $"{Units.FormatCoins(value)} {Coin.Coin.Symbol} (id {Coin.Coin.Id})")], warnings, this);
        if (outcome?.Succeeded == true) { main.Shell.AddressBook.Remember(to); Amount = ""; Payload = ""; Changed(nameof(RecipientCheck)); Changed(nameof(RecipientTone)); }
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
        var warnings = items.Where(i => !main.Shell.AddressBook.IsKnown(i.To)).Select(i => $"Новый получатель {AddressBook.Grouped(i.To)}").Take(10).ToList();
        var rows = items.Select((i, k) => ($"{k + 1}.", $"{AddressBook.Grouped(i.To)}\n{Units.FormatCoins(i.Value)} {main.Symbol(i.Coin)} (id {i.Coin})")).ToList();
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
    private bool exactInput = true;
    private (ITxData Data, string Route, string Expected, string Limit)? planned;

    public SwapViewModel(MainViewModel main)
    {
        this.main = main;
        Estimate = new AsyncCommand(EstimateAsync, Fail);
        SwapNow = new AsyncCommand(SwapAsync, Fail, () => planned != null);
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
            if (!Set(ref sell, value)) return;
            Changed(nameof(Available));
            if (value != null && Buy.Selected?.Id == value.Coin.Id) Buy.Selected = null;
            InputChanged();
        }
    }
    public string Amount { get => amount; set { if (Set(ref amount, value)) InputChanged(); } }
    public string Slippage { get => slippage; set { if (!Set(ref slippage, value)) return; Changed(nameof(SlippageIndex)); InputChanged(); } }
    public int SlippageIndex { get => Array.IndexOf(Presets, Slippage.Replace(',', '.').Trim()); set { if (value >= 0) Slippage = Presets[value]; } }
    public bool ExactInput { get => exactInput; set { if (!Set(ref exactInput, value)) return; Changed(nameof(ModeIndex)); Changed(nameof(AmountLabel)); InputChanged(); } }
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
        Coins.Clear();
        foreach (var b in main.Balances) Coins.Add(b);
        Sell = Coins.FirstOrDefault(c => c.Coin.Id == keep) ?? Coins.FirstOrDefault();
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
        Say("Поиск маршрута…", Tone.Progress);
        var wallet = await main.WalletAsync();
        var warning = main.Shell.Coins.Observe(new CoinRef(target.Id, target.Symbol)) is { } c ? $"Внимание: {c.Symbol} раньше был id {c.KnownId}, теперь id {c.NewId}." : "";
        var bps = (int)(tolerance * 100);
        var trade = await wallet.Backend.BestTradeAsync(Sell.Coin.Id, target.Id, value, ExactInput);
        ITxData data; string route; BigInteger expected, limit;
        if (trade is { Path.Count: >= 2 })
        {
            route = string.Join(" → ", trade.Path.Select(main.Symbol)) + " (пулы)";
            expected = trade.Result;
            limit = ExactInput ? expected * (10000 - bps) / 10000 : expected * (10000 + bps) / 10000;
            data = ExactInput ? new SellSwapPoolData(trade.Path, value, limit) : new BuySwapPoolData(trade.Path, value, limit);
        }
        else
        {
            var estimate = ExactInput ? await wallet.Backend.EstimateSellAsync(Sell.Coin.Id, target.Id, value, SwapFrom.Bancor)
                                      : await wallet.Backend.EstimateBuyAsync(Sell.Coin.Id, target.Id, value, SwapFrom.Bancor);
            route = $"{Sell.Coin.Symbol} → {target.Symbol} (резерв)";
            expected = estimate.Amount;
            limit = ExactInput ? expected * (10000 - bps) / 10000 : expected * (10000 + bps) / 10000;
            data = ExactInput ? new SellCoinData(Sell.Coin.Id, value, target.Id, limit) : new BuyCoinData(target.Id, value, Sell.Coin.Id, limit);
        }
        var expectedText = $"{Display.Amount(expected, 6)} {(ExactInput ? target.Symbol : Sell.Coin.Symbol)}";
        var limitText = ExactInput ? $"не меньше {Units.FormatCoins(limit)} {target.Symbol}" : $"не больше {Units.FormatCoins(limit)} {Sell.Coin.Symbol}";
        planned = (data, route, expectedText, limitText);
        foreach (var name in new[] { nameof(HasQuote), nameof(QuoteRoute), nameof(QuoteExpectedLabel), nameof(QuoteExpected), nameof(QuoteLimit) }) Changed(name);
        SwapNow.Refresh();
        Say(warning, Tone.Warning);
    }

    private async Task SwapAsync()
    {
        if (planned is not { } p) return;
        quoteTimer.Stop();
        var outcome = await main.ConfirmAndSendAsync("Обмен", p.Data, 0, [],
            [("Маршрут", p.Route), ("Оценка", $"≈ {p.Expected}"), ("Гарантия", p.Limit)], [], this);
        if (outcome?.Succeeded == true) { amount = ""; Changed(nameof(Amount)); ClearQuote(); }
        else if (outcome != null) ClearQuote();
    }
}

public sealed record CandidateItem(CandidateInfo Info, string Base)
{
    public string Key => Info.PublicKey.ToString();
    public string Short => Core.History.Short(Info.PublicKey);
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
    private string amount = "", filter = "", count = "";

    public StakingViewModel(MainViewModel main)
    {
        this.main = main;
        Load = new AsyncCommand(LoadAsync, Fail);
        DelegateNow = new AsyncCommand(() => StakeAsync(true), Fail);
        Unbond = new AsyncCommand(() => StakeAsync(false), Fail);
    }

    public ObservableCollection<CandidateItem> Candidates { get; } = [];
    public ObservableCollection<BalanceItem> Coins { get; } = [];
    public CandidateItem? Candidate { get => candidate; set { Set(ref candidate, value); Changed(nameof(CandidateTitle)); Changed(nameof(CandidateCaption)); } }
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
    }

    public void Reset() { all.Clear(); Candidates.Clear(); Candidate = null; Count = ""; }

    private void ApplyFilter()
    {
        var keep = Candidate;
        var text = Filter.Trim();
        Candidates.Clear();
        foreach (var c in all.Where(c => text.Length == 0 || c.Key.Contains(text, StringComparison.OrdinalIgnoreCase))) Candidates.Add(c);
        if (keep != null && Candidates.Contains(keep)) Candidate = keep;
    }

    private async Task LoadAsync()
    {
        var wallet = await main.WalletAsync();
        var list = await wallet.Backend.CandidatesAsync();
        all.Clear();
        all.AddRange(list.OrderByDescending(c => c.Validator).ThenByDescending(c => c.TotalStake).Select(c => new CandidateItem(c, main.Symbol(0))));
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
        await main.ConfirmAndSendAsync(delegating ? "Делегирование" : "Вывод из стейка", data, 0, [],
            [("Валидатор", $"{Candidate.Key}\nкомиссия {Candidate.Commission}, {Candidate.State}"), ("Сумма", $"{Units.FormatCoins(value)} {Coin.Coin.Symbol} (id {Coin.Coin.Id})")], warnings, this);
    }
}

public sealed record HistoryItem(HistoryEntry Entry)
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

public sealed class HistoryViewModel : ScreenModel
{
    private readonly MainViewModel main;
    private int page = 1;
    private bool loading, loaded;

    public HistoryViewModel(MainViewModel main)
    {
        this.main = main;
        Load = new AsyncCommand(LoadAsync, e => { Loading = false; Fail(e); });
        Next = new Command(() => { page++; Load.Execute(null); }, () => Items.Count >= PageSize);
        Previous = new Command(() => { if (page > 1) { page--; Load.Execute(null); } }, () => page > 1);
        CopyHash = new RelayCommand(async hash => { if (hash is string h && main.Shell.CopyText != null) { await main.Shell.CopyText(h); Say("Хеш транзакции скопирован.", Tone.Success); } });
    }

    private const int PageSize = 30;
    public ObservableCollection<HistoryItem> Items { get; } = [];
    public string PageText => $"Страница {page}";
    public bool Loading { get => loading; set { Set(ref loading, value); Changed(nameof(IsEmpty)); } }
    public bool IsEmpty => loaded && !Loading && Items.Count == 0;
    public string EmptyText => page > 1 ? "На этой странице записей нет." : "Транзакций пока нет — или у источника выключен индекс транзакций.";
    public AsyncCommand Load { get; }
    public Command Next { get; }
    public Command Previous { get; }
    public RelayCommand CopyHash { get; }

    public void Reset()
    {
        page = 1; loaded = false;
        Items.Clear();
        Changed(nameof(PageText)); Changed(nameof(IsEmpty));
        Next.Refresh(); Previous.Refresh();
        if (main.IsHistory) Load.Execute(null);
    }

    private async Task LoadAsync()
    {
        if (main.Selected == null) return;
        Loading = true;
        var wallet = await main.WalletAsync();
        var me = main.Selected.Account.Address;
        var records = await wallet.Backend.HistoryAsync(me, page, PageSize);
        Items.Clear();
        foreach (var r in records) Items.Add(new HistoryItem(Core.History.Decode(r, me, main.Symbol)));
        loaded = true;
        Loading = false;
        Changed(nameof(PageText)); Changed(nameof(EmptyText));
        Next.Refresh(); Previous.Refresh();
        if (Tone == Tone.Error) Say("");
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
    private string gasCap, autoLock, newName = "", newUrl = "";
    private bool adding;

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
            Changed(); Changed(nameof(CanRemove));
            Reconnect.Execute(null);
        }
    }
    public bool CanRemove => !main.Shell.Settings.Source.IsDefaultGate && main.Shell.Settings.Sources.Count > 1;
    public bool Adding { get => adding; set => Set(ref adding, value); }
    public string NewName { get => newName; set => Set(ref newName, value); }
    public string NewUrl { get => newUrl; set => Set(ref newUrl, value); }
    public string GasCap { get => gasCap; set => Set(ref gasCap, value); }
    public string AutoLock { get => autoLock; set => Set(ref autoLock, value); }
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
        Changed(nameof(SourceIndex)); Changed(nameof(CanRemove));
    }

    // Drops everything read from the previous source and connects to the chosen one.
    private async Task ConnectAsync()
    {
        var source = main.Shell.Settings.Source;
        main.Disconnect();
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
        using var probe = new MinterWallet.Api.NodeApiClient(new Uri(url));
        var status = await probe.StatusAsync();
        var gate = await probe.IsGateAsync();
        settings.Sources.Add(new SourceSettings { Name = NewName.Trim().Length > 0 ? NewName.Trim() : uri.Host, Url = url, Kind = gate ? "gate" : "node", ExpectedNetwork = status.Network });
        settings.Save(main.Shell.DataDirectory);
        NewName = ""; NewUrl = ""; Adding = false;
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
