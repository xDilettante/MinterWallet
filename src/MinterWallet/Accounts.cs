using System.Collections.ObjectModel;
using Minter.Protocol;
using MinterWallet.Core;

namespace MinterWallet;

public enum AddKind { Derive, NewPhrase, ImportPhrase, ImportKey, Watch, Vanity }

// An address of a phrase by its number (index + 1): in the wallet already, used elsewhere, or never used.
public sealed record DerivedItem(uint Index, Address Address, string State, bool InWallet)
{
    public string Number => $"№ {Index + 1}";
    public string AddressText => Address.ToString();
    public bool CanAdd => !InWallet;
}

// A recovery phrase already in the key store, named after the accounts made from it.
public sealed record PhraseSource(string SecretId, string Title)
{
    public override string ToString() => Title;
}

// The accounts of the wallet: pick one, rename it, show its phrase, remove it, and add more — the next address of a
// phrase already here, a new phrase, or an existing phrase, key or address.
public sealed class AccountsViewModel : ScreenModel
{
    private readonly MainViewModel main;
    private int addIndex;
    private string name = "", secret = "", renameTo = "", newPhrase = "";
    private PhraseSource? phrase;
    private IReadOnlyList<PhraseWord> newWords = [], shownWords = [];
    private string? shownPhrase;
    private bool confirmingRemove;
    private string contactName = "", contactAddress = "";

