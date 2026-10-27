using System.Collections.ObjectModel;
using System.Numerics;
using System.Security.Cryptography;
using Minter.Protocol;
using MinterWallet.Core;

namespace MinterWallet;

// Minter checks: a promise of coins, signed offline by the issuer, that whoever knows its password can redeem once
// before its due block. The issuer's balance is charged only at redemption, together with the commission.
public sealed class CheckIssueViewModel : ScreenModel
{
    private const long BlocksPerDay = 17280;   // 5-second blocks, as the network's schedule counts them
    private readonly MainViewModel main;
    private BalanceItem? coin;
    private string amount = "", password = "", days = "30", issued = "", issuedInfo = "";

    public CheckIssueViewModel(MainViewModel main)
    {
        this.main = main;
        Issue = new AsyncCommand(IssueAsync, Fail);
        Copy = new AsyncCommand(async () => { if (issued.Length > 0 && main.Shell.CopyText != null) { await main.Shell.CopyText(issued); Say("Чек скопирован. Пароль передайте отдельно.", Tone.Success); } }, Fail);
        Clear = new Command(() => { Issued = ""; IssuedInfo = ""; Say(""); });
    }

    public ObservableCollection<BalanceItem> Coins { get; } = [];
    public BalanceItem? Coin { get => coin; set { Set(ref coin, value); Changed(nameof(Available)); } }
    public string Available => Coin == null ? "" : $"Доступно: {Coin.AmountShort} {Coin.Symbol}";
    public string Amount { get => amount; set => Set(ref amount, value); }
    public string Password { get => password; set => Set(ref password, value); }
    public string Days { get => days; set => Set(ref days, value); }
    public string Issued { get => issued; private set { Set(ref issued, value); Changed(nameof(HasIssued)); } }
    public string IssuedInfo { get => issuedInfo; private set => Set(ref issuedInfo, value); }
    public bool HasIssued => Issued.Length > 0;
    public AsyncCommand Issue { get; }
    public AsyncCommand Copy { get; }
    public Command Clear { get; }

    public void BalancesChanged()
    {
        var keep = Coin?.Coin.Id;
        Coins.Clear();
        foreach (var b in main.Balances) Coins.Add(b);
        Coin = Coins.FirstOrDefault(c => c.Coin.Id == keep) ?? Coins.FirstOrDefault(c => c.Coin.Id == 0) ?? Coins.FirstOrDefault();
    }

    private async Task IssueAsync()
    {
        var account = main.Selected?.Account;
        if (account == null || account.WatchOnly) { Say("Этот счёт только для просмотра: выписать чек нечем.", Tone.Error); return; }
        if (Coin == null) { Say("Выберите монету.", Tone.Error); return; }
        if (!MainViewModel.TryAmount(Amount, this, out var value)) return;
        if (Password.Length < 4) { Say("Пароль чека — не короче 4 символов: без него чек не погасить.", Tone.Error); return; }
        if (!int.TryParse(Days, out var d) || d is < 1 or > 365) { Say("Срок — от 1 до 365 дней.", Tone.Error); return; }
        var wallet = await main.WalletAsync();
        var height = (await wallet.Backend.StatusAsync()).Height;
        var due = (ulong)(height + d * BlocksPerDay);
        var text = $"{Units.FormatCoins(value)} {Coin.Coin.Symbol} (id {Coin.Coin.Id})";
        var confirm = main.Shell.Confirm == null ? null : await main.Shell.Confirm(new ConfirmRequest("Выписать чек",
            [("Чек на", text), ("Со счёта", $"{account.Name}\n{AddressBook.Grouped(account.Address)}"), ("Действует", $"до блока {Display.Integer((long)due)}, ≈ {d} дн.")],
            ["Сумма и комиссия за погашение спишутся с этого счёта, когда чек погасят. До тех пор держите их на счёте — иначе погасить не выйдет.",
             "Кто знает чек и его пароль — заберёт монеты. Передавайте пароль отдельно от чека."], "Выписать чек"));
        if (confirm == null) { Say("Отменено.", Tone.Info); return; }
        if (!await main.Shell.CheckPasswordAsync(confirm)) { Say("Неверный пароль кошелька — чек не выписан.", Tone.Error); return; }
        using var key = main.Shell.Session!.OpenKey(account);
        var check = Check.Issue(key, Password, RandomNumberGenerator.GetBytes(8), wallet.ChainId, due, Coin.Coin.Id, value, gasCoin: 0);
        Issued = check.ToString();
        IssuedInfo = $"Чек на {Display.Amount(value)} {Coin.Coin.Symbol}, действует до блока {Display.Integer((long)due)} (≈ {d} дн.).";
        Password = ""; Amount = "";
        Say("Чек выписан. Отправьте его получателю, а пароль — отдельно, другим способом.", Tone.Success);
    }
}

