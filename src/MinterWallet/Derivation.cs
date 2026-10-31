using System.Collections.ObjectModel;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Threading;
using Minter.Protocol;
using MinterWallet.Api;
using MinterWallet.Core;

namespace MinterWallet;

public sealed record DerivationCard(WalletAccount Account, string Title, bool Current, string Balance, string Activity)
{
    public string Address => Core.History.Short(Account.Address);
    public string Path => Account.Path;
}

// One root in the sidebar; coordinates and public address observations belong to that root. Viewing a coordinate
// does not add an account. Only a name, an issued payment address or detected activity makes it a tracked address.
public sealed class DerivationViewModel : ScreenModel
{
    private readonly MainViewModel main;
    private AccountItem? wallet;
    private bool reloading, collapsed, parameters, renaming, acknowledged, backedUp, loading, initialScanNext;
    private string renameTo = "", fullPath = "", scanText = "Адреса ещё не проверены", summary = "";
    private string checkedAt = "";
    private uint page;
    private int pageSize;
    private CancellationTokenSource scope = new();
    private readonly Dictionary<string, Observation> observations = [];
    private readonly HashSet<string> automaticScans = [];
    private string? approvedPath;

    public DerivationViewModel(MainViewModel main)
    {
        this.main = main;
        collapsed = main.Shell.Settings.DerivationCollapsed;
        pageSize = main.Shell.Settings.DerivationPageSize == 10 ? 10 : 5;
        Pick = new RelayCommand(p => { if (p is DerivationCard card && Cards.Contains(card) && card.Account.SecretId == Root && !Parameters) Select(card.Account); });
        Toggle = new Command(() => { Collapsed = !Collapsed; Parameters = false; });
        Edit = new Command(() => { Parameters = !Parameters; });
        Apply = new Command(ApplyFields);
        ApplyFullPath = new Command(() => ApplyPath(FullPath));
        Reset = new Command(() => ApplyPath(HdPath.Minter().Text));
        Previous = new Command(() => MovePage(false), () => page > 0 && !Parameters);
        Next = new Command(() => MovePage(true), () => (ulong)page + (uint)pageSize <= HdPath.MaxIndex && !Parameters);
        Rename = new Command(() => { RenameTo = PersonalName(Current); Renaming = !Renaming; });
        SaveName = new Command(() => { try { SaveNameNow(); } catch (Exception e) { Say(e.Message, Tone.Error); } });
        CancelName = new Command(() => Renaming = false);
        Scan = new AsyncCommand(ScanAsync, e => Say(Describe(e), Tone.Warning));
        CancelScan = new Command(Cancel);
        Export = new AsyncCommand(ExportAsync, Fail);
        Import = new AsyncCommand(ImportAsync, e => Say(e.Message, Tone.Error));
        ConfirmRisk = new Command(() => { if (BackedUp && Acknowledged && Current != null) { approvedPath = Current.Path; NotifySafety(); } });
        ChooseSource = new Command(() => main.Section = Section.Settings);
    }

    private Shell Shell => main.Shell;
    private WalletAccount? Current => main.Selected?.Account;
    private string Root => wallet?.Account.SecretId ?? wallet?.Account.Id ?? "";
    private string ScopeKey => Root + "|" + Shell.ActiveSource.Url + "|" + (Shell.Status?.Network ?? Shell.ActiveSource.ExpectedNetwork);
    private string Key(string path) => ScopeKey + "|" + path;
    private sealed record Observation(WalletAccount Account, AccountState State, IReadOnlyList<FrozenFunds> Frozen, bool Used, bool EmptyKnown);