    public AccountsViewModel(MainViewModel main)
    {
        this.main = main;
        Add = new Command(AddNow);
        Paste = new AsyncCommand(PasteAsync, Fail);
        Vanity = new VanityViewModel(main);
        ShowDerived = new AsyncCommand(ShowDerivedAsync, Fail);
        AddDerived = new RelayCommand(p => { if (p is DerivedItem item) AddDerivedNow(item); });
        CopyNewPhrase = new AsyncCommand(() => Copy(newPhrase, "Фраза скопирована. Сохраните её там, где её никто не увидит."), Fail);
        Rename = new Command(RenameNow);
        Reveal = new AsyncCommand(RevealAsync, Fail);
        CopyPhrase = new AsyncCommand(() => Copy(shownPhrase ?? "", "Фраза скопирована."), Fail);
        HidePhrase = new Command(() => { shownPhrase = null; ShownWords = []; });
        CopyAddress = new AsyncCommand(() => Copy(main.Address, "Адрес скопирован."), Fail);
        Remove = new AsyncCommand(RemoveAsync, Fail);
        CancelRemove = new Command(() => ConfirmingRemove = false);
        AddContact = new Command(AddContactNow);
    }

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
            foreach (var n in new[] { nameof(IsDerive), nameof(IsNewPhrase), nameof(IsImport), nameof(IsKey), nameof(IsPlainSecret), nameof(IsVanity), nameof(IsPlainAdd), nameof(SecretLabel), nameof(SecretPlaceholder), nameof(SecretHeight), nameof(AddLabel) }) Changed(n);
        }
    }
    public AddKind Kind => (AddKind)addIndex;
    public bool IsDerive => Kind == AddKind.Derive;
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
    public string AddLabel => Kind switch { AddKind.Derive => "Создать адрес", AddKind.NewPhrase => "Добавить кошелёк", _ => "Добавить" };

    public ObservableCollection<PhraseSource> Phrases { get; } = [];
    public PhraseSource? Phrase { get => phrase; set { if (!Set(ref phrase, value)) return; Derived.Clear(); derivedNext = 0; Changed(nameof(HasDerived)); } }

    // The phrase's addresses by number, ten at a time, with what each is: so an address used in another wallet can be
    // added back by its number.
    public ObservableCollection<DerivedItem> Derived { get; } = [];
    public bool HasDerived => Derived.Count > 0;
    public string DerivedLabel => Derived.Count == 0 ? "Показать адреса этой фразы" : "Ещё 10";
    private uint derivedNext;
    public bool HasPhrases => Phrases.Count > 0;
    public bool HasSeveralPhrases => Phrases.Count > 1;
    public string DeriveHint => Phrases.Count == 0 ? "В кошельке пока нет фразы восстановления. Создайте новую или импортируйте свою."
        : $"Следующий адрес из фразы «{(Phrases.Count == 1 ? Phrases[0].Title : "выбранной ниже")}». Он восстанавливается той же фразой — отдельно записывать ничего не нужно.";
    public string Name { get => name; set => Set(ref name, value); }
    public string Secret { get => secret; set { Set(ref secret, value); Changed(nameof(SecretCheck)); Changed(nameof(SecretTone)); if (Tone == Tone.Error) Say(""); } }
    public string SecretCheck => Secrets.Check(ImportAs, Secret);
    public Tone SecretTone => Secrets.ToneOf(ImportAs, Secret);
    public IReadOnlyList<PhraseWord> NewWords { get => newWords; private set => Set(ref newWords, value); }

    public string RenameTo { get => renameTo; set => Set(ref renameTo, value); }
    public IReadOnlyList<PhraseWord> ShownWords { get => shownWords; private set { Set(ref shownWords, value); Changed(nameof(PhraseShown)); } }
    public bool PhraseShown => ShownWords.Count > 0;
    public bool SelectedHasPhrase => main.Selected?.Account is { SecretId: not null } a && main.Shell.Store!.KindOf(a) == SecretKind.Mnemonic;
    public string SelectedKind => main.Selected?.Account is not { } a ? "" : a.WatchOnly ? "Только просмотр: ключа в кошельке нет, подписать нельзя."
        : main.Shell.Store!.KindOf(a) == SecretKind.Mnemonic ? $"Адрес № {a.Index + 1} из фразы восстановления." : "Добавлен приватным ключом.";
    public bool ConfirmingRemove { get => confirmingRemove; set => Set(ref confirmingRemove, value); }
    public string RemoveWarning => main.Selected?.Account is not { } a ? "" : a.WatchOnly ? "Адрес только для просмотра просто исчезнет из списка."
        : main.Shell.Store!.Accounts.Count(x => x.SecretId == a.SecretId) > 1 ? "Адрес исчезнет из списка; фраза останется для других адресов, и его можно будет создать снова."
        : "Ключ этого счёта удалится из файла. Без сохранённой фразы или ключа доступ к средствам на нём не вернуть.";

    public Command Add { get; }
    public AsyncCommand Paste { get; }
    public AsyncCommand CopyNewPhrase { get; }
    public AsyncCommand ShowDerived { get; }
    public RelayCommand AddDerived { get; }
    public Command Rename { get; }
    public AsyncCommand Reveal { get; }
    public AsyncCommand CopyPhrase { get; }
    public Command HidePhrase { get; }
    public AsyncCommand CopyAddress { get; }
    public AsyncCommand Remove { get; }
    public Command CancelRemove { get; }

    // Called when the list of accounts or the current one changes.
    public void Reload()
    {
        var store = main.Shell.Store!;
        var keep = Phrase?.SecretId;
        Phrases.Clear();
        foreach (var group in store.Accounts.Where(a => a.SecretId != null && store.KindOf(a) == SecretKind.Mnemonic).GroupBy(a => a.SecretId!))
        {
            var first = group.OrderBy(a => a.Index).First();
            Phrases.Add(new PhraseSource(group.Key, group.Count() == 1 ? first.Name : $"{first.Name} и ещё {group.Count() - 1}"));
        }
        Phrase = Phrases.FirstOrDefault(p => p.SecretId == keep) ?? Phrases.FirstOrDefault(p => p.SecretId == main.Selected?.Account.SecretId) ?? Phrases.FirstOrDefault();
        if (Name.Length == 0 || Name.StartsWith("Счёт ", StringComparison.Ordinal)) Name = $"Счёт {store.Accounts.Count + 1}";
        RenameTo = main.AccountName;
        shownPhrase = null; ShownWords = []; ConfirmingRemove = false;
        foreach (var n in new[] { nameof(HasPhrases), nameof(HasSeveralPhrases), nameof(DeriveHint), nameof(SelectedHasPhrase), nameof(SelectedKind), nameof(RemoveWarning) }) Changed(n);
    }

    // Opens the "add" panel on the kind that fits: the next address of a phrase when there is one.
    public void StartAdding() => AddIndex = (int)(HasPhrases ? AddKind.Derive : AddKind.NewPhrase);

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

    private async Task ShowDerivedAsync()
    {
        if (Phrase is not { } source) { Say("В кошельке нет фразы: создайте новую или импортируйте.", Tone.Error); return; }
        var session = main.Shell.Session!;
        var addresses = session.AddressesOf(source.SecretId, derivedNext, 10);
        var wallet = await main.WalletAsync();
        Say("Проверяю, какие адреса использовались…", Tone.Progress);
        for (var i = 0; i < addresses.Count; i++)
        {
            var a = addresses[i];
            var mine = main.Accounts.FirstOrDefault(x => x.Account.Address == a);
            string state;
            if (mine != null) state = $"в кошельке: «{mine.Name}»";
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
        if (Phrase is not { } source || item.InWallet) return;
        try
        {
            var title = Name.Trim().Length > 0 ? Name.Trim() : $"Адрес № {item.Index + 1}";
            var account = main.Shell.Session!.AddIndex(source.SecretId, item.Index, title);
            Name = "";
            var at = Derived.IndexOf(item);
            if (at >= 0) Derived[at] = item with { InWallet = true, State = $"в кошельке: «{account.Name}»" };
            main.LoadAccounts(account.Id);
            Say($"Добавлен счёт «{account.Name}»: {Display.Address(account.Address)}. Он уже выбран.", Tone.Success);
        }
        catch (InvalidOperationException e) { Fail(e); }
    }

    private void AddNow()
    {
        var title = Name.Trim().Length > 0 ? Name.Trim() : $"Счёт {main.Shell.Store!.Accounts.Count + 1}";
        var session = main.Shell.Session!;
        try
        {
            WalletAccount account;
            switch (Kind)
            {
                case AddKind.Derive:
                    if (Phrase == null) { Say("В кошельке нет фразы: создайте новую или импортируйте.", Tone.Error); return; }
                    account = session.AddNextIndex(Phrase.SecretId, title);
                    break;
                case AddKind.NewPhrase:
                    account = session.AddMnemonic(newPhrase, title);
                    NewPhrase();
                    break;
                default:
                    if (Secrets.Problem(ImportAs, Secret) is { } problem) { Say(problem.Length == 0 ? $"Укажите: {SecretLabel.ToLowerInvariant()}." : problem, Tone.Error); return; }
                    account = Secrets.Add(session, ImportAs, Secret, title);
                    Secret = "";
                    break;
            }
            Name = "";
            main.LoadAccounts(account.Id);
            Say($"Добавлен счёт «{account.Name}»: {Display.Address(account.Address)}. Он уже выбран.", Tone.Success);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or FormatException) { Fail(e); }
    }

    private void RenameNow()
    {
        if (main.Selected == null || RenameTo.Trim().Length == 0) return;
        main.Shell.Session!.Rename(main.Selected.Account.Id, RenameTo.Trim());
        main.LoadAccounts(main.Selected.Account.Id);
        Say("Счёт переименован.", Tone.Success);
    }

    private async Task RevealAsync()
    {
        var account = main.Selected?.Account;
        if (account?.SecretId == null || !SelectedHasPhrase) { Say("У этого счёта нет фразы.", Tone.Warning); return; }
        var password = main.Shell.Confirm == null ? null : await main.Shell.Confirm(new ConfirmRequest("Показать фразу восстановления",
            [("Счёт", account.Name)], ["Фраза даёт полный доступ ко всем адресам из неё. Убедитесь, что экран никто не видит и нет записи экрана."], "Показать фразу"));
        if (password == null) return;
        if (!await main.Shell.CheckPasswordAsync(password)) { Say("Неверный пароль — фраза не показана.", Tone.Error); return; }
        shownPhrase = main.Shell.Session!.RevealMnemonic(account.SecretId);
        ShownWords = shownPhrase.Split(' ').Select((w, i) => new PhraseWord(i + 1, w)).ToArray();
        Say("");
    }

    // First click asks, the second removes after the password.
    private async Task RemoveAsync()
    {
        var account = main.Selected?.Account;
        if (account == null) return;
        if (main.Shell.Store!.Accounts.Count == 1) { Say("Это единственный счёт кошелька — его нельзя удалить. Можно сначала добавить другой.", Tone.Warning); return; }
        if (!ConfirmingRemove) { ConfirmingRemove = true; return; }
        var password = main.Shell.Confirm == null ? null : await main.Shell.Confirm(new ConfirmRequest("Удалить счёт",
            [("Счёт", $"{account.Name}\n{Display.Address(account.Address)}")], [RemoveWarning], "Удалить счёт"));
        if (password == null) { ConfirmingRemove = false; return; }
        if (!await main.Shell.CheckPasswordAsync(password)) { Say("Неверный пароль — счёт не удалён.", Tone.Error); return; }
        main.Shell.Session!.Remove(account.Id);
        main.LoadAccounts();
        Say($"Счёт «{account.Name}» удалён из кошелька.", Tone.Success);
    }
}
