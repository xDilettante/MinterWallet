using System.Collections.ObjectModel;
using System.Numerics;
using System.Text;
using Avalonia.Threading;
using Minter.Protocol;
using MinterWallet.Core;

namespace MinterWallet;

public sealed record TokenItem(CoinDetails Info, BigInteger Balance)
{
    public string Symbol => Info.Symbol;
    public string Name => Info.Name.Length > 0 ? Info.Name : Info.Symbol;
    public string Initial => Symbol.Length > 0 ? Symbol[..1] : "?";
    public bool IsToken => Info.Crr == 0;
    public string SupplyText => $"выпущено {Display.Amount(Info.Volume)} из {Display.Amount(Info.MaxSupply)}";
    public string Flags => !IsToken ? "монета с резервом" : (Info.Mintable, Info.Burnable) switch
    {
        (true, true) => "можно выпускать и сжигать",
        (true, false) => "можно выпускать",
        (false, true) => "можно сжигать",
        _ => "выпуск закрыт",
    };
    public string BalanceText => $"у вас {Display.Amount(Balance)}";
    public BigInteger Room => Info.MaxSupply - Info.Volume;
}

// «Токены»: the account's own tokens (found among its balances, or by ticker) with minting, burning, a new owner and
// re-creation; creating a token, with its price shown while the ticker is typed; a new pool for two coins.
public sealed class TokensViewModel : ScreenModel
{
    private static readonly BigInteger MaxSupplyLimit = BigInteger.Pow(10, 15) * Units.PipPerCoin;
    private readonly MainViewModel main;
    private readonly DispatcherTimer costTimer;
    private int modeIndex;
    private TokenItem? selected;
    private string mintAmount = "", burnAmount = "", newOwner = "", find = "";
    private string name = "", symbol = "", initial = "", maxSupply = "", cost = "";
    private bool mintable = true, burnable = true, recreating;
    private BalanceItem? coinA, coinB;
    private string amountA = "", amountB = "", poolText = "";

    public TokensViewModel(MainViewModel main)
    {
        this.main = main;
        Load = new AsyncCommand(() => ReadAsync(true), Fail);
        Find = new AsyncCommand(FindAsync, Fail);
        Mint = new AsyncCommand(MintAsync, Fail, () => Selected is { IsToken: true, Info.Mintable: true });
        Burn = new AsyncCommand(BurnAsync, Fail, () => Selected is { IsToken: true, Info.Burnable: true });
        GiveAway = new AsyncCommand(GiveAwayAsync, Fail, () => Selected != null);
        StartRecreate = new Command(() =>
        {
            if (Selected is not { IsToken: true } t) return;
            recreating = true;
            Symbol = t.Symbol; Name = t.Info.Name; Initial = ""; MaxSupply = ""; Mintable = t.Info.Mintable; Burnable = t.Info.Burnable;
            foreach (var n in new[] { nameof(Recreating), nameof(CreateLabel), nameof(CreateTitle) }) Changed(n);
            ModeIndex = 1;
        }, () => Selected is { IsToken: true });
        Create = new AsyncCommand(CreateAsync, Fail);
        CancelRecreate = new Command(() => { recreating = false; Symbol = ""; Name = ""; foreach (var n in new[] { nameof(Recreating), nameof(CreateLabel), nameof(CreateTitle) }) Changed(n); });
        CreatePool = new AsyncCommand(CreatePoolAsync, Fail);
        // The price of a token depends on the length of its ticker: asked of the source 0.6 s after the last change.
        costTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        costTimer.Tick += async (_, _) => { costTimer.Stop(); await CostAsync(); };
    }

    public int ModeIndex
    {
        get => modeIndex;
        set
        {
            if (value < 0 || !Set(ref modeIndex, value)) return;
            foreach (var n in new[] { nameof(IsMine), nameof(IsCreate), nameof(IsPool) }) Changed(n);
            if (value == 1 && Prices.Count == 0) _ = LoadPricesAsync();
        }
    }
    public bool IsMine => modeIndex == 0;
    public bool IsCreate => modeIndex == 1;
    public bool IsPool => modeIndex == 2;

