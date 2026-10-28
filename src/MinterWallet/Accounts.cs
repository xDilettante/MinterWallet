using System.Collections.ObjectModel;
using Minter.Protocol;
using MinterWallet.Core;

namespace MinterWallet;

public enum AddKind { NewPhrase, ImportPhrase, ImportKey, Watch, Vanity }

// An address of a phrase by its number (index + 1): in the wallet already, used elsewhere, or never used.
public sealed record DerivedItem(uint Index, Address Address, string State, bool InWallet)
{
    public string Number => $"№ {Index + 1}";
    public string AddressText => Address.ToString();
    public bool CanAdd => !InWallet;
}

// An account of the wallet as a card: a phrase with its addresses (№ 1, № 2 …), a key, or a watched address. The
// account of the current address is active: its card shows what can be done with it.
public sealed record AddressRow(AccountItem Item, bool IsCurrent)
{
    public string Title => Item.RowTitle;
    public string AddressText => Item.AddressText;
}

public sealed record AccountGroup(string? SecretId, string Name, string Kind, string Glyph, IReadOnlyList<AddressRow> Addresses, bool IsPhrase, bool IsActive)
{
    public string Caption => IsPhrase ? $"{Kind} · адресов: {Addresses.Count}" : Kind;
    public bool IsInactive => !IsActive;
}

// The accounts of the wallet and their addresses. A phrase is one account with any number of addresses — all of them
// restored by the same phrase — so the addresses are managed inside its card: the next one, one by its number (an
// address used in another wallet), names, the phrase itself. New accounts: a new phrase, an existing phrase, a key, an
// address to watch, a vanity address.
public sealed class AccountsViewModel : ScreenModel
{
    private readonly MainViewModel main;
    private int addIndex;
    private string name = "", secret = "", renameTo = "", groupRenameTo = "", newPhrase = "";
    private IReadOnlyList<PhraseWord> newWords = [], shownWords = [];
    private string? shownPhrase, derivedFor;
    private bool confirmingRemove, confirmingGroupRemove;
    private string contactName = "", contactAddress = "";
    private uint derivedNext;

    public AccountsViewModel(MainViewModel main)
    {
        this.main = main;
        Add = new Command(AddNow);
        Paste = new AsyncCommand(PasteAsync, Fail);
        Vanity = new VanityViewModel(main);
        Pick = new RelayCommand(p => { if (p is AccountItem item) main.Selected = main.Accounts.FirstOrDefault(a => a.Account.Id == item.Account.Id); });
        NextAddress = new Command(NextAddressNow);
        ShowDerived = new AsyncCommand(ShowDerivedAsync, Fail);
        AddDerived = new RelayCommand(p => { if (p is DerivedItem item) AddDerivedNow(item); });
        CopyNewPhrase = new AsyncCommand(() => Copy(newPhrase, "Фраза скопирована. Сохраните её там, где её никто не увидит."), Fail);
        Rename = new Command(RenameNow);
        RenameGroup = new Command(RenameGroupNow);
        Reveal = new AsyncCommand(RevealAsync, Fail);
        CopyPhrase = new AsyncCommand(() => Copy(shownPhrase ?? "", "Фраза скопирована."), Fail);
        HidePhrase = new Command(() => { shownPhrase = null; ShownWords = []; });
        CopyAddress = new AsyncCommand(() => Copy(main.Address, "Адрес скопирован."), Fail);
        Remove = new AsyncCommand(RemoveAsync, Fail);
        CancelRemove = new Command(() => { ConfirmingRemove = false; ConfirmingGroupRemove = false; });
        RemoveGroup = new AsyncCommand(RemoveGroupAsync, Fail);
        AddContact = new Command(AddContactNow);
        NewPhrase();   // «Новая фраза» is the first way to add an account: its words are ready
    }

    public ObservableCollection<AccountGroup> Groups { get; } = [];
    public RelayCommand Pick { get; }

    public string ContactName { get => contactName; set => Set(ref contactName, value); }
    public string ContactAddress { get => contactAddress; set => Set(ref contactAddress, value); }
    public Command AddContact { get; }

