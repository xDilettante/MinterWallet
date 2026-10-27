using System.Collections.ObjectModel;
using System.Globalization;
using System.Numerics;
using Avalonia.Threading;
using Minter.Protocol;
using MinterWallet.Core;

namespace MinterWallet;

public sealed record PositionItem(PoolInfo Pool, string Symbol0, string Symbol1, BigInteger Amount0, BigInteger Amount1, BigInteger Liquidity)
{
    public string Title => $"{Symbol0} / {Symbol1}";
    public string AmountsText => $"{Display.Amount(Amount0)} {Symbol0} + {Display.Amount(Amount1)} {Symbol1}";
    public string ShareText => Pool.Liquidity.IsZero ? "" : $"ваша доля {(decimal)(Liquidity * 1_000_000 / Pool.Liquidity) / 10_000:0.####} %";
}

// Liquidity: the account's positions (its LP-<pool> tokens) with a share and a way out, and adding to a pool of two
// coins at the pool's price (the second amount follows the first; 1 % tolerance either way).
public sealed class LiquidityViewModel : ScreenModel
{
    private readonly MainViewModel main;
    private readonly DispatcherTimer quoteTimer;
    private IReadOnlyList<PoolInfo>? pools;
    private object? poolsFrom;
    private BalanceItem? coinA;
    private PositionItem? selected;
    private string amountA = "", needText = "";
    private (BigInteger ReserveA, BigInteger ReserveB)? reserves;

    public LiquidityViewModel(MainViewModel main)
    {
        this.main = main;
        Load = new AsyncCommand(LoadAsync, Fail);
        Add = new AsyncCommand(AddAsync, Fail);
        Withdraw = new RelayCommand(async p =>
        {
            if (Selected is not { } pos) { Say("Выберите позицию в списке.", Tone.Warning); return; }
            if (p is string percent) await RunAsync(() => WithdrawAsync(pos, int.Parse(percent, CultureInfo.InvariantCulture)));
        });
        CoinB = new CoinPickerViewModel(() => main.CoinOptions(CoinA?.Coin.Id), () => main.HasCatalog, main.LookupCoinAsync);
        CoinB.SelectedChanged += InputChanged;
        quoteTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        quoteTimer.Tick += async (_, _) => { quoteTimer.Stop(); await RunAsync(QuoteAsync); };
    }

    public ObservableCollection<PositionItem> Positions { get; } = [];
    public ObservableCollection<BalanceItem> Coins { get; } = [];
    public bool HasPositions => Positions.Count > 0;
    public PositionItem? Selected { get => selected; set => Set(ref selected, value); }
    public BalanceItem? CoinA { get => coinA; set { if (Set(ref coinA, value)) InputChanged(); } }
    public CoinPickerViewModel CoinB { get; }
    public string AmountA { get => amountA; set { if (Set(ref amountA, value)) InputChanged(); } }
    public string NeedText { get => needText; private set => Set(ref needText, value); }
    public AsyncCommand Load { get; }
    public AsyncCommand Add { get; }
    public RelayCommand Withdraw { get; }

    public void BalancesChanged()
    {
        var keep = CoinA?.Coin.Id;
        Coins.Clear();
        foreach (var b in main.Balances.Where(b => !b.Symbol.StartsWith("LP-", StringComparison.Ordinal))) Coins.Add(b);
        coinA = Coins.FirstOrDefault(c => c.Coin.Id == keep) ?? Coins.FirstOrDefault();
        Changed(nameof(CoinA));
    }

    public void Reset() { pools = null; poolsFrom = null; Positions.Clear(); Changed(nameof(HasPositions)); }

    private async Task RunAsync(Func<Task> action) { try { await action(); } catch (Exception e) { Fail(e); } }

    private void InputChanged()
    {
        reserves = null; NeedText = "";
        quoteTimer?.Stop();
        if (CoinA != null && CoinB.Selected != null && Amount(AmountA) > 0) quoteTimer?.Start();
    }

    private static BigInteger Amount(string text) { try { return Units.ParseCoins(text); } catch (FormatException) { return 0; } }

    // The second coin the pool asks for this much of the first, at its current price.
    private async Task QuoteAsync()
    {
        if (CoinA == null || CoinB.Selected is not { } b || Amount(AmountA) is var a && a <= 0) return;
        await main.WalletAsync();
        if (main.Shell.Backend == null) return;
        var pool = await main.Shell.Backend.SwapPoolAsync(CoinA.Coin.Id, b.Id);
        if (pool is not ({ Sign: > 0 } ra, var rb)) { NeedText = $"Пула {CoinA.Symbol} / {b.Symbol} нет: добавить можно только в существующий пул."; return; }
        reserves = (ra, rb);
        NeedText = $"Вместе с ним уйдёт ≈ {Display.Amount(a * rb / ra)} {b.Symbol} — по текущей цене пула.";
    }