public sealed class CheckRedeemViewModel : ScreenModel
{
    private readonly MainViewModel main;
    private string text = "", password = "", info = "";
    private Tone infoTone;
    private Check? check;

    public CheckRedeemViewModel(MainViewModel main)
    {
        this.main = main;
        Redeem = new AsyncCommand(RedeemAsync, Fail);
        Paste = new AsyncCommand(async () =>
        {
            var pasted = main.Shell.PasteText == null ? null : await main.Shell.PasteText();
            if (string.IsNullOrWhiteSpace(pasted)) { Say("В буфере обмена нет текста.", Tone.Warning); return; }
            Text = pasted.Trim();
        }, Fail);
    }

    public string Text { get => text; set { Set(ref text, value); Read(); } }
    public string Password { get => password; set { Set(ref password, value); if (Tone == Tone.Error) Say(""); } }
    public string Info { get => info; private set => Set(ref info, value); }
    public Tone InfoTone { get => infoTone; private set => Set(ref infoTone, value); }
    public AsyncCommand Redeem { get; }
    public AsyncCommand Paste { get; }

    // What the check promises, read from its own bytes while it is pasted.
    private void Read()
    {
        check = null;
        var t = Text.Trim();
        if (t.Length == 0) { Info = ""; InfoTone = Tone.None; return; }
        try { check = Check.Parse(t); }
        catch (Exception e) when (e is FormatException or ArgumentException or InvalidOperationException) { Info = "Это не чек Minter: он начинается с Mc и состоит из шестнадцатеричных символов."; InfoTone = Tone.Warning; return; }
        string issuer;
        try { issuer = History.Short(check.Issuer()); } catch (Exception e) when (e is FormatException or ArgumentException or InvalidOperationException) { issuer = "?"; }
        Info = $"Чек на {Display.Amount(check.Value)} {main.Symbol(check.Coin)} от {issuer}, действует до блока {Display.Integer((long)check.DueBlock)}.";
        InfoTone = Tone.Success;
    }

    private async Task RedeemAsync()
    {
        if (check == null) { Say("Вставьте чек.", Tone.Error); return; }
        var account = main.Selected?.Account;
        if (account == null || account.WatchOnly) { Say("Этот счёт только для просмотра: погасить чек на него нельзя.", Tone.Error); return; }
        var wallet = await main.WalletAsync();
        if (check.ChainId != wallet.ChainId) { Say("Чек выписан для другой сети.", Tone.Error); return; }
        var height = (await wallet.Backend.StatusAsync()).Height;
        if ((long)check.DueBlock < height) { Say($"Срок чека истёк на блоке {Display.Integer((long)check.DueBlock)}.", Tone.Error); return; }
        var data = check.Redeem(account.Address, Password);
        if (!check.AcceptsProof(account.Address, data.Proof)) { Say("Пароль чека не подходит.", Tone.Error); return; }
        var outcome = await main.ConfirmAndSendAsync("Погашение чека", data, check.GasCoin, [],
            [("Чек", Info), ("На счёт", $"{account.Name}\n{AddressBook.Grouped(account.Address)}")],
            ["Комиссию за погашение платит тот, кто выписал чек."], this);
        if (outcome?.Succeeded == true) { Text = ""; Password = ""; }
    }
}
