using System.Collections.ObjectModel;
using System.Numerics;
using System.Text;
using Avalonia.Threading;
using Minter.Protocol;
using MinterWallet.Core;

namespace MinterWallet;

// One member of a shared account being created: an address (typed, or picked from the accounts and contacts) and a weight.
public sealed class MemberRow : Observable
{
    private readonly MultisigViewModel owner;
    private string address = "";
    private decimal? weight = 1;

    public MemberRow(MultisigViewModel owner, string address = "")
    {
        this.owner = owner;
        this.address = address;
        Remove = new Command(() => owner.RemoveMember(this));
    }

    public string Address { get => address; set { if (!Set(ref address, value.Trim())) return; owner.MembersChanged(); } }
    public decimal? Weight { get => weight; set { if (!Set(ref weight, value)) return; owner.MembersChanged(); } }
    public uint WeightValue => (uint)Math.Clamp(weight ?? 1, 1, 1023);
    public string Hint => owner.HintOf(this);
    public Tone HintTone => owner.ToneOf(this);
    public Command Remove { get; }
    public void Refresh() { Changed(nameof(Hint)); Changed(nameof(HintTone)); }
}

public sealed record KnownAddress(string Name, Address Address)
{
    public override string ToString() => $"{Name} · {Core.History.Short(Address)}";
}

public sealed record SignerItem(string Name, string Address, uint Weight, bool Signed)
{
    public string Caption => Signed ? $"вес {Weight} · подписал" : $"вес {Weight}";
}

// «Общий счёт» (multisignature): create one from members' addresses with weights and a threshold; build a transfer from
// it signed by this account and pass the text on; sign a text another member sent; send once the weights reach the
// threshold. Every signature asks for the password over what is signed, as a transfer does.
public sealed class MultisigViewModel : ScreenModel
{
    private readonly MainViewModel main;
    private int modeIndex;
    private string created = "", account = "", accountInfo = "", recipient = "", amount = "", payload = "", text = "", textInfo = "", copyLabel = "Копировать";
    private CoinChoice? coin;
    private Transaction? tx;
    private MultisigInfo? info;
    private uint signedWeight;
    private ulong usedNonce;

    private decimal? thresholdValue = 2;

    public MultisigViewModel(MainViewModel main)
    {
        this.main = main;
        AddMember = new Command(() => { MemberRows.Add(new MemberRow(this)); MembersChanged(); });
        SetThreshold = new RelayCommand(p => ThresholdValue = p switch
        {
            "all" => Total,
            "most" => Total / 2 + 1,
            _ => MemberRows.Count == 0 ? 1 : MemberRows.Min(r => r.WeightValue),
        });
        Create = new AsyncCommand(CreateAsync, Fail);
        WatchCreated = new Command(() =>
        {
            main.AccountsPage.AddIndex = (int)AddKind.Watch; main.AccountsPage.Secret = created; main.AccountsPage.Name = "Общий счёт";
            main.AccountsPage.Add.Execute(null);
        });
        LoadAccount = new AsyncCommand(LoadAccountAsync, Fail);
        Propose = new AsyncCommand(ProposeAsync, Fail);
        Sign = new AsyncCommand(SignAsync, Fail, () => CanSign);
        SendNow = new AsyncCommand(SendAsync, Fail, () => CanSend);
        Copy = new AsyncCommand(async () =>
        {
            if (main.Shell.CopyText == null || Text.Length == 0) return;
            await main.Shell.CopyText(Text);
            CopyLabel = "Скопировано";
            DispatcherTimer.RunOnce(() => CopyLabel = "Копировать", TimeSpan.FromSeconds(2));
        }, Fail);
        Paste = new AsyncCommand(async () => { if (main.Shell.PasteText != null && await main.Shell.PasteText() is { } t) Text = t.Trim(); }, Fail);
    }

    public int ModeIndex { get => modeIndex; set { if (value >= 0 && Set(ref modeIndex, value)) { Changed(nameof(IsCreate)); Changed(nameof(IsTransaction)); if (value == 0) StartCreating(); } } }
    public bool IsCreate => modeIndex == 0;
    public bool IsTransaction => modeIndex == 1;