    // A name for an address: new or renamed (the same address again with another name).
    private void AddContactNow()
    {
        if (!Address.TryParse(ContactAddress.Trim(), out var address)) { Say("Адрес — Mx и 40 шестнадцатеричных символов.", Tone.Error); return; }
        if (ContactName.Trim().Length == 0) { Say("Укажите имя.", Tone.Error); return; }
        main.Shell.AddressBook.Remember(address, ContactName.Trim());
        main.LoadContacts();
        Say($"«{ContactName.Trim()}» в адресной книге.", Tone.Success);
        ContactName = ""; ContactAddress = "";
    }

    public int AddIndex
    {
        get => addIndex;
        set
        {
            if (value < 0 || !Set(ref addIndex, value)) return;
            Secret = ""; Say("");
            if (Kind == AddKind.NewPhrase && newPhrase.Length == 0) NewPhrase();
            foreach (var n in new[] { nameof(IsNewPhrase), nameof(IsImport), nameof(IsKey), nameof(IsPlainSecret), nameof(IsVanity), nameof(IsPlainAdd), nameof(SecretLabel), nameof(SecretPlaceholder), nameof(SecretHeight), nameof(AddLabel) }) Changed(n);
        }
    }
    public AddKind Kind => (AddKind)addIndex;
    public bool IsNewPhrase => Kind == AddKind.NewPhrase;
    public bool IsImport => Kind is AddKind.ImportPhrase or AddKind.ImportKey or AddKind.Watch;
    public bool IsVanity => Kind == AddKind.Vanity;
    public bool IsPlainAdd => Kind != AddKind.Vanity;
    public VanityViewModel Vanity { get; }
    public bool IsKey => Kind == AddKind.ImportKey;
    public bool IsPlainSecret => Kind is AddKind.ImportPhrase or AddKind.Watch;
    private ImportKind ImportAs => Kind switch { AddKind.ImportKey => ImportKind.PrivateKey, AddKind.Watch => ImportKind.WatchOnly, _ => ImportKind.Mnemonic };
    public string SecretLabel => Kind switch { AddKind.ImportKey => "Приватный ключ", AddKind.Watch => "Адрес", _ => "Фраза восстановления" };
    public string SecretPlaceholder => Kind switch { AddKind.ImportKey => "64 шестнадцатеричных символа", AddKind.Watch => "Mx…", _ => "word word word …" };
    public double SecretHeight => Kind == AddKind.ImportPhrase ? 84 : 0;
    public string AddLabel => Kind == AddKind.NewPhrase ? "Добавить счёт с этой фразой" : "Добавить";

    // The active account: the current address's.
    public AccountGroup? Active => Groups.FirstOrDefault(g => g.IsActive);
    public bool ActiveIsPhrase => Active?.IsPhrase == true;

    // The phrase's addresses by number, ten at a time, with what each is: so an address used in another wallet can be
    // added back by its number.
    public ObservableCollection<DerivedItem> Derived { get; } = [];
    public bool HasDerived => Derived.Count > 0;
    public string DerivedLabel => Derived.Count == 0 ? "Адреса фразы по номеру" : "Ещё 10";
    public string Name { get => name; set => Set(ref name, value); }
    public string Secret { get => secret; set { Set(ref secret, value); Changed(nameof(SecretCheck)); Changed(nameof(SecretTone)); if (Tone == Tone.Error) Say(""); } }
    public string SecretCheck => Secrets.Check(ImportAs, Secret);
    public Tone SecretTone => Secrets.ToneOf(ImportAs, Secret);
    public IReadOnlyList<PhraseWord> NewWords { get => newWords; private set => Set(ref newWords, value); }