    public ObservableCollection<AccountItem> Wallets { get; } = [];
    public ObservableCollection<DerivationCard> Cards { get; } = [];
    public ObservableCollection<DerivationField> Fields { get; } = [];
    public AccountItem? SelectedWallet
    {
        get => wallet;
        set
        {
            if (!Set(ref wallet, value) || reloading || value == null) return;
            Cancel(); observations.Clear(); automaticScans.Clear(); approvedPath = null; BackedUp = false; Acknowledged = false;
            Parameters = false; Renaming = false; ScanText = "Адреса ещё не проверены"; Summary = ""; CheckedAt = "";
            var path = Shell.Settings.WalletCoordinates.GetValueOrDefault(Root);
            WalletAccount address = value.Account;
            if (value.Phrase && path != null)
                try { address = Shell.Session!.PreviewAddress(Root, path); }
                catch (FormatException) { Say("Сохранённый путь не распознан. Выбран прежний адрес.", Tone.Warning); }
            Select(address);
        }
    }
    public bool IsHd => wallet?.Phrase == true;
    public string NetworkName => Shell.Status?.Network ?? Shell.ActiveSource.ExpectedNetwork ?? "Сеть ещё не определена";
    public string KindExplanation => wallet?.Account.WatchOnly == true ? "Адрес только для просмотра: ключей в этом кошельке нет."
        : "Приватный ключ задаёт один адрес. Переключение дочерних адресов доступно кошелькам из фразы восстановления.";
    public bool Collapsed { get => collapsed; set { if (Set(ref collapsed, value)) { Changed(nameof(Expanded)); Changed(nameof(ToggleLabel)); Shell.Settings.DerivationCollapsed = value; SaveSettings(); } } }
    public bool Expanded => !Collapsed;
    public string ToggleLabel => Collapsed ? "Развернуть" : "Свернуть";
    public bool Parameters
    {
        get => parameters;
        set
        {
            if (!Set(ref parameters, value)) return;
            if (value) LoadFields();
            NotifySafety(); Previous.Refresh(); Next.Refresh();
        }
    }
    public string FullPath { get => fullPath; set => Set(ref fullPath, value); }
    public bool Renaming { get => renaming; private set => Set(ref renaming, value); }
    public string RenameTo { get => renameTo; set => Set(ref renameTo, value); }
    public string CurrentName => Current == null ? "" : IsHd ? (PersonalName(Current) is { Length: > 0 } n ? n : $"Адрес № {Current.Index + 1}") : wallet?.Name ?? "";
    public string CurrentPath => IsHd ? Current?.Path ?? "" : "";
    public string Range => $"Адреса {page + 1}–{Math.Min((ulong)HdPath.MaxIndex + 1, (ulong)page + (uint)pageSize)}";
    public int PageSizeIndex { get => pageSize == 10 ? 1 : 0; set { var size = value == 1 ? 10 : 5; if (pageSize == size) return; pageSize = size; Shell.Settings.DerivationPageSize = size; SaveSettings(); ShowPage(); } }
    public string ScanText { get => scanText; private set => Set(ref scanText, value); }
    public string Summary { get => summary; private set => Set(ref summary, value); }
    public string CheckedAt { get => checkedAt; private set => Set(ref checkedAt, value); }
    public bool Loading { get => loading; private set => Set(ref loading, value); }
    public bool Risk => IsHd && Current != null && !DerivationSafety.Recoverable(HdPath.Parse(Current.Path), UsedPaths());
    public bool NeedsProtection => Risk && approvedPath != Current?.Path;
    public bool CanReceive => Current != null && !Parameters && !NeedsProtection;
    public bool BackedUp { get => backedUp; private set => Set(ref backedUp, value); }
    public bool Acknowledged { get => acknowledged; set => Set(ref acknowledged, value); }
    public string RiskText => "Этот путь может быть пропущен при обычном восстановлении из фразы: другая группа, нестандартные параметры или большой промежуток адресов. Сначала сохраните профиль путей отдельно от компьютера и подтвердите именно этот адрес. Профиль не заменяет фразу восстановления.";
    public RelayCommand Pick { get; }
    public Command Toggle { get; }
    public Command Edit { get; }
    public Command Apply { get; }
    public Command ApplyFullPath { get; }
    public Command Reset { get; }
    public Command Previous { get; }
    public Command Next { get; }
    public Command Rename { get; }
    public Command SaveName { get; }
    public Command CancelName { get; }
    public AsyncCommand Scan { get; }
    public Command CancelScan { get; }
    public AsyncCommand Export { get; }
    public AsyncCommand Import { get; }
    public Command ConfirmRisk { get; }
    public Command ChooseSource { get; }