    // Creating: the members as rows, this account first; the threshold with its meaning in words.
    public ObservableCollection<MemberRow> MemberRows { get; } = [];
    public ObservableCollection<KnownAddress> Known { get; } = [];
    public KnownAddress? Pick
    {
        get => null;
        set
        {
            if (value == null) return;
            var empty = MemberRows.FirstOrDefault(r => r.Address.Length == 0);
            if (MemberRows.Any(r => string.Equals(r.Address, value.Address.ToString(), StringComparison.OrdinalIgnoreCase))) return;
            if (empty != null) empty.Address = value.Address.ToString(); else { MemberRows.Add(new MemberRow(this, value.Address.ToString())); MembersChanged(); }
            Changed();
        }
    }
    public Command AddMember { get; }
    public RelayCommand SetThreshold { get; }
    public decimal? ThresholdValue { get => thresholdValue; set { if (Set(ref thresholdValue, value)) Changed(nameof(ThresholdText)); } }
    public uint Total => (uint)MemberRows.Sum(r => (long)r.WeightValue);
    public string TotalText => $"Сумма весов: {Total}";
    public string ThresholdText
    {
        get
        {
            var need = (uint)Math.Max(1, ThresholdValue ?? 1);
            var rows = MemberRows.Where(r => r.Address.Length > 0).ToList();
            if (rows.Count < 2) return "Добавьте хотя бы двух участников.";
            if (need > Total) return $"Порог {need} больше суммы весов {Total}: такой счёт никогда не сможет ничего отправить.";
            if (rows.All(r => r.WeightValue == rows[0].WeightValue))
            {
                var k = (int)Math.Ceiling(need / (double)rows[0].WeightValue);
                return k == rows.Count ? $"Тратить можно, только когда подпишут все {rows.Count}." : k == 1 ? "Тратить может любой участник в одиночку." : $"Тратить можно, когда подпишут любые {k} из {rows.Count}.";
            }
            var alone = rows.Where(r => r.WeightValue >= need).Select(Name).ToList();
            return alone.Count > 0 ? $"Нужен вес {need} из {Total}. В одиночку может: {string.Join(", ", alone)}." : $"Нужен вес {need} из {Total}: подписи нескольких участников, чьи веса в сумме не меньше {need}.";
        }
    }

    public void MembersChanged()
    {
        foreach (var r in MemberRows) r.Refresh();
        Changed(nameof(Total)); Changed(nameof(TotalText)); Changed(nameof(ThresholdText));
    }

    public void RemoveMember(MemberRow row) { MemberRows.Remove(row); MembersChanged(); }

    private string Name(MemberRow row) => Minter.Protocol.Address.TryParse(row.Address, out var a) ? NameOf(a) ?? Core.History.Short(a) : row.Address;
    private string? NameOf(Address a) => main.Accounts.FirstOrDefault(x => x.Account.Address == a)?.Name is { } n ? n : main.Shell.AddressBook.NameOf(a) is { Length: > 0 } c ? c : null;

    public string HintOf(MemberRow row)
    {
        if (row.Address.Length == 0) return "Вставьте адрес Mx… или выберите из счетов и контактов.";
        if (!Minter.Protocol.Address.TryParse(row.Address, out var a)) return "Адрес — Mx и 40 шестнадцатеричных символов.";
        if (MemberRows.TakeWhile(r => r != row).Any(r => string.Equals(r.Address, row.Address, StringComparison.OrdinalIgnoreCase))) return "Этот адрес уже есть выше.";
        if (main.LookalikeOf(a) is { } like) return MainViewModel.LookalikeWarning(like);
        if (main.Selected?.Account.Address == a) return $"Это вы — «{main.Selected.Name}».";
        return NameOf(a) is { } name ? $"«{name}»." : "Адрес не из ваших счетов и контактов: сверьте его с участником.";
    }

    public Tone ToneOf(MemberRow row) =>
        row.Address.Length == 0 ? Tone.None
        : !Minter.Protocol.Address.TryParse(row.Address, out var a) || MemberRows.TakeWhile(r => r != row).Any(r => string.Equals(r.Address, row.Address, StringComparison.OrdinalIgnoreCase)) || main.LookalikeOf(a) != null ? Tone.Error
        : NameOf(a) != null ? Tone.Success : Tone.Warning;

    // This account first, an empty row for the next member, and the accounts and contacts to pick from.
    public void StartCreating()
    {
        Known.Clear();
        foreach (var a in main.Accounts) Known.Add(new KnownAddress(a.Name, a.Account.Address));
        foreach (var c in main.Contacts) if (Minter.Protocol.Address.TryParse(c.Address, out var ca) && Known.All(k => k.Address != ca)) Known.Add(new KnownAddress(c.Name, ca));
        if (MemberRows.Count > 0) return;
        if (main.Selected is { } me) MemberRows.Add(new MemberRow(this, me.Account.Address.ToString()));
        MemberRows.Add(new MemberRow(this));
        MembersChanged();
    }
    public string Created { get => created; private set { Set(ref created, value); Changed(nameof(HasCreated)); } }
    public bool HasCreated => Created.Length > 0;
    public AsyncCommand Create { get; }
    public Command WatchCreated { get; }