    // My tokens
    public ObservableCollection<TokenItem> Mine { get; } = [];
    public bool HasMine => Mine.Count > 0;
    public TokenItem? Selected
    {
        get => selected;
        set
        {
            if (!Set(ref selected, value)) return;
            foreach (var n in new[] { nameof(HasSelected), nameof(SelectedTitle), nameof(MintHint), nameof(CanMint), nameof(CanBurn) }) Changed(n);
            Mint.Refresh(); Burn.Refresh(); GiveAway.Refresh(); StartRecreate.Refresh();
        }
    }
    public bool HasSelected => Selected != null;
    public string SelectedTitle => Selected == null ? "" : $"{Selected.Symbol} · id {Selected.Info.Id}";
    public bool CanMint => Selected is { IsToken: true, Info.Mintable: true };
    public bool CanBurn => Selected is { IsToken: true, Info.Burnable: true };
    public string MintHint => Selected is { IsToken: true, Info.Mintable: true } t ? $"Можно выпустить ещё до {Display.Amount(t.Room)} {t.Symbol}." : "";
    public string MintAmount { get => mintAmount; set => Set(ref mintAmount, value); }
    public string BurnAmount { get => burnAmount; set => Set(ref burnAmount, value); }
    public string NewOwner { get => newOwner; set => Set(ref newOwner, value); }
    public string FindText { get => find; set => Set(ref find, value); }
    public AsyncCommand Load { get; }
    public AsyncCommand Find { get; }
    public AsyncCommand Mint { get; }
    public AsyncCommand Burn { get; }
    public AsyncCommand GiveAway { get; }
    public Command StartRecreate { get; }

    // Create (or re-create)
    public string Name { get => name; set { if (Set(ref name, value)) InputChanged(); } }
    public string Symbol { get => symbol; set { if (Set(ref symbol, value.Trim().ToUpperInvariant())) InputChanged(); } }
    public string Initial { get => initial; set { if (Set(ref initial, value)) InputChanged(); } }
    public string MaxSupply { get => maxSupply; set { if (Set(ref maxSupply, value)) InputChanged(); } }
    public bool Mintable { get => mintable; set { if (Set(ref mintable, value)) InputChanged(); } }
    public bool Burnable { get => burnable; set { if (Set(ref burnable, value)) InputChanged(); } }
    public string Cost { get => cost; private set => Set(ref cost, value); }
    public ObservableCollection<string> Prices { get; } = [];

    // What the ticker field and the two supply fields mean right now, before anything is sent.
    public string TickerHint => Symbol.Length == 0 ? "3–10 заглавных латинских букв и цифр, первая — буква. Короче тикер — дороже."
        : CoinSymbol.IsValidTicker(Symbol) ? $"{Symbol}: {Symbol.Length} {Chars(Symbol.Length)}." : "Тикер — от 3 до 10 заглавных латинских букв и цифр, первая — буква.";
    public string SupplyHint
    {
        get
        {
            if (Initial.Trim().Length == 0) return "Начальный выпуск придёт на этот счёт. У токена 18 знаков после точки, как у BIP.";
            BigInteger first, max;
            try { first = Units.ParseCoins(Initial.Trim().Replace(',', '.')); } catch (FormatException) { return "Начальный выпуск — число."; }
            if (first.Sign <= 0) return "Начальный выпуск должен быть больше нуля.";
            if (!Mintable) return $"Выпуск закрыт: всего будет ровно {Display.Amount(first)}, больше не выпустить никогда.";
            if (MaxSupply.Trim().Length == 0) max = MaxSupplyLimit;
            else try { max = Units.ParseCoins(MaxSupply.Trim().Replace(',', '.')); } catch (FormatException) { return "Предел выпуска — число."; }
            if (max > MaxSupplyLimit) return "Предел выпуска — не больше 1 000 000 000 000 000 (10¹⁵): так устроена сеть.";
            if (max < first) return "Предел выпуска меньше начального.";
            return $"Сейчас выпустится {Display.Amount(first)}; позже владелец сможет довыпустить ещё до {Display.Amount(max - first)} — всего не больше {Display.Amount(max)}.";
        }
    }