    public void Reload(string? select)
    {
        reloading = true;
        Wallets.Clear();
        foreach (var group in main.Accounts.GroupBy(a => a.Account.SecretId ?? a.Account.Id)) Wallets.Add(group.First());
        wallet = null;
        reloading = false;
        var item = main.Accounts.FirstOrDefault(a => a.Account.Id == select);
        SelectedWallet = Wallets.FirstOrDefault(w => (w.Account.SecretId ?? w.Account.Id) == (item?.Account.SecretId ?? item?.Account.Id ?? Shell.Settings.LastWalletId)) ?? Wallets.FirstOrDefault();
        if (item != null && Current?.Id != item.Account.Id) Select(item.Account);
        Changed(nameof(SelectedWallet));
    }

    private void Select(WalletAccount account)
    {
        var root = Wallets.FirstOrDefault(a => (a.Account.SecretId ?? a.Account.Id) == (account.SecretId ?? account.Id));
        if (root == null) return;
        Parameters = false;
        var existing = main.Accounts.FirstOrDefault(a => a.Account.Id == account.Id);
        main.Selected = existing ?? new AccountItem(account, root.Kind, root.Glyph, root.Group, root.Phrase);
        if (IsHd) page = account.Index / (uint)pageSize * (uint)pageSize;
        ShowPage();
    }

    public void SelectedChanged()
    {
        if (Current == null) return;
        var root = Wallets.FirstOrDefault(a => (a.Account.SecretId ?? a.Account.Id) == (Current.SecretId ?? Current.Id));
        if (root != null && root != wallet) { Cancel(); wallet = root; Changed(nameof(SelectedWallet)); observations.Clear(); automaticScans.Clear(); }
        Renaming = false; parameters = false; Changed(nameof(Parameters));
        approvedPath = null; BackedUp = false; Acknowledged = false;
        if (IsHd) Shell.Settings.WalletCoordinates[Root] = Current.Path;
        Shell.Settings.LastWalletId = Root;
        SaveSettings();
        foreach (var n in new[] { nameof(IsHd), nameof(KindExplanation), nameof(CurrentName), nameof(CurrentPath), nameof(ToggleLabel) }) Changed(n);
        NotifySafety();
    }

    private static string PersonalName(WalletAccount? address) => address == null || address.Name.StartsWith("Адрес № ", StringComparison.Ordinal) ? "" : address.Name;
    private void SaveSettings() => Shell.Settings.Save(Shell.DataDirectory);
    private IEnumerable<string> UsedPaths() => observations.Where(p => p.Key.StartsWith(ScopeKey + "|", StringComparison.Ordinal) && p.Value.Used).Select(p => p.Value.Account.Path);

    private void MovePage(bool next)
    {
        if (Parameters || !IsHd) return;
        page = next ? (uint)Math.Min(HdPath.MaxIndex, (ulong)page + (uint)pageSize) : page > pageSize ? page - (uint)pageSize : 0;
        ShowPage();
    }
    private void ShowPage()
    {
        var newCards = new List<DerivationCard>();
        if (IsHd && Current != null && Shell.Session is { IsLocked: false } session)
        {
            var count = (int)Math.Min((ulong)pageSize, (ulong)HdPath.MaxIndex - page + 1);
            foreach (var a in session.PreviewAddresses(Root, Current.Path, page, count))
            {
                var seen = observations.GetValueOrDefault(Key(a.Path));
                newCards.Add(new(a, PersonalName(a) is { Length: > 0 } name ? name : $"Адрес № {a.Index + 1}", a.Path == Current.Path,
                    seen == null ? "—" : $"{Display.Amount(seen.State.TotalBipValue)} {main.BaseSymbol}",
                    seen == null ? "не проверен" : seen.Used ? "используется" : seen.EmptyKnown ? "не использован" : "история неполная"));
            }
        }
        MainViewModel.SyncList(Cards, newCards);
        Changed(nameof(Range)); Changed(nameof(ToggleLabel)); Previous.Refresh(); Next.Refresh();
    }

