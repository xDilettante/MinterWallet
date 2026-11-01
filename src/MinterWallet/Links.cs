using System.Collections.ObjectModel;
using System.Numerics;
using System.Text;
using Avalonia.Threading;
using Minter.Protocol;

namespace MinterWallet;

public sealed record CoinChoice(uint Id, string Symbol)
{
    public override string ToString() => Symbol;
}

// The receive panel explicitly chooses a bare address or a Minter Link payment request. Draft fields survive
// switching modes; an invalid request never silently falls back to a bare-address QR.
public sealed class PaymentRequestViewModel : Observable
{
    private readonly MainViewModel main;
    private string amount = "", message = "", copyLabel = "Копировать запрос";
    private CoinChoice? coin;
    private int modeIndex;

    public PaymentRequestViewModel(MainViewModel main)
    {
        this.main = main;
        Copy = new AsyncCommand(CopyAsync, _ => { });
    }

    public ObservableCollection<CoinChoice> Coins { get; } = [];
    public int ModeIndex
    {
        get => modeIndex;
        set { if (value is 0 or 1 && Set(ref modeIndex, value)) { Changed(nameof(IsRequest)); Update(); } }
    }
    public bool IsRequest => ModeIndex == 1;
    public string Amount { get => amount; set { if (Set(ref amount, value)) Update(); } }
    public CoinChoice? Coin { get => coin; set { if (Set(ref coin, value)) Update(); } }
    public string Message { get => message; set { if (Set(ref message, value)) Update(); } }
    public string CopyLabel { get => copyLabel; private set => Set(ref copyLabel, value); }
    public AsyncCommand Copy { get; }

    public string Link
    {
        get
        {
            if (!IsRequest || !main.CanReceive || main.Selected == null || Coin == null || Amount.Trim().Length == 0) return "";
            BigInteger value;
            try { value = Units.ParseCoins(Amount.Trim().Replace(',', '.')); } catch (FormatException) { return ""; }
            if (value.Sign <= 0) return "";
            var payload = Encoding.UTF8.GetBytes(Message.Trim());
            return payload.Length > Transaction.MaxPayload ? "" : MinterLink.Of(new SendData(Coin.Id, main.Selected.Account.Address, value), payload).ToString();
        }
    }
    public bool HasLink => Link.Length > 0;
    public string QrText => !main.CanReceive ? "" : IsRequest ? Link : main.Address;
    public bool HasQr => QrText.Length > 0;
    public string QrCaption => !IsRequest ? "QR содержит только адрес" : HasLink
        ? $"Запрос на {Units.FormatCoins(Units.ParseCoins(Amount.Trim().Replace(',', '.')))} {Coin!.Symbol}"
        : "Заполните поля запроса";
    public string Problem
    {
        get
        {
            if (!IsRequest || Amount.Trim().Length == 0) return "";
            if (Coin == null) return "Выберите монету.";
            try { if (Units.ParseCoins(Amount.Trim().Replace(',', '.')).Sign <= 0) return "Сумма должна быть больше нуля."; }
            catch (FormatException) { return "Проверьте число: цифры и точка, не больше 18 знаков после неё."; }
            return Encoding.UTF8.GetByteCount(Message.Trim()) > Transaction.MaxPayload ? $"Сообщение — не больше {Transaction.MaxPayload} байт." : "";
        }
    }

    // The coins to ask for: the account's, and the base coin always.
    public void BalancesChanged()
    {
        var keep = Coin?.Id ?? 0;
        var list = new List<CoinChoice> { new(0, main.BaseSymbol) };
        foreach (var b in main.Balances.Where(b => b.Coin.Id != 0)) list.Add(new CoinChoice(b.Coin.Id, b.Symbol));
        MainViewModel.SyncList(Coins, list);
        Coin = Coins.FirstOrDefault(c => c.Id == keep) ?? Coins[0];
        Update();
    }

    public void Update()
    {
        CopyLabel = "Копировать запрос";
        foreach (var name in new[] { nameof(Link), nameof(HasLink), nameof(QrText), nameof(HasQr), nameof(QrCaption), nameof(Problem) }) Changed(name);
    }

    private async Task CopyAsync()
    {
        if (!HasLink || main.Shell.CopyText == null || !main.Derivation.PrepareReceive()) return;
        await main.Shell.CopyText(Link);
        CopyLabel = "Скопировано";
        DispatcherTimer.RunOnce(() => CopyLabel = "Копировать запрос", TimeSpan.FromSeconds(2));
    }
}