    // The current address's own name, and the account's.
    public string RenameTo { get => renameTo; set => Set(ref renameTo, value); }
    public string GroupRenameTo { get => groupRenameTo; set => Set(ref groupRenameTo, value); }
    public string CurrentTitle => main.Selected is not { } s ? "" : s.Account.WatchOnly ? "Адрес для просмотра" : s.Phrase ? $"Адрес {s.Number}" : "Адрес ключа";
    public IReadOnlyList<PhraseWord> ShownWords { get => shownWords; private set { Set(ref shownWords, value); Changed(nameof(PhraseShown)); } }
    public bool PhraseShown => ShownWords.Count > 0;
    public bool SelectedHasPhrase => main.Selected?.Account is { SecretId: not null } a && main.Shell.Store!.KindOf(a) == SecretKind.Mnemonic;
    public string SelectedKind => main.Selected?.Account is not { } a ? "" : a.WatchOnly ? "Только просмотр: ключа в кошельке нет, подписать нельзя."
        : main.Shell.Store!.KindOf(a) == SecretKind.Mnemonic ? $"Адрес № {a.Index + 1} фразы этого счёта. Он восстанавливается той же фразой, отдельно записывать ничего не нужно."
        : "Добавлен приватным ключом: восстанавливается только этим ключом.";
    public bool ConfirmingRemove { get => confirmingRemove; set => Set(ref confirmingRemove, value); }
    public bool ConfirmingGroupRemove { get => confirmingGroupRemove; set => Set(ref confirmingGroupRemove, value); }
    public bool CanRemoveAddress => main.Selected?.Phrase == true && main.Shell.Store!.Accounts.Count(x => x.SecretId == main.Selected.Account.SecretId) > 1;
    public string RemoveLabel => CanRemoveAddress ? "Убрать этот адрес из счёта" : "Удалить этот счёт из кошелька";
    public string RemoveWarning => main.Selected?.Account is not { } a ? "" : a.WatchOnly ? "Адрес только для просмотра просто исчезнет из списка."
        : CanRemoveAddress ? "Адрес исчезнет из списка; фраза останется для других адресов, и его можно будет вернуть по номеру."
        : "Ключ этого счёта удалится из файла. Без сохранённой фразы или ключа доступ к средствам на нём не вернуть.";
    public string GroupRemoveWarning => Active is not { } g ? "" : g.IsPhrase
        ? $"Фраза и все её адреса ({g.Addresses.Count}) удалятся из кошелька. Без записанной фразы доступ к средствам на них не вернуть."
        : RemoveWarning;

    public Command Add { get; }
    public AsyncCommand Paste { get; }
    public AsyncCommand CopyNewPhrase { get; }
    public Command NextAddress { get; }
    public AsyncCommand ShowDerived { get; }
    public RelayCommand AddDerived { get; }
    public Command Rename { get; }
    public Command RenameGroup { get; }
    public AsyncCommand Reveal { get; }
    public AsyncCommand CopyPhrase { get; }
    public Command HidePhrase { get; }
    public AsyncCommand CopyAddress { get; }
    public AsyncCommand Remove { get; }
    public Command CancelRemove { get; }
    public AsyncCommand RemoveGroup { get; }

    // Called when the list of addresses or the current one changes.
    public void Reload()
    {
        var store = main.Shell.Store!;
        Groups.Clear();
        foreach (var group in main.Accounts.GroupBy(a => a.Account.SecretId ?? a.Account.Id))
        {
            var items = group.ToList();
            var first = items[0];
            var active = items.Any(i => i.Account.Id == main.Selected?.Account.Id);
            Groups.Add(new AccountGroup(first.Account.SecretId, first.Group.Length > 0 ? first.Group : first.Name,
                first.Phrase ? "фраза восстановления" : first.Account.WatchOnly ? "только просмотр" : "приватный ключ", first.Glyph,
                items.Select(i => new AddressRow(i, i.Account.Id == main.Selected?.Account.Id)).ToList(), first.Phrase, active));
        }
        if (Active?.SecretId != derivedFor) { Derived.Clear(); derivedNext = 0; derivedFor = null; }
        if (Name.Length == 0 || Name.StartsWith("Счёт ", StringComparison.Ordinal)) Name = $"Счёт {Groups.Count + 1}";
        RenameTo = main.Selected?.Name ?? "";
        GroupRenameTo = Active?.Name ?? "";
        shownPhrase = null; ShownWords = []; ConfirmingRemove = false; ConfirmingGroupRemove = false;
        foreach (var n in new[] { nameof(Active), nameof(ActiveIsPhrase), nameof(HasDerived), nameof(DerivedLabel), nameof(CurrentTitle), nameof(SelectedHasPhrase), nameof(SelectedKind),
                     nameof(CanRemoveAddress), nameof(RemoveLabel), nameof(RemoveWarning), nameof(GroupRemoveWarning) }) Changed(n);
    }

    // The "+" of the sidebar: the accounts page, a new phrase ready below the accounts.
    public void StartAdding() => AddIndex = (int)AddKind.NewPhrase;

    private int newWordCountIndex;
    public int NewWordCountIndex { get => newWordCountIndex; set { if (value >= 0 && Set(ref newWordCountIndex, value)) NewPhrase(); } }

    private void NewPhrase()
    {
        newPhrase = Mnemonic.Generate(newWordCountIndex == 1 ? 24 : 12);
        NewWords = newPhrase.Split(' ').Select((w, i) => new PhraseWord(i + 1, w)).ToArray();
    }