    private async Task LoadAsync()
    {
        var wallet = await main.WalletAsync();
        if (main.Selected == null || main.Shell.Backend == null) return;
        var backend = main.Shell.Backend;
        if (!ReferenceEquals(poolsFrom, backend) || pools == null) { pools = await backend.SwapPoolsAsync(); poolsFrom = backend; }
        var me = main.Selected.Account.Address;
        // The account as it is now (the overview may still show the state before the last transaction).
        var account = await wallet.Backend.AccountAsync(me);
        Positions.Clear();
        foreach (var lp in account.Balances.Where(b => b.Coin.Symbol.StartsWith("LP-", StringComparison.Ordinal)))
        {
            if (!uint.TryParse(lp.Coin.Symbol[3..], out var id) || pools.FirstOrDefault(p => p.Id == id) is not { } pool) continue;
            if (await backend.ProviderAsync(pool.Coin0, pool.Coin1, me) is not { } part || part.Liquidity.IsZero) continue;
            Positions.Add(new PositionItem(pool, main.Symbol(pool.Coin0), main.Symbol(pool.Coin1), part.Amount0, part.Amount1, part.Liquidity));
        }
        Selected = Positions.FirstOrDefault();
        Changed(nameof(HasPositions));
    }

    private async Task AddAsync()
    {
        if (CoinA == null || CoinB.Selected is not { } b) { Say("Выберите обе монеты.", Tone.Error); return; }
        if (!MainViewModel.TryAmount(AmountA, this, out var a)) return;
        await QuoteAsync();
        if (reserves is not { } r) { Say(NeedText.Length > 0 ? NeedText : "Пула этих монет нет.", Tone.Error); return; }
        var need = a * r.ReserveB / r.ReserveA;
        var max = need * 101 / 100;
        var outcome = await main.ConfirmAndSendAsync("Добавление ликвидности", new AddLiquidityData(CoinA.Coin.Id, b.Id, a, max), 0, [],
            [("Пул", $"{CoinA.Symbol} / {b.Symbol}"), ("Вносите", $"{Units.FormatCoins(a)} {CoinA.Symbol}\n≈ {Units.FormatCoins(need)} {b.Symbol}, не больше {Units.FormatCoins(max)}")],
            ["Взамен вы получите токены пула LP: по ним вернёте свою долю обеих монет вместе с заработанными комиссиями."], this);
        if (outcome?.Succeeded == true) { AmountA = ""; await LoadAsync(); }
    }

    private async Task WithdrawAsync(PositionItem p, int percent)
    {
        var liquidity = p.Liquidity * percent / 100;
        BigInteger Min(BigInteger amount) => amount * percent / 100 * 99 / 100;
        var outcome = await main.ConfirmAndSendAsync("Вывод ликвидности", new RemoveLiquidityData(p.Pool.Coin0, p.Pool.Coin1, liquidity, Min(p.Amount0), Min(p.Amount1)), 0, [],
            [("Пул", p.Title), ("Выводите", $"{percent} % доли\n≈ {Display.Amount(p.Amount0 * percent / 100)} {p.Symbol0} + {Display.Amount(p.Amount1 * percent / 100)} {p.Symbol1}")], [], this);
        if (outcome?.Succeeded == true) await LoadAsync();
    }
}

public sealed record OrderItem(OrderInfo Order)
{
    public string Title => $"Продать {Display.Amount(Order.WantSell)} {Order.Sell.Symbol} за {Display.Amount(Order.WantBuy)} {Order.Buy.Symbol}";
    public string Caption => $"№ {Order.Id} · с блока {Display.Integer(Order.Height)}";
}

// Limit orders: sell a coin for another at the price asked, up to five times better than the pool's (the network
// refuses an order below the pool's price, which would fill at once, or more than five times above it). The account's
// open orders are found through its history (the order id is a tag of the transaction that placed it).
public sealed class OrdersViewModel : ScreenModel
{
    private readonly MainViewModel main;
    private readonly DispatcherTimer quoteTimer;
    private BalanceItem? sell;
    private string amountSell = "", amountBuy = "", range = "";

    public OrdersViewModel(MainViewModel main)
    {
        this.main = main;
        Load = new AsyncCommand(LoadAsync, Fail);
        Place = new AsyncCommand(PlaceAsync, Fail);
        Cancel = new RelayCommand(async o => { if (o is OrderItem item) { try { await CancelAsync(item); } catch (Exception e) { Fail(e); } } });
        Buy = new CoinPickerViewModel(() => main.CoinOptions(Sell?.Coin.Id), () => main.HasCatalog, main.LookupCoinAsync);
        Buy.SelectedChanged += InputChanged;
        quoteTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        quoteTimer.Tick += async (_, _) => { quoteTimer.Stop(); try { await RangeAsync(); } catch (Exception e) { Fail(e); } };
    }