    private void LoadFields()
    {
        Fields.Clear();
        if (Current == null || !IsHd) return;
        var path = HdPath.Parse(Current.Path);
        FullPath = path.Text;
        var labels = new[] { "Назначение", "Профиль монеты", "Группа", "Ветка", "Индекс" };
        var tips = new[] {
            "purpose: 44 — стандарт BIP44. Изменение создаёт другой набор адресов; для обычного Minter оставьте 44 и усиление (′).",
            "coin_type: 60 — совместимый профиль Minter/Ethereum. Это не id монеты BIP или токена. Профиль меняет адреса, но не сеть подключения.",
            "account: отдельная группа адресов одной фразы. Восстановление BIP44 останавливается на первой неиспользованной группе. Не пропускайте пустые группы.",
            "change: обычно 0, внешние адреса. 1 — внутренняя ветка. В Minter нет автоматической сдачи как в Bitcoin; обе ветки дают обычные Mx-адреса.",
            "address_index: нумерация с нуля. Индекс 0 — карточка № 1. Обычное восстановление прекращает поиск после 20 подряд неиспользованных адресов." };
        for (var i = 0; i < path.Depth; i++) Fields.Add(new(i < 5 && path.Depth == 5 ? labels[i] : $"Уровень {i + 1}",
            i < 5 && path.Depth == 5 ? tips[i] : "Уровень произвольного пути BIP32. Изменение любого уровня даёт другие ключи и адреса.", path.ValueAt(i), path.HardenedAt(i)));
    }
    private void ApplyFields()
    {
        try
        {
            var path = "m/" + string.Join('/', Fields.Select(f => f.Value.Trim() + (f.Hardened ? "'" : "")));
            ApplyPath(path);
        }
        catch (FormatException e) { Say(e.Message, Tone.Error); }
    }
    private void ApplyPath(string path)
    {
        if (!IsHd) return;
        try { var address = Shell.Session!.PreviewAddress(Root, HdPath.Parse(path).Text); Select(address); Say(""); }
        catch (FormatException e) { Say(e.Message, Tone.Error); }
    }
    private void SaveNameNow()
    {
        if (Current == null) return;
        if (RenameTo.Trim().Length > 64) { Say("Название — не больше 64 символов.", Tone.Error); return; }
        var a = Current;
        if (IsHd) a = main.TrackAddress(a);
        Shell.Session!.Rename(a.Id, RenameTo.Trim().Length == 0 ? $"Адрес № {a.Index + 1}" : RenameTo.Trim());
        main.LoadAccounts(a.Id);
        Say("Название сохранено только в этом кошельке. Адрес и ключи не изменились.", Tone.Success);
    }

    public void NotifySafety()
    {
        foreach (var n in new[] { nameof(Risk), nameof(NeedsProtection), nameof(CanReceive) }) Changed(n);
        main.ReceiveSafetyChanged();
    }
    public bool PrepareReceive()
    {
        if (!CanReceive) { Say(Parameters ? "Примените параметры или закройте редактор пути." : RiskText, Tone.Warning); return false; }
        if (IsHd && Current != null) main.TrackAddress(Current);
        return true;
    }
    public void Cancel()
    {
        scope.Cancel(); scope.Dispose(); scope = new();
        Loading = false;
    }
    public void SourceChanged()
    {
        Cancel(); observations.Clear(); automaticScans.Clear();
        approvedPath = null; BackedUp = false; Acknowledged = false;
        ScanText = "Источник изменён: адреса ещё не проверены"; Summary = "";
        CheckedAt = "";
        ShowPage(); NotifySafety();
    }

