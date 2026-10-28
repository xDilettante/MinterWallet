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

// "Попросить сумму" in the receive panel: a Minter Link (https://bip.to/tx/…) asking for a transfer of this amount of
// this coin to the account, with an optional message; the QR code shows the link instead of the bare address.
public sealed class PaymentRequestViewModel : Observable
{
    private readonly MainViewModel main;
    private string amount = "", message = "", copyLabel = "Копировать";
    private CoinChoice? coin;

    public PaymentRequestViewModel(MainViewModel main)
    {
        this.main = main;
        Copy = new AsyncCommand(CopyAsync, _ => { });
    }

    public ObservableCollection<CoinChoice> Coins { get; } = [];
    public string Amount { get => amount; set { if (Set(ref amount, value)) Update(); } }
    public CoinChoice? Coin { get => coin; set { if (Set(ref coin, value)) Update(); } }
    public string Message { get => message; set { if (Set(ref message, value)) Update(); } }
    public string CopyLabel { get => copyLabel; private set => Set(ref copyLabel, value); }
    public AsyncCommand Copy { get; }

    public string Link
    {
        get
        {
            if (main.Selected == null || Coin == null || Amount.Trim().Length == 0) return "";
            BigInteger value;
            try { value = Units.ParseCoins(Amount.Trim().Replace(',', '.')); } catch (FormatException) { return ""; }
            if (value.Sign <= 0) return "";
            var payload = Encoding.UTF8.GetBytes(Message.Trim());
            return payload.Length > Transaction.MaxPayload ? "" : MinterLink.Of(new SendData(Coin.Id, main.Selected.Account.Address, value), payload).ToString();
        }
    }
    public bool HasLink => Link.Length > 0;
    public string QrText => HasLink ? Link : main.Address;
    public string Problem
    {
        get
        {
            if (Amount.Trim().Length == 0) return "";
            try { if (Units.ParseCoins(Amount.Trim().Replace(',', '.')).Sign <= 0) return "Сумма должна быть больше нуля."; }
            catch (FormatException) { return "Проверьте число: цифры и точка, не больше 18 знаков после неё."; }
            return Encoding.UTF8.GetByteCount(Message.Trim()) > Transaction.MaxPayload ? $"Сообщение — не больше {Transaction.MaxPayload} байт." : "";
        }
    }

    // The coins to ask for: the account's, and the base coin always.
    public void BalancesChanged()
    {
        var keep = Coin?.Id ?? 0;
        Coins.Clear();
        Coins.Add(new CoinChoice(0, main.BaseSymbol));
        foreach (var b in main.Balances.Where(b => b.Coin.Id != 0)) Coins.Add(new CoinChoice(b.Coin.Id, b.Symbol));
        Coin = Coins.FirstOrDefault(c => c.Id == keep) ?? Coins[0];
        Update();
    }

    public void Update()
    {
        foreach (var name in new[] { nameof(Link), nameof(HasLink), nameof(QrText), nameof(Problem) }) Changed(name);
    }

    private async Task CopyAsync()
    {
        if (!HasLink || main.Shell.CopyText == null) return;
        await main.Shell.CopyText(Link);
        CopyLabel = "Скопировано";
        DispatcherTimer.RunOnce(() => CopyLabel = "Копировать", TimeSpan.FromSeconds(2));
    }
}
