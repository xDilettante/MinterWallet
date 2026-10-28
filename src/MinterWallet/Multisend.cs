using System.Collections.ObjectModel;
using System.Numerics;
using Minter.Protocol;
using MinterWallet.Core;

namespace MinterWallet;

// One recipient of a multisend: the address (checked as it is typed — a contact, a new address, a lookalike of a
// contact), the amount and the coin from the account's balances.
public sealed class MultisendRow : Observable
{
    private readonly MultisendViewModel owner;
    private string address = "", amount = "";
    private BalanceItem? coin;
    private int number;

    public MultisendRow(MultisendViewModel owner, string address = "", string amount = "", BalanceItem? coin = null)
    {
        this.owner = owner; this.address = address; this.amount = amount; this.coin = coin ?? owner.Coins.FirstOrDefault();
        Remove = new Command(() => owner.RemoveRow(this));
    }

    public int Number { get => number; set { if (Set(ref number, value)) Changed(nameof(NumberText)); } }
    public string NumberText => $"{Number}.";
    public ObservableCollection<BalanceItem> Coins => owner.Coins;
    public string Address { get => address; set { if (Set(ref address, value.Trim())) { Changed(nameof(Check)); Changed(nameof(CheckTone)); owner.RowsChanged(); } } }
    public string Amount { get => amount; set { if (Set(ref amount, value)) { Changed(nameof(Check)); Changed(nameof(CheckTone)); owner.RowsChanged(); } } }
    public BalanceItem? Coin { get => coin; set { if (Set(ref coin, value)) owner.RowsChanged(); } }
    public Command Remove { get; }

    public bool IsEmpty => Address.Length == 0 && Amount.Trim().Length == 0;

    // What is wrong with the row, or who the recipient is.
    public string Check => Problem() ?? owner.Describe(Minter.Protocol.Address.Parse(Address));
    public Tone CheckTone => IsEmpty ? Tone.None : Problem() != null ? Tone.Error : owner.ToneOf(Minter.Protocol.Address.Parse(Address));

    public string? Problem()
    {
        if (IsEmpty) return "";
        if (!Minter.Protocol.Address.TryParse(Address, out _)) return Address.Length == 0 ? "Укажите адрес получателя." : "Адрес — Mx и 40 шестнадцатеричных символов.";
        if (Amount.Trim().Length == 0) return "Укажите сумму.";
        try { if (Units.ParseCoins(Amount.Trim().Replace(',', '.')).Sign <= 0) return "Сумма должна быть больше нуля."; }
        catch (FormatException) { return "Сумма — число с точкой или запятой, не больше 18 знаков после неё."; }
        return Coin == null ? "Выберите монету." : null;
    }

    public MultisendItem? Item => Problem() == null && Coin != null && !IsEmpty
        ? new MultisendItem(Coin.Coin.Id, Minter.Protocol.Address.Parse(Address), Units.ParseCoins(Amount.Trim().Replace(',', '.'))) : null;
}

// «Нескольким сразу»: up to 100 recipients in one transaction and one commission. Recipients as rows (an address, an
// amount, a coin), added one by one, from the contacts or pasted as a list; or the list as text, «Mx… сумма [монета]» per
// line, which becomes rows again. Below: how many recipients and how much of each coin against what the account has.
public sealed class MultisendViewModel : ScreenModel
{
    private readonly MainViewModel main;
    private string lines = "";
    private bool textMode;

    public MultisendViewModel(MainViewModel main)
    {
        this.main = main;
        SendNow = new AsyncCommand(SendAsync, Fail);
        Paste = new AsyncCommand(PasteAsync, Fail);
        AddRow = new Command(() => { Rows.Add(new MultisendRow(this)); RowsChanged(); });
        PickContact = new RelayCommand(item => { if (item is ContactItem c) AddAddress(c.Address); });
        Rows.Add(new MultisendRow(this));
        RowsChanged();
    }