    // A transfer from the shared account
    public string Account { get => account; set => Set(ref account, value); }
    public string AccountInfo { get => accountInfo; private set => Set(ref accountInfo, value); }
    public ObservableCollection<CoinChoice> Coins { get; } = [];
    public CoinChoice? Coin { get => coin; set => Set(ref coin, value); }
    public string Recipient { get => recipient; set => Set(ref recipient, value); }
    public string Amount { get => amount; set => Set(ref amount, value); }
    public string Payload { get => payload; set => Set(ref payload, value); }
    public AsyncCommand LoadAccount { get; }
    public AsyncCommand Propose { get; }

    // The transaction travelling between members
    public string Text { get => text; set { if (Set(ref text, value)) _ = ReadAsync(); } }
    public string TextInfo { get => textInfo; private set => Set(ref textInfo, value); }
    public bool HasTx => tx != null;
    public ObservableCollection<SignerItem> Signers { get; } = [];
    public string Progress => info == null ? "" : $"Подписано: вес {signedWeight} из {info.Threshold} нужных.";
    public bool CanSign => tx != null && info != null && main.Selected is { Account.WatchOnly: false } s && info.IsMember(s.Account.Address)
        && !tx.MultisigSigners().Contains(s.Account.Address) && tx.Nonce > usedNonce;
    public bool CanSend => tx != null && info != null && signedWeight >= info.Threshold && tx.Nonce > usedNonce;
    public string CopyLabel { get => copyLabel; private set => Set(ref copyLabel, value); }
    public AsyncCommand Sign { get; }
    public AsyncCommand SendNow { get; }
    public AsyncCommand Copy { get; }
    public AsyncCommand Paste { get; }

    private string? ParseMembers(out List<Address> addresses, out List<uint> weights)
    {
        addresses = []; weights = [];
        var number = 0;
        foreach (var row in MemberRows)
        {
            number++;
            if (row.Address.Length == 0) continue;
            if (!Minter.Protocol.Address.TryParse(row.Address, out var a)) return $"Участник {number}: адрес — Mx и 40 шестнадцатеричных символов.";
            if (addresses.Contains(a)) return $"Участник {number}: этот адрес уже есть.";
            addresses.Add(a); weights.Add(row.WeightValue);
        }
        if (addresses.Count < 2) return "Нужно хотя бы два участника.";
        if (addresses.Count > 32) return "Не больше 32 участников.";
        return null;
    }

    private async Task CreateAsync()
    {
        if (ParseMembers(out var addresses, out var weights) is { } problem) { Say(problem, Tone.Error); return; }
        var need = (uint)Math.Max(0, ThresholdValue ?? 0);
        if (need == 0) { Say("Порог — целое число больше нуля.", Tone.Error); return; }
        var total = weights.Aggregate(0u, (s, w) => s + w);
        if (need > total) { Say($"Порог {need} больше суммы весов {total}: такой счёт никогда не сможет ничего отправить.", Tone.Error); return; }
        var me = main.Selected!.Account.Address;
        var rows = new List<(string, string)> { ("Порог", $"{need} из {total}") };
        rows.AddRange(addresses.Select((a, i) => ($"Участник {i + 1}", $"{Display.Address(a)}\nвес {weights[i]}")));
        var warnings = new List<string>();
        if (!addresses.Contains(me)) warnings.Add("Этого счёта нет среди участников: подписывать за общий счёт он не сможет.");
        var outcome = await main.ConfirmAndSendAsync("Создание общего счёта", new CreateMultisigData(need, weights, addresses), 0, [], rows, warnings, this);
        if (outcome?.Succeeded != true) return;
        // The address follows from the creator and the nonce of the creating transaction.
        var wallet = await main.WalletAsync();
        var record = await wallet.Backend.TransactionAsync(outcome.Hash);
        if (record == null) return;
        Created = Minter.Protocol.Address.Multisig(me, Transaction.Decode(record.Raw).Nonce).ToString();
        Account = Created;
        Say($"Готово: общий счёт {Core.History.Short(Minter.Protocol.Address.Parse(Created))}. Пополните его обычным переводом, а тратить — здесь, во вкладке «Перевод».", Tone.Success);
    }