    private static string Chars(int n) => n % 10 == 1 && n % 100 != 11 ? "символ" : n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14 ? "символа" : "символов";

    // The network's price of a token by the length of its ticker: one estimate through the source, the others in proportion.
    private async Task LoadPricesAsync()
    {
        if (main.Selected == null) return;
        try
        {
            var wallet = await main.WalletAsync();
            var prices = await wallet.Backend.PricesAsync();
            ITxData Probe(int length) => new CreateTokenData("", CoinSymbol.FromString(new string('A', length)), 1, 1, false, false);
            var preview = await wallet.PreviewAsync(main.Selected.Account, Probe(7));
            if (preview.PriceInVoteCoin.IsZero) return;
            Prices.Clear();
            foreach (var (length, label) in new[] { (3, "3 символа"), (4, "4 символа"), (5, "5 символов"), (6, "6 символов"), (7, "7–10 символов") })
            {
                var price = Commission.WithGasPrice(Commission.Price(Probe(length), 0, prices), preview.GasPrice) * preview.Commission / preview.PriceInVoteCoin;
                Prices.Add($"{label} — ≈ {Display.Amount(price, 2)} {preview.CommissionCoin.Symbol}");
            }
        }
        catch (Exception e) when (e is HttpRequestException or ChainApiException or TaskCanceledException) { }
    }
    public bool Recreating => recreating;
    public string CreateTitle => recreating ? $"Пересоздать {Symbol}" : "Новый токен";
    public string CreateLabel => recreating ? "Пересоздать" : "Создать токен";
    public AsyncCommand Create { get; }
    public Command CancelRecreate { get; }

    // New pool
    public ObservableCollection<BalanceItem> Coins { get; } = [];
    public BalanceItem? CoinA { get => coinA; set { if (Set(ref coinA, value)) PoolChanged(); } }
    public BalanceItem? CoinB { get => coinB; set { if (Set(ref coinB, value)) PoolChanged(); } }
    public string AmountA { get => amountA; set { if (Set(ref amountA, value)) PoolChanged(); } }
    public string AmountB { get => amountB; set { if (Set(ref amountB, value)) PoolChanged(); } }
    public string PoolText { get => poolText; private set => Set(ref poolText, value); }
    public AsyncCommand CreatePool { get; }

    public void BalancesChanged()
    {
        var keepA = CoinA?.Coin.Id; var keepB = CoinB?.Coin.Id;
        Coins.Clear();
        foreach (var b in main.Balances) Coins.Add(b);
        CoinA = Coins.FirstOrDefault(c => c.Coin.Id == keepA) ?? Coins.FirstOrDefault(c => c.Coin.Id != 0) ?? Coins.FirstOrDefault();
        CoinB = Coins.FirstOrDefault(c => c.Coin.Id == keepB) ?? Coins.FirstOrDefault(c => c.Coin.Id == 0);
    }

    public void Reset()
    {
        Mine.Clear(); Selected = null; Changed(nameof(HasMine));
    }

    // Own tokens among the balances: a coin_info for each, kept when its owner is this account. After an operation the
    // list is read again quietly, so its result stays on the screen.
    private async Task ReadAsync(bool say)
    {
        if (main.Selected == null) return;
        var me = main.Selected.Account.Address;
        var wallet = await main.WalletAsync();
        if (say) Say("Ищу ваши токены…", Tone.Progress);
        var keep = Selected?.Info.Id;
        var found = new List<TokenItem>();
        foreach (var b in main.Balances.Where(b => b.Coin.Id != 0).ToList())   // a snapshot: the balances may be read again meanwhile
            if (await wallet.Backend.CoinAsync(b.Coin.Id) is { } info && info.Owner == me) found.Add(new TokenItem(info, b.Value));
        foreach (var t in Mine.Where(t => found.All(f => f.Info.Id != t.Info.Id)).ToList())   // found by ticker before
            if (await wallet.Backend.CoinAsync(t.Info.Id) is { } info && info.Owner == me) found.Add(new TokenItem(info, Balance(info.Id)));
        Mine.Clear();
        foreach (var t in found.OrderBy(t => t.Symbol, StringComparer.Ordinal)) Mine.Add(t);
        Selected = Mine.FirstOrDefault(t => t.Info.Id == keep) ?? Mine.FirstOrDefault();
        Changed(nameof(HasMine));
        if (say) Say(Mine.Count == 0 ? "Токенов, которыми владеет этот счёт, среди его монет нет. Свой токен с нулевым балансом можно найти по тикеру." : "", Tone.Info);
    }