    public ObservableCollection<MultisendRow> Rows { get; } = [];
    public ObservableCollection<BalanceItem> Coins { get; } = [];
    public ObservableCollection<ContactItem> Contacts => main.Contacts;
    public bool HasContacts => main.Contacts.Count > 0;
    public int ModeIndex { get => textMode ? 1 : 0; set { if (value >= 0) SetTextMode(value == 1); } }
    public bool IsRows => !textMode;
    public bool IsText => textMode;
    // The list as text; setting it switches to the text form (a pasted list, the checks).
    public string Lines { get => lines; set { if (!Set(ref lines, value)) return; if (!textMode) { textMode = true; ModeChanged(); } SummaryChanged(); } }
    public AsyncCommand SendNow { get; }
    public AsyncCommand Paste { get; }
    public Command AddRow { get; }
    public RelayCommand PickContact { get; }

    public string Summary => Collect(out var items) is { } problem ? problem
        : items.Count == 0 ? "" : $"Получателей: {items.Count} · " + string.Join(", ", items.GroupBy(i => i.Coin).Select(g => $"{Display.Amount(g.Aggregate(BigInteger.Zero, (s, i) => s + i.Value))} {main.Symbol(g.Key)}"))
            + Shortage(items);
    public Tone SummaryTone => Collect(out var items) != null ? Tone.Warning : items.Count == 0 ? Tone.None : Shortage(items).Length > 0 ? Tone.Warning : Tone.Success;

    public void BalancesChanged()
    {
        var keep = Rows.Select(r => r.Coin?.Coin.Id).ToList();
        Coins.Clear();
        foreach (var b in main.Balances) Coins.Add(b);
        for (var i = 0; i < Rows.Count; i++) Rows[i].Coin = Coins.FirstOrDefault(c => c.Coin.Id == keep[i]) ?? Coins.FirstOrDefault();
        SummaryChanged();
    }

    public void RemoveRow(MultisendRow row)
    {
        Rows.Remove(row);
        if (Rows.Count == 0) Rows.Add(new MultisendRow(this));
        RowsChanged();
    }

    public void RowsChanged()
    {
        for (var i = 0; i < Rows.Count; i++) Rows[i].Number = i + 1;
        SummaryChanged();
    }

    public string Describe(Address to) =>
        main.LookalikeOf(to) is { } like ? MainViewModel.LookalikeWarning(like)
        : main.Shell.AddressBook.NameOf(to) is { Length: > 0 } n ? $"Из адресной книги: {n}"
        : main.Accounts.FirstOrDefault(a => a.Account.Address == to) is { } own ? $"Ваш счёт «{own.Name}»."
        : main.Shell.AddressBook.IsKnown(to) ? "Вы уже отправляли на этот адрес." : "Новый получатель: сверьте адрес целиком.";
    public Tone ToneOf(Address to) => main.LookalikeOf(to) != null ? Tone.Error
        : main.Shell.AddressBook.IsKnown(to) || main.Accounts.Any(a => a.Account.Address == to) ? Tone.Success : Tone.Warning;

    private void AddAddress(string address)
    {
        if (Rows.LastOrDefault() is { IsEmpty: true } last) last.Address = address;
        else Rows.Add(new MultisendRow(this, address));
        RowsChanged();
    }

    private void SetTextMode(bool text)
    {
        if (text == textMode) return;
        if (text)
        {
            lines = string.Join("\n", Rows.Where(r => !r.IsEmpty).Select(r => $"{r.Address} {r.Amount.Trim()} {r.Coin?.Symbol}".TrimEnd()));
            Changed(nameof(Lines));
        }
        else
        {
            if (ParseLines(out var parsed) is { } problem) { Say(problem, Tone.Error); Changed(nameof(ModeIndex)); return; }
            Rows.Clear();
            foreach (var (to, amount, coin) in parsed) Rows.Add(new MultisendRow(this, to, amount, coin));
            if (Rows.Count == 0) Rows.Add(new MultisendRow(this));
            RowsChanged();
            Say("");
        }
        textMode = text;
        ModeChanged();
    }

    private void ModeChanged() { Changed(nameof(ModeIndex)); Changed(nameof(IsRows)); Changed(nameof(IsText)); }
    private void SummaryChanged() { Changed(nameof(Summary)); Changed(nameof(SummaryTone)); }

    // The recipients as they stand, or the first problem.
    private string? Collect(out List<MultisendItem> items)
    {
        items = [];
        if (textMode)
        {
            if (ParseLines(out var parsed) is { } problem) return problem;
            items = parsed.Select(p => new MultisendItem(p.Coin.Coin.Id, Address.Parse(p.To), Units.ParseCoins(p.Amount.Replace(',', '.')))).ToList();
        }
        else
        {
            foreach (var row in Rows.Where(r => !r.IsEmpty))
            {
                if (row.Problem() is { } problem) return $"Строка {row.Number}: {problem}";
                items.Add(row.Item!);
            }
        }
        return items.Count > 100 ? "Не больше 100 получателей в одной транзакции." : null;
    }