    private async Task Copy(string text, string done)
    {
        if (text.Length == 0 || main.Shell.CopyText == null) return;
        await main.Shell.CopyText(text);
        Say(done, Tone.Success);
    }

    private async Task PasteAsync()
    {
        var text = main.Shell.PasteText == null ? null : await main.Shell.PasteText();
        if (string.IsNullOrWhiteSpace(text)) { Say("В буфере обмена нет текста.", Tone.Warning); return; }
        Secret = Secrets.Clean(ImportAs, text);
    }

    // The phrase's next number, added to its account and made current.
    private void NextAddressNow()
    {
        if (Active is not { IsPhrase: true, SecretId: { } secretId }) { Say("Новые адреса бывают только у фразы восстановления.", Tone.Warning); return; }
        var next = main.Shell.Store!.Accounts.Where(a => a.SecretId == secretId).Max(a => a.Index) + 1;
        var account = main.Shell.Session!.AddNextIndex(secretId, $"Адрес № {next + 1}");
        main.LoadAccounts(account.Id);
        Say($"Добавлен адрес № {account.Index + 1}: {Display.Address(account.Address)}. Он уже выбран.", Tone.Success);
    }

    private async Task ShowDerivedAsync()
    {
        if (Active is not { IsPhrase: true, SecretId: { } secretId }) { Say("Адреса по номеру бывают только у фразы восстановления.", Tone.Warning); return; }
        if (derivedFor != secretId) { Derived.Clear(); derivedNext = 0; derivedFor = secretId; }
        var addresses = main.Shell.Session!.AddressesOf(secretId, derivedNext, 10);
        var wallet = await main.WalletAsync();
        Say("Проверяю, какие адреса использовались…", Tone.Progress);
        for (var i = 0; i < addresses.Count; i++)
        {
            var a = addresses[i];
            var mine = main.Accounts.FirstOrDefault(x => x.Account.Address == a);
            string state;
            if (mine != null) state = "уже в счёте";
            else
            {
                try { var s = await wallet.Backend.AccountAsync(a); state = s.Nonce > 0 || s.Balances.Count > 0 ? "есть монеты или транзакции" : "не использовался"; }
                catch (Exception e) when (e is HttpRequestException or ChainApiException or TaskCanceledException) { state = "не проверен"; }
            }
            Derived.Add(new DerivedItem(derivedNext + (uint)i, a, state, mine != null));
        }
        derivedNext += (uint)addresses.Count;
        Changed(nameof(HasDerived)); Changed(nameof(DerivedLabel));
        Say("");
    }

    private void AddDerivedNow(DerivedItem item)
    {
        if (derivedFor is not { } secretId || item.InWallet) return;
        try
        {
            var account = main.Shell.Session!.AddIndex(secretId, item.Index, $"Адрес № {item.Index + 1}");
            var at = Derived.IndexOf(item);
            if (at >= 0) Derived[at] = item with { InWallet = true, State = "уже в счёте" };
            main.LoadAccounts(account.Id);
            Say($"Добавлен адрес № {account.Index + 1}: {Display.Address(account.Address)}. Он уже выбран.", Tone.Success);
        }
        catch (InvalidOperationException e) { Fail(e); }
    }

    private void AddNow()
    {
        var title = Name.Trim().Length > 0 ? Name.Trim() : $"Счёт {Groups.Count + 1}";
        var session = main.Shell.Session!;
        try
        {
            WalletAccount account;
            if (Kind == AddKind.NewPhrase) { account = session.AddMnemonic(newPhrase, title); NewPhrase(); }
            else
            {
                if (Secrets.Problem(ImportAs, Secret) is { } problem) { Say(problem.Length == 0 ? $"Укажите: {SecretLabel.ToLowerInvariant()}." : problem, Tone.Error); return; }
                account = Secrets.Add(session, ImportAs, Secret, title);
                Secret = "";
            }
            Name = "";
            main.LoadAccounts(account.Id);
            Say($"Добавлен счёт «{title}»: {Display.Address(account.Address)}. Он уже выбран.", Tone.Success);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or FormatException) { Fail(e); }
    }