    // Reading the selected address is also evidence of activity. Empty balances alone never prove an unused address.
    public void Observe(WalletAccount account, AccountState state, IReadOnlyList<FrozenFunds> frozen)
    {
        if (!IsHd || account.SecretId != Root) return;
        Changed(nameof(NetworkName));
        var used = state.Nonce > 0 || state.Balances.Any(b => b.Value > 0) || state.Delegated.Any(d => d.Value > 0) || frozen.Any(f => f.Value > 0);
        if (used) observations[Key(account.Path)] = new(account, state, frozen, true, false);
        UpdateSummary();
        ShowPage(); NotifySafety();
        StartInitialScan();
    }
    private void StartInitialScan()
    {
        if (!IsHd || main.State == null || Shell.Session?.IsLocked != false || !Scan.CanExecute(null) || automaticScans.Contains(ScopeKey)) return;
        automaticScans.Add(ScopeKey);
        initialScanNext = true;
        Scan.Execute(null);
    }

    private async Task<Observation> ReadAsync(WalletAccount a, AccountState state, IChainBackend backend, bool completeHistory, CancellationToken ct)
    {
        var frozen = await backend.FrozenAsync(a.Address, ct);
        var used = state.Nonce > 0 || state.Balances.Any(b => b.Value > 0) || state.Delegated.Any(d => d.Value > 0) || frozen.Any(f => f.Value > 0);
        if (!used) used = (await backend.HistoryAsync(a.Address, 1, 1, ct)).Count > 0;
        if (!used && !completeHistory && Shell.Settings.HistoryFromExplorer && Shell.Status?.Network.StartsWith("minter-mainnet", StringComparison.Ordinal) == true)
        {
            var older = await Shell.Explorer.HistoryAsync(a.Address, 1, ct);
            used = older.Records.Count > 0;
            completeHistory = true;
        }
        // A gate may index only recent blocks. Its empty history cannot prove that recovery should stop here.
        return new(a, state, frozen, used, !used && completeHistory);
    }