    private async Task LoadAccountAsync()
    {
        info = null; Coins.Clear(); AccountInfo = "";
        if (!Minter.Protocol.Address.TryParse(Account.Trim(), out var address)) { Say("Адрес общего счёта — Mx и 40 шестнадцатеричных символов.", Tone.Error); return; }
        var wallet = await main.WalletAsync();
        var state = await wallet.Backend.AccountAsync(address);
        if (state.Multisig is not { } m) { Say("Это обычный адрес, а не общий счёт.", Tone.Error); return; }
        info = m;
        foreach (var b in state.Balances) Coins.Add(new CoinChoice(b.Coin.Id, b.Coin.Symbol));
        Coin = Coins.FirstOrDefault(c => c.Id == 0) ?? Coins.FirstOrDefault();
        var me = main.Selected?.Account.Address;
        var mine = m.Members.FirstOrDefault(x => x.Address == me);
        // Lines, each translated on its own.
        AccountInfo = string.Join("\n",
            $"Порог {m.Threshold} из {m.Members.Sum(x => (long)x.Weight)}, участников: {m.Members.Count}.",
            mine != null ? $"Этот счёт — участник с весом {mine.Weight}." : "Этот счёт не участник: подписать не сможет.",
            state.Balances.Count == 0 ? "На общем счёте пока пусто." : $"На счёте: {string.Join(", ", state.Balances.Select(b => $"{Display.Amount(b.Value)} {b.Coin.Symbol}"))}.");
        Say("");
    }