    // The current address's own name (a phrase's address) or the account's (a key, a watched address: one address).
    private void RenameNow()
    {
        if (main.Selected is not { } s || RenameTo.Trim().Length == 0) return;
        main.Shell.Session!.Rename(s.Account.Id, RenameTo.Trim());
        if (!s.Phrase && s.Account.SecretId is { } secretId) main.Shell.Session.RenameSecret(secretId, RenameTo.Trim());
        main.LoadAccounts(s.Account.Id);
        Say("Название сохранено.", Tone.Success);
    }

    private void RenameGroupNow()
    {
        if (Active is not { } g || main.Selected is not { } s || GroupRenameTo.Trim().Length == 0) return;
        var title = GroupRenameTo.Trim();
        if (g.SecretId is { } secretId) main.Shell.Session!.RenameSecret(secretId, title);
        if (!g.IsPhrase) main.Shell.Session!.Rename(s.Account.Id, title);
        main.LoadAccounts(s.Account.Id);
        Say("Счёт переименован.", Tone.Success);
    }

    private async Task RevealAsync()
    {
        var account = main.Selected?.Account;
        if (account?.SecretId == null || !SelectedHasPhrase) { Say("У этого счёта нет фразы.", Tone.Warning); return; }
        var password = main.Shell.Confirm == null ? null : await main.Shell.Confirm(new ConfirmRequest("Показать фразу восстановления",
            [("Счёт", main.Shell.Store!.NameOf(account.SecretId))], ["Фраза даёт полный доступ ко всем адресам из неё. Убедитесь, что экран никто не видит и нет записи экрана."], "Показать фразу"));
        if (password == null) return;
        if (!await main.Shell.CheckPasswordAsync(password)) { Say("Неверный пароль — фраза не показана.", Tone.Error); return; }
        shownPhrase = main.Shell.Session!.RevealMnemonic(account.SecretId);
        ShownWords = shownPhrase.Split(' ').Select((w, i) => new PhraseWord(i + 1, w)).ToArray();
        Say("");
    }

    // The current address: first click asks, the second removes after the password.
    private async Task RemoveAsync()
    {
        var account = main.Selected?.Account;
        if (account == null) return;
        if (main.Shell.Store!.Accounts.Count == 1) { Say("Это единственный адрес кошелька — его нельзя удалить. Можно сначала добавить другой счёт.", Tone.Warning); return; }
        if (!ConfirmingRemove) { ConfirmingRemove = true; ConfirmingGroupRemove = false; return; }
        var password = main.Shell.Confirm == null ? null : await main.Shell.Confirm(new ConfirmRequest(CanRemoveAddress ? "Убрать адрес" : "Удалить счёт",
            [("Адрес", $"{main.Selected!.Title}\n{Display.Address(account.Address)}")], [RemoveWarning], CanRemoveAddress ? "Убрать адрес" : "Удалить счёт"));
        if (password == null) { ConfirmingRemove = false; return; }
        if (!await main.Shell.CheckPasswordAsync(password)) { Say("Неверный пароль — ничего не удалено.", Tone.Error); return; }
        var title = main.Selected!.Title;
        main.Shell.Session!.Remove(account.Id);
        main.LoadAccounts();
        Say($"«{title}» удалён из кошелька.", Tone.Success);
    }

    // The whole account: a phrase with all its addresses.
    private async Task RemoveGroupAsync()
    {
        if (Active is not { } g) return;
        if (main.Shell.Store!.Accounts.Count == g.Addresses.Count) { Say("Это единственный счёт кошелька — его нельзя удалить. Можно сначала добавить другой.", Tone.Warning); return; }
        if (!ConfirmingGroupRemove) { ConfirmingGroupRemove = true; ConfirmingRemove = false; return; }
        var password = main.Shell.Confirm == null ? null : await main.Shell.Confirm(new ConfirmRequest("Удалить счёт",
            [("Счёт", $"{g.Name}\nадресов: {g.Addresses.Count}")], [GroupRemoveWarning], "Удалить счёт"));
        if (password == null) { ConfirmingGroupRemove = false; return; }
        if (!await main.Shell.CheckPasswordAsync(password)) { Say("Неверный пароль — счёт не удалён.", Tone.Error); return; }
        if (g.SecretId is { } secretId) main.Shell.Session!.RemoveSecret(secretId);
        else foreach (var a in g.Addresses) main.Shell.Session!.Remove(a.Item.Account.Id);
        main.LoadAccounts();
        Say($"Счёт «{g.Name}» удалён из кошелька.", Tone.Success);
    }
}
