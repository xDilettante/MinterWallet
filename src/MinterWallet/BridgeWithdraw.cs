using System.Collections.ObjectModel;
using System.Numerics;
using System.Text;
using Avalonia.Threading;
using Minter.Protocol;
using MinterWallet.Core;

namespace MinterWallet;

// «В BSC / Ethereum»: a coin out through Minter Hub. The coins the bridge carries, the network, the 0x recipient
// (EIP-55; the account's own address there is one click — the same key), what the bridge keeps (1 % and the gas,
// estimated from its latest withdrawals of that coin), and after sending, the bridge's settlement and a link to the
// recipient on bscscan/etherscan. Mainnet only: the bridge has no other.
public sealed class BridgeViewModel : ScreenModel
{
    private readonly MainViewModel main;
    private BalanceItem? coin;
    private int networkIndex;
    private string recipient = "", amount = "", estimate = "", tracking = "";
    private IReadOnlyList<BridgeTx> activity = [];
    private DateTime activityAt;
    private string? link;

    public BridgeViewModel(MainViewModel main)
    {
        this.main = main;
        SendNow = new AsyncCommand(SendAsync, Fail, () => IsAvailable);
        Own = new Command(() => { if (main.Selected != null) Recipient = Bridge.OwnAddress(main.Selected.Account.Address); });
        All = new Command(() => { if (Coin != null) Amount = Units.FormatCoins(Coin.Value); });
        OpenLink = new AsyncCommand(async () => { if (link != null && main.Shell.OpenUrl != null) await main.Shell.OpenUrl(new Uri(link)); }, Fail);
    }

    public bool IsAvailable => main.Shell.Status?.Network.StartsWith("minter-mainnet", StringComparison.Ordinal) == true;
    public bool IsUnavailable => !IsAvailable;
    public ObservableCollection<BalanceItem> Coins { get; } = [];
    public BalanceItem? Coin
    {
        get => coin;
        set
        {
            if (!Set(ref coin, value)) return;
            // A coin that goes to one network only switches to it.
            if (value != null && Bridge.NetworksOf(value.Coin.Id).ToList() is [var only]) NetworkIndex = (int)only;
            Changed(nameof(NetworkHint)); Changed(nameof(Available)); _ = EstimateAsync();
        }
    }
    public int NetworkIndex { get => networkIndex; set { if (value >= 0 && Set(ref networkIndex, value)) { Changed(nameof(NetworkHint)); Changed(nameof(RecipientCheck)); _ = EstimateAsync(); } } }
    public BridgeNetwork Network => (BridgeNetwork)networkIndex;
    public string NetworkHint => Coin == null ? "" : Bridge.TokenOf(Coin.Coin.Id, Network) is { } t
        ? $"{Coin.Symbol} придёт в {Bridge.NameOf(Network)} как {(t.Contract == null ? $"родная монета сети ({t.Denom.ToUpperInvariant()})" : $"токен {t.Contract}")}."
        : $"{Coin.Symbol} через мост уходит только в {string.Join(" и ", Bridge.NetworksOf(Coin.Coin.Id).Select(Bridge.NameOf))}.";
    public string Recipient { get => recipient; set { if (Set(ref recipient, value.Trim())) { Changed(nameof(RecipientCheck)); Changed(nameof(RecipientTone)); } } }
    public string RecipientCheck
    {
        get
        {
            if (Recipient.Length == 0) return "";
            if (Bridge.RecipientProblem(Recipient) is { } problem) return problem;
            var own = main.Accounts.FirstOrDefault(a => string.Equals(Bridge.OwnAddress(a.Account.Address), Bridge.Checksum(Recipient), StringComparison.Ordinal));
            return own != null ? $"Это ваш адрес в {Bridge.NameOf(Network)}: тот же ключ, что у счёта «{own.Name}» (откроется в MetaMask по его фразе или ключу)."
                : $"Адрес в {Bridge.NameOf(Network)}: {Bridge.Checksum(Recipient)}. Если это биржа, убедитесь, что она принимает {Coin?.Symbol ?? "эту монету"} именно в {Bridge.NameOf(Network)}.";
        }
    }
    public Tone RecipientTone => Recipient.Length == 0 ? Tone.None : Bridge.RecipientProblem(Recipient) != null ? Tone.Error : Tone.Success;
    public string Amount { get => amount; set { if (Set(ref amount, value)) _ = EstimateAsync(); } }
    public string Available => Coin == null ? "" : $"Доступно: {Coin.AmountShort} {Coin.Symbol}";
    public string Estimate { get => estimate; private set => Set(ref estimate, value); }
    public string Tracking { get => tracking; private set { Set(ref tracking, value); Changed(nameof(HasLink)); } }
    public bool HasLink => link != null && Tracking.Length > 0;
    public AsyncCommand SendNow { get; }
    public Command Own { get; }
    public Command All { get; }
    public AsyncCommand OpenLink { get; }

    public void BalancesChanged()
    {
        var keep = Coin?.Coin.Id;
        Coins.Clear();
        foreach (var b in main.Balances.Where(b => Bridge.NetworksOf(b.Coin.Id).Any())) Coins.Add(b);
        Coin = Coins.FirstOrDefault(c => c.Coin.Id == keep) ?? Coins.FirstOrDefault();
        Changed(nameof(IsAvailable)); Changed(nameof(IsUnavailable)); SendNow.Refresh();
    }