    private async Task FindAsync()
    {
        var ticker = FindText.Trim().ToUpperInvariant();
        if (ticker.Length == 0 || main.Selected == null) return;
        if (await AddOwnAsync(ticker) is { } problem) { Say(problem, Tone.Warning); return; }
        FindText = ""; Say("");
    }

    // The token with this ticker into the list and selected, when this account owns it; otherwise why not.
    private async Task<string?> AddOwnAsync(string ticker)
    {
        var wallet = await main.WalletAsync();
        if (await wallet.Backend.CoinAsync(ticker) is not { } info) return $"Монеты {ticker} нет в сети.";
        if (info.Owner != main.Selected!.Account.Address) return $"{ticker} принадлежит {(info.Owner is { } o ? Core.History.Short(o) : "никому")}, а не этому счёту.";
        var known = Mine.FirstOrDefault(t => t.Info.Id == info.Id);
        if (known != null) Mine[Mine.IndexOf(known)] = new TokenItem(info, Balance(info.Id));
        else Mine.Add(new TokenItem(info, Balance(info.Id)));
        Selected = Mine.First(t => t.Info.Id == info.Id);
        Changed(nameof(HasMine));
        return null;
    }

    private BigInteger Balance(uint id) => main.Balances.FirstOrDefault(b => b.Coin.Id == id)?.Value ?? 0;

    private async Task MintAsync()
    {
        if (Selected is not { } t || !MainViewModel.TryAmount(MintAmount, this, out var value)) return;
        if (value > t.Room) { Say($"Больше предела выпуска: можно ещё до {Units.FormatCoins(t.Room)} {t.Symbol}.", Tone.Error); return; }
        var outcome = await main.ConfirmAndSendAsync("Выпуск токена", new MintTokenData(t.Info.Id, value), 0, [],
            [("Выпуск", $"{Units.FormatCoins(value)} {t.Symbol} (id {t.Info.Id})"), ("Станет", $"{Units.FormatCoins(t.Info.Volume + value)} из {Units.FormatCoins(t.Info.MaxSupply)}")], [], this);
        if (outcome?.Succeeded == true) { MintAmount = ""; await AfterAsync(); }
    }

    private async Task BurnAsync()
    {
        if (Selected is not { } t || !MainViewModel.TryAmount(BurnAmount, this, out var value)) return;
        if (value > Balance(t.Info.Id)) { Say($"Сжечь можно только своё: на счёте {Units.FormatCoins(Balance(t.Info.Id))} {t.Symbol}.", Tone.Error); return; }
        var outcome = await main.ConfirmAndSendAsync("Сжигание токена", new BurnTokenData(t.Info.Id, value), 0, [],
            [("Сжечь", $"{Units.FormatCoins(value)} {t.Symbol} (id {t.Info.Id})")], ["Сожжённые токены исчезают навсегда."], this);
        if (outcome?.Succeeded == true) { BurnAmount = ""; await AfterAsync(); }
    }