    public ObservableCollection<BalanceItem> Coins { get; } = [];
    public ObservableCollection<OrderItem> Mine { get; } = [];
    public bool HasMine => Mine.Count > 0;
    public BalanceItem? Sell { get => sell; set { if (Set(ref sell, value)) InputChanged(); } }
    public CoinPickerViewModel Buy { get; }
    public string AmountSell { get => amountSell; set { if (Set(ref amountSell, value)) InputChanged(); } }
    public string AmountBuy { get => amountBuy; set => Set(ref amountBuy, value); }
    public string Range { get => range; private set => Set(ref range, value); }
    public AsyncCommand Load { get; }
    public AsyncCommand Place { get; }
    public RelayCommand Cancel { get; }

    public void BalancesChanged()
    {
        var keep = Sell?.Coin.Id;
        Coins.Clear();
        foreach (var b in main.Balances.Where(b => !b.Symbol.StartsWith("LP-", StringComparison.Ordinal))) Coins.Add(b);
        sell = Coins.FirstOrDefault(c => c.Coin.Id == keep) ?? Coins.FirstOrDefault();
        Changed(nameof(Sell));
    }

    public void Reset() { Mine.Clear(); Changed(nameof(HasMine)); }

    private void InputChanged()
    {
        Range = "";
        quoteTimer?.Stop();
        if (Sell != null && Buy.Selected != null) quoteTimer?.Start();
    }

    // What the pool gives now for the amount to sell, and so the range the network accepts for the order.
    private async Task RangeAsync()
    {
        if (Sell == null || Buy.Selected is not { } b || main.Shell.Backend == null) return;
        BigInteger value;
        try { value = Units.ParseCoins(AmountSell); } catch (FormatException) { return; }
        if (value <= 0) return;
        if (await main.Shell.Backend.SwapPoolAsync(Sell.Coin.Id, b.Id) is not ({ Sign: > 0 } rs, var rb)) { Range = $"Пула {Sell.Symbol} / {b.Symbol} нет: ордер выставляется только в существующем пуле."; return; }
        var market = value * rb / rs;
        Range = $"Пул сейчас даёт ≈ {Display.Amount(market)} {b.Symbol}. Просить можно от этого до {Display.Amount(market * 5)} {b.Symbol}: ордер исполнится, когда цена дойдёт.";
        if (AmountBuy.Length == 0) AmountBuy = Units.FormatCoins(market * 11 / 10);
    }

    private async Task LoadAsync()
    {
        var wallet = await main.WalletAsync();
        if (main.Selected == null || main.Shell.Backend == null) return;
        var me = main.Selected.Account.Address;
        var ids = new HashSet<uint>();
        for (var page = 1; page <= 3; page++)
        {
            var records = await wallet.Backend.HistoryAsync(me, page, 50);
            foreach (var r in records)
                if (r.Code == 0 && r.Tags.TryGetValue("tx.order_id", out var id) && uint.TryParse(id, out var n)
                    && Transaction.Decode(r.Raw) is var tx && tx.Type == TxType.AddLimitOrder && tx.Sender() == me) ids.Add(n);
            if (records.Count < 50) break;
        }
        var open = await main.Shell.Backend.LimitOrdersAsync(ids);
        Mine.Clear();
        foreach (var o in open.Where(o => o.Owner == me).OrderByDescending(o => o.Height)) Mine.Add(new OrderItem(o));
        Changed(nameof(HasMine));
    }

    private async Task PlaceAsync()
    {
        if (Sell == null || Buy.Selected is not { } b) { Say("Выберите, что продать и что получить.", Tone.Error); return; }
        if (!MainViewModel.TryAmount(AmountSell, this, out var value)) return;
        if (!MainViewModel.TryAmount(AmountBuy, this, out var want)) return;
        var outcome = await main.ConfirmAndSendAsync("Лимитный ордер", new AddLimitOrderData(Sell.Coin.Id, value, b.Id, want), 0, [],
            [("Продать", $"{Units.FormatCoins(value)} {Sell.Symbol} (id {Sell.Coin.Id})"), ("Получить", $"{Units.FormatCoins(want)} {b.Symbol} (id {b.Id})")],
            ["Монеты на продажу сразу уходят в ордер и возвращаются, если его отменить."], this);
        if (outcome?.Succeeded == true) { AmountSell = ""; AmountBuy = ""; await LoadAsync(); }
    }

    private async Task CancelAsync(OrderItem item)
    {
        var outcome = await main.ConfirmAndSendAsync("Отмена ордера", new RemoveLimitOrderData(item.Order.Id), 0, [], [("Ордер", $"{item.Title}\n{item.Caption}")], [], this);
        if (outcome?.Succeeded == true) await LoadAsync();
    }
}