    private async Task ScanAsync()
    {
        var initial = initialScanNext;
        initialScanNext = false;
        if (!IsHd) return;
        var stamp = ScopeKey; var token = scope.Token; var root = Root;
        Loading = true;
        var checkedCount = 0; var incomplete = false;
        var checkedPaths = new HashSet<string>();
        try
        {
            using var _ = SourceRequestHandler.EnterBackground();
            var service = await main.WalletAsync();
            if (stamp != ScopeKey || token.IsCancellationRequested) return;
            var backend = service.Backend;
            var complete = Shell.GateIndexFrom <= 1;
            async Task CheckBatch(IReadOnlyList<WalletAccount> batch)
            {
                var pending = batch.Where(a => !checkedPaths.Contains(a.Path)).ToList();
                if (pending.Count == 0) return;
                var toFetch = pending.Where(a => !initial || !observations.TryGetValue(Key(a.Path), out var o) || !o.Used).ToList();
                var states = toFetch.Count > 0 ? await backend.AccountsAsync(toFetch.Select(a => a.Address).ToList(), token) : new Dictionary<Address, AccountState>();
                // At most five reads at once; snapshot/root cancellation prevents publishing another wallet's data.
                var found = await Task.WhenAll(pending.Select(a =>
                    initial && observations.TryGetValue(Key(a.Path), out var kept) && kept.Used
                        ? Task.FromResult(kept)
                        : ReadAsync(a, states[a.Address], backend, complete, token)));
                token.ThrowIfCancellationRequested();
                if (stamp != ScopeKey) throw new OperationCanceledException();
                foreach (var item in found)
                {
                    observations[Key(item.Account.Path)] = item;
                    checkedPaths.Add(item.Account.Path);
                    checkedCount++;
                    if (item.Used) main.TrackAddress(item.Account);
                }
                ScanText = $"Проверено адресов: {checkedCount}";
                CheckedAt = $"Последняя проверка: {DateTime.Now:HH:mm:ss}";
                UpdateSummary(); ShowPage(); NotifySafety();
            }
            // Explicit saved/issued coordinates come first, including nonstandard branches and distant indices.
            var saved = Shell.Store!.Accounts.Where(a => a.SecretId == root).ToList();
            foreach (var batch in saved.Chunk(20)) await CheckBatch(batch);
            for (uint group = 0; group < 10; group++)
            {
                uint gap = 0; bool accountUsed = false; uint unconfirmed = 0;
                for (uint index = 0; index < 1000; index += 20)
                {
                    var batch = Shell.Session!.PreviewAddresses(root, HdPath.Minter(0, group).Text, index, 20);
                    await CheckBatch(batch);
                    foreach (var address in batch)
                    {
                        var o = observations[Key(address.Path)];
                        if (!o.Used && !o.EmptyKnown) { incomplete = true; gap = 0; unconfirmed++; }
                        else if (o.Used) { gap = 0; unconfirmed = 0; accountUsed = true; }
                        else { gap++; unconfirmed = 0; }
                        if (gap >= DerivationSafety.Gap || unconfirmed >= DerivationSafety.Gap) break;
                    }
                    if (gap >= DerivationSafety.Gap || unconfirmed >= DerivationSafety.Gap) break;
                    if (index == 980) incomplete = true;
                }
                if (incomplete || !accountUsed) break;
                if (group == 9) incomplete = true;
            }
            ScanText = incomplete ? $"Проверено {checkedCount}. Поиск неполный: источник не подтверждает старую историю или достигнут предел поиска."
                : $"Проверено {checkedCount}. Базовый путь: поиск до 20 пустых адресов и первой пустой группы.";
        }
        catch (OperationCanceledException) { if (stamp == ScopeKey && !Loading) ScanText = $"Поиск остановлен. Проверено: {checkedCount}."; }
        catch (Exception e) { if (stamp == ScopeKey) { ScanText = $"Поиск неполный. Проверено: {checkedCount}."; Say(Describe(e), Tone.Warning); } }
        finally
        {
            if (stamp == ScopeKey && !token.IsCancellationRequested) { Loading = false; UpdateSummary(); }
            else Dispatcher.UIThread.Post(StartInitialScan);
        }
    }
    private void UpdateSummary()
    {
        var items = observations.Where(p => p.Key.StartsWith(ScopeKey + "|", StringComparison.Ordinal)).Select(p => p.Value).ToList();
        if (items.Count == 0) { Summary = "Баланс адресов ещё не получен. Запустите поиск в параметрах деривации."; return; }
        static string Sum(IEnumerable<(CoinRef Coin, BigInteger Value)> amounts)
        {
            var coins = amounts.Where(b => b.Value > 0).GroupBy(b => b.Coin.Id)
                .Select(g => $"{Display.Amount(g.Aggregate(BigInteger.Zero, (sum, b) => sum + b.Value))} {g.First().Coin.Symbol} (id {g.Key})").ToList();
            return coins.Count == 0 ? "нет" : string.Join(" · ", coins);
        }
        var available = Sum(items.SelectMany(o => o.State.Balances).Select(b => (b.Coin, b.Value)));
        var stakes = Sum(items.SelectMany(o => o.State.Delegated).Select(b => (b.Coin, b.Value)));
        var frozen = Sum(items.SelectMany(o => o.Frozen).Select(b => (b.Coin, b.Value)));
        Summary = $"По проверенным адресам ({items.Count}). Доступно: {available}. В стейкинге: {stakes}. Заморожено: {frozen}.";
    }