    private async Task GiveAwayAsync()
    {
        if (Selected is not { } t) return;
        if (!Minter.Protocol.Address.TryParse(NewOwner.Trim(), out var to)) { Say("Укажите адрес нового владельца: Mx и 40 шестнадцатеричных символов.", Tone.Error); return; }
        if (to == main.Selected!.Account.Address) { Say("Этот счёт уже владелец.", Tone.Warning); return; }
        var warnings = new List<string>();
        if (main.LookalikeOf(to) is { } like) warnings.Add(MainViewModel.LookalikeWarning(like));
        warnings.Add($"Владение {t.Symbol} перейдёт к этому адресу. Выпускать, пересоздавать и передавать токен сможет только он, вернуть его может только он.");
        var outcome = await main.ConfirmAndSendAsync("Передача владения", new EditCoinOwnerData(CoinSymbol.FromString(t.Symbol), to), 0, [],
            [("Токен", $"{t.Symbol} (id {t.Info.Id})"), ("Новый владелец", Display.Address(to))], warnings, this);
        if (outcome?.Succeeded == true) { NewOwner = ""; Mine.Remove(t); Selected = Mine.FirstOrDefault(); Changed(nameof(HasMine)); }
    }

    private void InputChanged()
    {
        Changed(nameof(TickerHint)); Changed(nameof(SupplyHint));
        Cost = "";
        costTimer?.Stop();
        if (CoinSymbol.IsValidTicker(Symbol)) costTimer?.Start();
    }

    // Name, ticker and supply as the node checks them; null with the reason said otherwise.
    private ITxData? Planned(bool say)
    {
        string? problem = null;
        BigInteger first = 0, max = 0;
        if (!CoinSymbol.IsValidTicker(Symbol)) problem = "Тикер — от 3 до 10 заглавных латинских букв и цифр, первая — буква.";
        else if (Encoding.UTF8.GetByteCount(Name.Trim()) > 64) problem = "Название — не больше 64 байт.";
        else
        {
            try { first = Units.ParseCoins(Initial.Trim().Replace(',', '.')); } catch (FormatException) { problem = "Начальный выпуск — число."; }
            if (problem == null && first.Sign <= 0) problem = "Начальный выпуск должен быть больше нуля.";
            if (problem == null)
            {
                if (!Mintable || MaxSupply.Trim().Length == 0) max = Mintable ? MaxSupplyLimit : first;
                else try { max = Units.ParseCoins(MaxSupply.Trim().Replace(',', '.')); } catch (FormatException) { problem = "Предел выпуска — число."; }
                if (problem == null && max < first) problem = "Предел выпуска меньше начального.";
                if (problem == null && max > MaxSupplyLimit) problem = "Предел выпуска — не больше 10¹⁵.";
            }
        }
        if (problem != null) { if (say) Say(problem, Tone.Error); return null; }
        var ticker = CoinSymbol.FromString(Symbol);
        return recreating ? new RecreateTokenData(Name.Trim(), ticker, first, max, Mintable, Burnable) : new CreateTokenData(Name.Trim(), ticker, first, max, Mintable, Burnable);
    }

    private async Task CostAsync()
    {
        if (main.Selected == null || Planned(false) is not { } data) { Cost = CoinSymbol.IsValidTicker(Symbol) ? "" : Cost; return; }
        try
        {
            var wallet = await main.WalletAsync();
            if (!recreating && await wallet.Backend.CoinAsync(Symbol) is { } taken) { Cost = $"Тикер {Symbol} уже занят (id {taken.Id})."; return; }
            var preview = await wallet.PreviewAsync(main.Selected.Account, data);
            Cost = $"Обойдётся в {Display.Amount(preview.Commission)} {preview.CommissionCoin.Symbol}: цена зависит от длины тикера, короткие дороже.";
        }
        catch (Exception e) when (e is HttpRequestException or ChainApiException or TaskCanceledException) { Cost = ""; }
    }