    // The bridge's latest work, read again after two minutes.
    private async Task<IReadOnlyList<BridgeTx>> ActivityAsync(bool fresh = false)
    {
        if (!fresh && activity.Count > 0 && DateTime.UtcNow - activityAt < TimeSpan.FromMinutes(2)) return activity;
        activity = await main.Shell.Explorer.BridgeActivityAsync();
        activityAt = DateTime.UtcNow;
        return activity;
    }

    // 1 % exactly; the gas as the bridge kept it in its latest withdrawals of this coin to this network.
    private async Task EstimateAsync()
    {
        if (!IsAvailable || Coin is not { } c || Bridge.TokenOf(c.Coin.Id, Network) == null) { Estimate = ""; return; }
        BigInteger value;
        try { value = Units.ParseCoins(Amount.Trim().Replace(',', '.')); } catch (FormatException) { Estimate = ""; return; }
        if (value.Sign <= 0) { Estimate = ""; return; }
        var fee = Bridge.HubFee(value);
        try
        {
            var gas = Bridge.RecentGas(await ActivityAsync(), c.Coin.Id, Network);
            if (Coin != c) return;
            if (gas.Count == 0)
            {
                Estimate = $"Мост удержит 1 % — {Display.Amount(fee)} {c.Symbol} — и плату за газ в {Bridge.NameOf(Network)}, её он считает сам: последних выводов {c.Symbol} в {Bridge.NameOf(Network)} нет, чтобы её оценить.";
                return;
            }
            var typical = gas.Order().ElementAt(gas.Count / 2);
            var arrives = value - fee - typical;
            Estimate = $"Мост удержит 1 % — {Display.Amount(fee)} {c.Symbol} — и газ в {Bridge.NameOf(Network)}: в последних {gas.Count} выводах ≈ {Display.Amount(typical)} {c.Symbol} (от {Display.Amount(gas.Min())} до {Display.Amount(gas.Max())})."
                + (arrives.Sign > 0 ? $"\nПридёт ≈ {Display.Amount(arrives)} {c.Symbol}." : "\nЭтого не хватит даже на комиссии моста.");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Estimate = $"Мост удержит 1 % — {Display.Amount(fee)} {c.Symbol} — и плату за газ в {Bridge.NameOf(Network)}; оценить её не удалось: обозреватель Minter не ответил.";
        }
    }

    private async Task SendAsync()
    {
        if (!IsAvailable) { Say("Мост Minter Hub работает только в основной сети Minter.", Tone.Error); return; }
        if (Coin is not { } c) { Say("Выберите монету, которую мост выводит.", Tone.Error); return; }
        if (Bridge.TokenOf(c.Coin.Id, Network) is not { } token) { Say(NetworkHint, Tone.Error); return; }
        if (Bridge.RecipientProblem(Recipient) is { } problem) { Say(problem, Tone.Error); return; }
        if (!MainViewModel.TryAmount(Amount, this, out var value)) return;
        var to = Bridge.Checksum(Recipient);
        var payload = Encoding.UTF8.GetBytes(Bridge.Payload(Network, to));
        var net = Bridge.NameOf(Network);
        var rows = new List<(string, string)>
        {
            ("Куда", $"{net} — через мост Minter Hub"), ("Получатель", to), ("Монета", $"{c.Symbol} (id {c.Coin.Id}) → {(token.Contract ?? token.Denom.ToUpperInvariant())}"),
            ("Отправляете", $"{Units.FormatCoins(value)} {c.Symbol}"), ("Мост удержит", $"1 % — {Units.FormatCoins(Bridge.HubFee(value))} {c.Symbol} — и газ в {net}"),
            ("Адрес моста", Bridge.Multisig.ToString()),
        };
        var warnings = new List<string>
        {
            $"Перевод на мост не отменить: монеты уйдут на {to} в сети {net}. Проверьте адрес и сеть — у биржи или кошелька, куда выводите.",
            "Minter Hub — мост команды Minter, не этого кошелька: кошелёк только отправляет ему перевод с командой.",
        };
        var outcome = await main.ConfirmAndSendAsync($"Вывод в {net}", new SendData(c.Coin.Id, Bridge.Multisig, value), 0, payload, rows, warnings, this);
        if (outcome?.Succeeded != true) return;
        Amount = "";
        link = Bridge.ExplorerOf(Network, to);
        Tracking = "Перевод на мост в блоке. Ждём, когда мост его обработает — обычно несколько минут…";
        _ = TrackAsync(outcome.Hash, c.Symbol, net, to);
    }

    // The bridge's multisend that settles this withdrawal, looked for every 20 s for up to 30 minutes.
    private async Task TrackAsync(string hash, string symbol, string net, string to)
    {
        var until = DateTime.UtcNow.AddMinutes(30);
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(TimeSpan.FromSeconds(20));
            try
            {
                var latest = await ActivityAsync(fresh: true);
                if (latest.FirstOrDefault(t => string.Equals(t.Hash, hash, StringComparison.OrdinalIgnoreCase)) is { } mine && Bridge.SettlementOf(mine, latest) is { } s)
                {
                    Tracking = $"Мост обработал вывод: удержал 1 % ({Display.Amount(s.HubFee)} {symbol}) и газ ({Display.Amount(s.GasFee)} {symbol}); "
                        + $"{Display.Amount(mine.Value - s.HubFee - s.GasFee)} {symbol} отправлены в {net} на {to}. Зачисление видно в обозревателе сети.";
                    return;
                }
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { }
        }
        Tracking = $"За 30 минут мост вывод не отметил — так бывает при загрузке сетей. Проверьте получателя в обозревателе {net} позже.";
    }
}