    private async Task ExportAsync()
    {
        if (!IsHd || Current == null || Shell.SaveText == null) return;
        var address = Current; var root = Root; var stamp = ScopeKey; var token = scope.Token;
        var anchor = Shell.Session!.PreviewAddress(root, HdPath.Minter().Text).Address.ToString();
        var paths = Shell.Store!.Accounts.Where(a => a.SecretId == root).Append(address).GroupBy(a => a.Path).Select(g => g.First())
            .Select(a => new DerivationProfileAddress(a.Path, a.Address.ToString(), a.Name)).ToList();
        var profile = new DerivationProfile(1, anchor, Shell.Status?.Network ?? Shell.ActiveSource.ExpectedNetwork ?? "не определена", paths);
        var text = JsonSerializer.Serialize(profile, DerivationJson.Default.DerivationProfile);
        if (paths.Count > 1000 || Encoding.UTF8.GetByteCount(text) + Encoding.UTF8.GetPreamble().Length > 1024 * 1024)
            throw new InvalidDataException("Профиль превышает ограничение: 1000 адресов или 1 МБ. Получение по нестандартному пути остаётся закрытым.");
        var saved = await Shell.SaveText($"minter-paths-{anchor[2..10]}-{anchor[^8..]}.json", text);
        if (!saved || token.IsCancellationRequested || Shell.Session?.IsLocked != false || stamp != ScopeKey || Current?.Path != address.Path) return;
        main.TrackAddress(address);
        BackedUp = true;
        Say("Профиль сохранён: пути, адреса и названия, без фразы и приватных ключей. Храните его приватно и отдельно от кошелька.", Tone.Success);
    }
    private async Task ImportAsync()
    {
        if (!IsHd || Shell.OpenText == null) return;
        var root = Root; var stamp = ScopeKey; var token = scope.Token;
        var text = await Shell.OpenText();
        if (text == null || token.IsCancellationRequested || Shell.Session?.IsLocked != false || stamp != ScopeKey) return;
        var profile = JsonSerializer.Deserialize(text.TrimStart('\uFEFF'), DerivationJson.Default.DerivationProfile) ?? throw new FormatException("Пустой профиль.");
        if (profile.Version != 1 || profile.Addresses == null || profile.Addresses.Count is < 1 or > 1000) throw new FormatException("Неизвестный или слишком большой профиль.");
        var entries = profile.Addresses.Select(p =>
        {
            if (p == null || p.Name == null || p.Name.Length > 64 || p.Path == null || p.Address == null) throw new FormatException("Слишком длинное название в профиле.");
            return (p.Path, p.Address, p.Name);
        }).ToList();
        var verified = await Shell.Session!.VerifyProfileAsync(root, profile.RootAddress ?? "", entries);
        if (token.IsCancellationRequested || Shell.Session?.IsLocked != false || stamp != ScopeKey) return;
        // Verify every entry before any write; one atomic keystore update persists the entire profile.
        Shell.Session!.ImportAddresses(verified);
        var keep = Current?.Id;
        main.LoadAccounts(keep);
        Say("Пути и названия восстановлены. Для проверки средств запустите поиск адресов.", Tone.Success);
    }
}

public sealed class DerivationField(string label, string help, uint value, bool hardened) : Observable
{
    private string text = value.ToString(CultureInfo.InvariantCulture);
    private bool hard = hardened;
    public string Label { get; } = label;
    public string Help { get; } = help;
    public string Value { get => text; set => Set(ref text, value); }
    public bool Hardened { get => hard; set => Set(ref hard, value); }
}
public sealed record DerivationProfileAddress(string Path, string Address, string Name);
public sealed record DerivationProfile(int Version, string RootAddress, string Network, List<DerivationProfileAddress> Addresses);
[JsonSerializable(typeof(DerivationProfile))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class DerivationJson : JsonSerializerContext;