    // The transfer built, confirmed and signed by this account: its text is what the other members sign.
    private async Task ProposeAsync()
    {
        if (info == null) await LoadAccountAsync();
        if (info == null) return;
        if (!Minter.Protocol.Address.TryParse(Recipient.Trim(), out var to)) { Say("Укажите получателя: Mx и 40 шестнадцатеричных символов.", Tone.Error); return; }
        if (Coin == null) { Say("На общем счёте нет монет.", Tone.Error); return; }
        if (!MainViewModel.TryAmount(Amount, this, out var value)) return;
        var multisig = Minter.Protocol.Address.Parse(Account.Trim());
        var wallet = await main.WalletAsync();
        var data = new SendData(Coin.Id, to, value);
        var (proposed, preview) = await wallet.ProposeMultisigAsync(multisig, data, 0, MainViewModel.Utf8(Payload));
        var rows = new List<(string, string)>
        {
            ("Операция", Core.History.Describe(data, multisig, multisig, main.Symbol)), ("От общего счёта", Display.Address(multisig)),
            ("Получатель", Display.Address(to)), ("Сумма", $"{Units.FormatCoins(value)} {Coin.Symbol} (id {Coin.Id})"),
            ("Комиссия", $"{Units.FormatCoins(preview.Commission)} {preview.CommissionCoin.Symbol}, платит общий счёт"), ("Номер транзакции", proposed.Nonce.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        };
        var warnings = new List<string>();
        if (main.LookalikeOf(to) is { } like) warnings.Add(MainViewModel.LookalikeWarning(like));
        if (!await ConfirmAndSignAsync("Перевод с общего счёта", proposed, rows, warnings)) return;
        Text = MultisigText.Of(proposed);
        Amount = ""; Payload = "";
    }

    private async Task SignAsync()
    {
        if (tx == null) return;
        var data = tx.DecodeData();
        var rows = new List<(string, string)> { ("Операция", Core.History.Describe(data, tx.Sender(), tx.Sender(), main.Symbol)), ("От общего счёта", Display.Address(tx.Sender())) };
        if (data is SendData s) { rows.Add(("Получатель", Display.Address(s.To))); rows.Add(("Сумма", $"{Units.FormatCoins(s.Value)} {main.Symbol(s.Coin)} (id {s.Coin})")); }
        if (tx.Payload.Length > 0) rows.Add(("Сообщение", Encoding.UTF8.GetString(tx.Payload)));
        rows.Add(("Номер транзакции", tx.Nonce.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        rows.Add(("Уже подписали", string.Join("\n", tx.MultisigSigners().Select(Display.Address))));
        var warnings = new List<string>();
        if (data is SendData { To: var to } && main.LookalikeOf(to) is { } like) warnings.Add(MainViewModel.LookalikeWarning(like));
        var copy = tx.Copy();
        if (!await ConfirmAndSignAsync("Подпись за общий счёт", copy, rows, warnings)) return;
        Text = MultisigText.Of(copy);
    }

    private async Task<bool> ConfirmAndSignAsync(string title, Transaction target, List<(string, string)> rows, List<string> warnings)
    {
        var account = main.Selected?.Account ?? throw new InvalidOperationException("Выберите счёт.");
        if (account.WatchOnly) throw new InvalidOperationException("Этот счёт только для просмотра: подписать нечем.");
        rows.Insert(1, ("Подписывает", $"{account.Name}\n{Display.Address(account.Address)}"));
        rows.Add(("Сеть", $"{main.Shell.Status?.Network} (chain ID {target.ChainId})"));
        var password = main.Shell.Confirm == null ? null : await main.Shell.Confirm(new ConfirmRequest(title, rows, warnings, "Подписать"));
        if (password == null) { Say("Отменено — ничего не подписано.", Tone.Info); return false; }
        if (!await main.Shell.CheckPasswordAsync(password)) { Say("Неверный пароль — ничего не подписано.", Tone.Error); return false; }
        var wallet = await main.WalletAsync();
        wallet.SignMultisig(account, target);
        Say("Подписано. Передайте текст ниже следующему участнику — или отправьте, если подписей уже хватает.", Tone.Success);
        return true;
    }

    private async Task SendAsync()
    {
        if (tx == null || !CanSend) return;
        var wallet = await main.WalletAsync();
        Say("Отправка…", Tone.Progress);
        var outcome = await wallet.SendMultisigAsync(tx, new Progress<TxOutcome>(o => Say($"Отправлено {o.Hash}, ждём блок…", Tone.Progress)));
        switch (outcome.State)
        {
            case TxState.Included: Say($"Готово — выполнено в блоке {Display.Integer(outcome.Height)}. Хеш: {outcome.Hash}", Tone.Success); break;
            case TxState.FailedInBlock: Say($"Транзакция попала в блок {Display.Integer(outcome.Height)}, но не выполнена (код {outcome.Code}): {outcome.Log}. Комиссия списана.", Tone.Error); break;
            case TxState.Rejected: Say($"Сеть отклонила (код {outcome.Code}): {outcome.Log}. Ничего не списано.", Tone.Error); break;
            default: Say(outcome.Log, Tone.Warning); break;
        }
        if (outcome.State is TxState.Included or TxState.FailedInBlock) { usedNonce = Math.Max(usedNonce, tx.Nonce); Refresh(); main.Refresh.Execute(null); }
    }

    // What the pasted text is: whose transaction, what it does, who signed and how much weight that is.
    private async Task ReadAsync()
    {
        tx = MultisigText.Parse(Text);
        info = tx == null ? info : null;
        Signers.Clear(); signedWeight = 0; usedNonce = 0; TextInfo = "";
        if (tx == null) { TextInfo = Text.Trim().Length == 0 ? "" : "Это не транзакция общего счёта: ожидается текст, который начинается с msig:."; Refresh(); return; }
        try
        {
            var wallet = await main.WalletAsync();
            var multisig = tx.Sender();
            var state = await wallet.Backend.AccountAsync(multisig);
            if (MultisigText.Parse(Text) is not { } current || current.Hash != tx.Hash) return;   // replaced meanwhile
            info = state.Multisig;
            usedNonce = state.Nonce;
            if (info == null) { TextInfo = "Адрес этой транзакции — не общий счёт."; Refresh(); return; }
            var signed = tx.MultisigSigners();
            signedWeight = info.WeightOf(signed);
            foreach (var m in info.Members)
            {
                var name = main.Accounts.FirstOrDefault(a => a.Account.Address == m.Address)?.Name ?? main.Shell.AddressBook.NameOf(m.Address) ?? "";
                Signers.Add(new SignerItem(name.Length > 0 ? name : Core.History.Short(m.Address), m.Address.ToString(), m.Weight, signed.Contains(m.Address)));
            }
            TextInfo = $"{Core.History.Describe(tx.DecodeData(), multisig, multisig, main.Symbol)} — с общего счёта {Core.History.Short(multisig)}, номер {tx.Nonce}."
                + (tx.Nonce <= state.Nonce ? "\nЭтот номер общий счёт уже использовал: транзакцию надо собрать заново." : "");
        }
        catch (Exception e) when (e is FormatException or InvalidOperationException) { TextInfo = $"Текст не читается: {e.Message}"; }
        Refresh();
    }

    private void Refresh()
    {
        foreach (var n in new[] { nameof(HasTx), nameof(Progress), nameof(CanSign), nameof(CanSend) }) Changed(n);
        Sign.Refresh(); SendNow.Refresh();
    }

    public void AccountChanged() { Refresh(); MembersChanged(); }
}