    // What the account lacks for these sums, coin by coin (the commission comes on top).
    private string Shortage(List<MultisendItem> items)
    {
        var lacking = items.GroupBy(i => i.Coin)
            .Select(g => (Coin: g.Key, Need: g.Aggregate(BigInteger.Zero, (s, i) => s + i.Value), Have: main.Balances.FirstOrDefault(b => b.Coin.Id == g.Key)?.Value ?? 0))
            .Where(x => x.Need > x.Have).Select(x => $"{main.Symbol(x.Coin)}: нужно {Display.Amount(x.Need)}, есть {Display.Amount(x.Have)}").ToList();
        return lacking.Count == 0 ? "" : "\nНе хватает — " + string.Join("; ", lacking);
    }

    // "Mx… сумма [ТИКЕР]" per line; spaces, tabs or semicolons between fields; the ticker must be one of the balances.
    private string? ParseLines(out List<(string To, string Amount, BalanceItem Coin)> parsed)
    {
        parsed = [];
        var number = 0;
        foreach (var raw in Lines.Split('\n'))
        {
            number++;
            var parts = raw.Split([' ', '\t', ';'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;
            if (parts.Length is < 2 or > 3 || !Address.TryParse(parts[0], out _)) return $"Строка {number}: нужно «Mx… сумма [монета]».";
            var symbol = parts.Length == 3 ? parts[2].ToUpperInvariant() : main.Symbol(0);
            var balance = main.Balances.FirstOrDefault(b => b.Coin.Symbol == symbol);
            if (balance == null) return $"Строка {number}: монеты {symbol} нет на счёте.";
            BigInteger value;
            try { value = Units.ParseCoins(parts[1].Replace(',', '.')); } catch (FormatException) { return $"Строка {number}: сумма «{parts[1]}» — не число."; }
            if (value.Sign <= 0) return $"Строка {number}: сумма должна быть больше нуля.";
            parsed.Add((parts[0], parts[1], balance));
        }
        return null;
    }

    // A pasted list: into the text form when it is open, otherwise into rows (the text form shows the line at fault).
    private async Task PasteAsync()
    {
        var text = main.Shell.PasteText == null ? null : await main.Shell.PasteText();
        if (string.IsNullOrWhiteSpace(text)) { Say("В буфере обмена нет текста.", Tone.Warning); return; }
        var pasted = text.Replace("\r\n", "\n").Trim();
        var existing = textMode ? lines : string.Join("\n", Rows.Where(r => !r.IsEmpty).Select(r => $"{r.Address} {r.Amount.Trim()} {r.Coin?.Symbol}".TrimEnd()));
        Lines = (existing.Length > 0 ? existing + "\n" : "") + pasted;
        if (ParseLines(out _) == null) SetTextMode(false);
    }

    private async Task SendAsync()
    {
        if (Collect(out var items) is { } problem) { Say(problem, Tone.Error); return; }
        if (items.Count == 0) { Say("Добавьте хотя бы одного получателя.", Tone.Error); return; }
        var warnings = items.Select(i => main.LookalikeOf(i.To) is { } like ? $"{Display.Address(i.To)}: {MainViewModel.LookalikeWarning(like)}" : null).OfType<string>().ToList();
        warnings.AddRange(items.Where(i => !main.Shell.AddressBook.IsKnown(i.To)).Select(i => $"Новый получатель {Display.Address(i.To)}").Take(10));
        var rows = items.Select((i, k) => ($"{k + 1}.", $"{Display.Address(i.To)}\n{Units.FormatCoins(i.Value)} {main.Symbol(i.Coin)} (id {i.Coin})")).ToList();
        var outcome = await main.ConfirmAndSendAsync("Мультисенд", new MultisendData(items), 0, [], rows, warnings, this);
        if (outcome?.Succeeded != true) return;
        foreach (var i in items) main.Shell.AddressBook.Remember(i.To);
        Rows.Clear(); Rows.Add(new MultisendRow(this)); lines = ""; Changed(nameof(Lines)); RowsChanged();
    }
}