    private async Task CreateAsync()
    {
        if (Planned(true) is not { } data) return;
        var wallet = await main.WalletAsync();
        if (!recreating && await wallet.Backend.CoinAsync(Symbol) is { } taken) { Say($"Тикер {Symbol} уже занят (id {taken.Id}).", Tone.Error); return; }
        var (first, max, mint, burn) = data switch
        {
            CreateTokenData c => (c.InitialAmount, c.MaxSupply, c.Mintable, c.Burnable),
            RecreateTokenData r => (r.InitialAmount, r.MaxSupply, r.Mintable, r.Burnable),
            _ => throw new InvalidOperationException()
        };
        var rows = new List<(string, string)>
        {
            ("Тикер", Symbol), ("Название", Name.Trim().Length > 0 ? Name.Trim() : "—"), ("Начальный выпуск", $"{Units.FormatCoins(first)} {Symbol} — на этот счёт"),
            ("Предел выпуска", Units.FormatCoins(max)),
            ("Свойства", $"{(mint ? "можно выпускать" : "выпуск закрыт")}, {(burn ? "можно сжигать" : "сжигать нельзя")}"),
        };
        var warnings = recreating ? new List<string> { $"Нынешний {Symbol} получит новый тикер с номером версии и останется у держателей; {Symbol} станет новым токеном." } : [];
        var outcome = await main.ConfirmAndSendAsync(recreating ? "Пересоздание токена" : "Создание токена", data, 0, [], rows, warnings, this);
        if (outcome?.Succeeded != true) return;
        var created = Symbol;
        recreating = false;
        foreach (var n in new[] { nameof(Recreating), nameof(CreateLabel), nameof(CreateTitle) }) Changed(n);
        name = ""; symbol = ""; initial = ""; maxSupply = ""; Cost = "";
        foreach (var n in new[] { nameof(Name), nameof(Symbol), nameof(Initial), nameof(MaxSupply) }) Changed(n);
        await AfterAsync();
        await AddOwnAsync(created);
    }

    private void PoolChanged()
    {
        PoolText = "";
        if (CoinA == null || CoinB == null || CoinA.Coin.Id == CoinB.Coin.Id) return;
        try
        {
            var a = Units.ParseCoins(AmountA.Trim().Replace(',', '.')); var b = Units.ParseCoins(AmountB.Trim().Replace(',', '.'));
            if (a.Sign > 0 && b.Sign > 0) PoolText = $"Начальная цена: 1 {CoinA.Symbol} = {Display.Amount(b * Units.PipPerCoin / a, 6)} {CoinB.Symbol}, 1 {CoinB.Symbol} = {Display.Amount(a * Units.PipPerCoin / b, 6)} {CoinA.Symbol}.";
        }
        catch (FormatException) { }
    }

    private async Task CreatePoolAsync()
    {
        if (CoinA == null || CoinB == null) { Say("Выберите обе монеты.", Tone.Error); return; }
        if (CoinA.Coin.Id == CoinB.Coin.Id) { Say("Монеты совпадают.", Tone.Error); return; }
        if (!MainViewModel.TryAmount(AmountA, this, out var a) || !MainViewModel.TryAmount(AmountB, this, out var b)) return;
        if (a > CoinA.Value || b > CoinB.Value) { Say("На счёте меньше, чем нужно положить в пул.", Tone.Error); return; }
        if (main.Shell.Backend != null && await main.Shell.Backend.SwapPoolAsync(CoinA.Coin.Id, CoinB.Coin.Id) != null)
        { Say($"Пул {CoinA.Symbol}/{CoinB.Symbol} уже есть — добавьте ликвидность в разделе «Обмен» → «Ликвидность».", Tone.Warning); return; }
        var outcome = await main.ConfirmAndSendAsync("Создание пула", new CreateSwapPoolData(CoinA.Coin.Id, CoinB.Coin.Id, a, b), 0, [],
            [("Пул", $"{CoinA.Symbol} / {CoinB.Symbol}"), ("Кладёте", $"{Units.FormatCoins(a)} {CoinA.Symbol} + {Units.FormatCoins(b)} {CoinB.Symbol}"), ("Цена", PoolText)],
            ["Эта пара задаёт начальную цену: если она далека от рыночной, разницу заберут те, кто обменяет первым."], this);
        if (outcome?.Succeeded == true) { AmountA = ""; AmountB = ""; }
    }

    private async Task AfterAsync()
    {
        var keep = Selected?.Info.Id;
        await ReadAsync(false);
        if (keep is { } id && Mine.FirstOrDefault(t => t.Info.Id == id) is { } again) Selected = again;
    }
}
