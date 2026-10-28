using System.Collections.ObjectModel;
using System.Numerics;
using Minter.Protocol;
using MinterWallet.Api;
using MinterWallet.Core;

namespace MinterWallet;

// A token at the account's address in Ethereum or BSC that the bridge takes: its balance there; for WETH/WBNB also the
// native coin, which the wallet wraps first.
public sealed record DepositToken(BridgeToken Token, BigInteger Balance, BigInteger Native, string MinterSymbol)
{
    public bool Wraps => Bridge.IsWrappedNative(Token);
    public string Symbol => Wraps ? Bridge.NativeOf(Token.Network) : Token.Denom.ToUpperInvariant();
    public BigInteger Total => Wraps ? Balance + Native : Balance;
    public string AmountShort => Display.Amount(EvmUnits.ToMinter(Total, Token.Decimals));
    public override string ToString() => $"{Symbol} — {EvmUnits.Format(Total, Token.Decimals)}";
}

// «Ввести в Minter»: tokens from Ethereum or BSC to a Minter address through Minter Hub, sent from the account's own
// address there (the same key). Up to three transactions in that network, each paying its gas in ETH or BNB: wrapping the
// native coin when it is what the user has (WETH, WBNB are what the bridge takes), approving exactly the amount to the
// bridge's contract, and transferToChain. Then the bridge's multisend on Minter is awaited; it keeps 1 %. Mainnet only.
public sealed class BridgeDepositViewModel : ScreenModel
{
    private const ulong WrapGas = 60_000, ApproveGas = 80_000, TransferGas = 150_000;
    private readonly MainViewModel main;
    private int networkIndex = 1;
    private DepositToken? token;
    private string amount = "", recipient = "", estimate = "", tracking = "", balances = "";
    private BigInteger native, gasPrice;
    private string? link;
    private int refreshing;

    public BridgeDepositViewModel(MainViewModel main)
    {
        this.main = main;
        SendNow = new AsyncCommand(SendAsync, Fail, () => IsAvailable && Token != null);
        Refresh = new AsyncCommand(RefreshAsync, Fail);
        Own = new Command(() => { if (main.Selected != null) Recipient = main.Selected.Account.Address.ToString(); });
        All = new Command(() => { if (Token is { } t) Amount = EvmUnits.Format(Spendable(t), t.Token.Decimals); });
        CopyAddress = new AsyncCommand(async () => { if (main.Shell.CopyText != null && EvmAddress.Length > 0) { await main.Shell.CopyText(EvmAddress); Say("Адрес скопирован.", Tone.Success); } }, Fail);
        OpenLink = new AsyncCommand(async () => { if (link != null && main.Shell.OpenUrl != null) await main.Shell.OpenUrl(new Uri(link)); }, Fail);
    }

    public bool IsAvailable => main.Shell.Status?.Network.StartsWith("minter-mainnet", StringComparison.Ordinal) == true;
    public bool IsUnavailable => !IsAvailable;
    public int NetworkIndex { get => networkIndex; set { if (value >= 0 && Set(ref networkIndex, value)) { Changed(nameof(NetworkName)); Changed(nameof(EvmAddressText)); _ = RefreshAsync(); } } }
    public BridgeNetwork Network => (BridgeNetwork)networkIndex;
    public string NetworkName => Bridge.NameOf(Network);
    public string EvmAddress => main.Selected == null ? "" : Bridge.OwnAddress(main.Selected.Account.Address);
    public string EvmAddressText => main.Selected == null ? "" : $"Ваш адрес в {NetworkName} — тот же ключ, что у счёта «{main.Selected.Name}»:";
    public string Balances { get => balances; private set => Set(ref balances, value); }
    public ObservableCollection<DepositToken> Tokens { get; } = [];
    public DepositToken? Token { get => token; set { if (Set(ref token, value)) { Changed(nameof(Available)); UpdateEstimate(); SendNow.Refresh(); } } }
    public string Amount { get => amount; set { if (Set(ref amount, value)) UpdateEstimate(); } }
    public string Available => Token is not { } t ? ""
        : t.Wraps && t.Balance.Sign > 0 && t.Native.Sign > 0 ? $"Доступно: {EvmUnits.Format(t.Total, 18)} {t.Symbol} ({EvmUnits.Format(t.Balance, 18)} W{t.Symbol} и {EvmUnits.Format(t.Native, 18)} {t.Symbol})"
        : $"Доступно: {EvmUnits.Format(t.Total, t.Token.Decimals)} {t.Symbol}";
    public string Recipient { get => recipient; set { if (Set(ref recipient, value.Trim())) { Changed(nameof(RecipientCheck)); Changed(nameof(RecipientTone)); } } }
    public string RecipientCheck => Recipient.Length == 0 ? "" : !Address.TryParse(Recipient, out var a) ? "Адрес в Minter — Mx и 40 шестнадцатеричных символов."
        : main.Accounts.FirstOrDefault(x => x.Account.Address == a) is { } own ? $"Ваш счёт «{own.Name}»."
        : "Чужой адрес Minter: монеты придут туда, сверьте его целиком.";
    public Tone RecipientTone => Recipient.Length == 0 ? Tone.None : !Address.TryParse(Recipient, out var a) ? Tone.Error
        : main.Accounts.Any(x => x.Account.Address == a) ? Tone.Success : Tone.Warning;
    public string Estimate { get => estimate; private set => Set(ref estimate, value); }
    public string Tracking { get => tracking; private set { Set(ref tracking, value); Changed(nameof(HasTracking)); } }
    public bool HasTracking => Tracking.Length > 0;
    public bool HasLink => link != null;
    public AsyncCommand SendNow { get; }
    public AsyncCommand Refresh { get; }
    public Command Own { get; }
    public Command All { get; }
    public AsyncCommand CopyAddress { get; }
    public AsyncCommand OpenLink { get; }

    // The selected account changed (or the page opened): its address there, its balances, itself as the recipient.
    public void AccountChanged()
    {
        Changed(nameof(EvmAddress)); Changed(nameof(EvmAddressText)); Changed(nameof(IsAvailable)); Changed(nameof(IsUnavailable));
        if (main.Selected != null && (Recipient.Length == 0 || main.Accounts.Any(a => a.Account.Address.ToString() == Recipient))) Recipient = main.Selected.Account.Address.ToString();
        Tokens.Clear(); Token = null; Balances = "";
        if (IsAvailable) _ = RefreshAsync();
    }

    // Balances of the bridge's tokens at the account's address there (a few requests at a time: public nodes limit the rate).
    private async Task RefreshAsync()
    {
        if (!IsAvailable || main.Selected is not { } account) return;
        var run = Interlocked.Increment(ref refreshing);
        var network = Network;
        var owner = account.Account.Address;
        Balances = $"Читаем балансы в {NetworkName}…";
        await main.Shell.LoadBridgeTokensAsync();
        var evm = main.Shell.EvmOf(network);
        var address = Bridge.OwnAddress(owner);
        var nativeBalance = await evm.BalanceAsync(address);
        var price = await evm.GasPriceAsync();
        using var gate = new SemaphoreSlim(4);
        var found = await Task.WhenAll(Bridge.TokensOf(network).Select(async t =>
        {
            await gate.WaitAsync();
            try { return (t, Balance: Abi.ReadUint(await evm.CallAsync(t.Contract, Bridge.BalanceOfCall(owner)))); }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or EvmRpcException or FormatException) { return (t, Balance: BigInteger.Zero); }
            finally { gate.Release(); }
        }));
        if (run != refreshing || network != Network) return;
        native = nativeBalance; gasPrice = price;
        var keep = Token?.Token;
        Tokens.Clear();
        foreach (var (t, balance) in found)
            if (balance.Sign > 0 || Bridge.IsWrappedNative(t) && nativeBalance.Sign > 0) Tokens.Add(new DepositToken(t, balance, Bridge.IsWrappedNative(t) ? nativeBalance : 0, main.Symbol(t.MinterId)));
        Token = Tokens.FirstOrDefault(x => x.Token == keep) ?? Tokens.FirstOrDefault();
        var nativeName = Bridge.NativeOf(network);
        Balances = (nativeBalance.IsZero ? $"На этом адресе нет {nativeName}, а газ в {NetworkName} платится им: пополните адрес с биржи или из другого кошелька."
                : $"{nativeName} на газ: {EvmUnits.Format(nativeBalance, 18)}. Цена газа {Gwei(price)} gwei.")
            + (Tokens.Count == 0 ? $"\nМонет, которые принимает мост, на этом адресе в {NetworkName} нет." : "");
    }

    private BigInteger Spendable(DepositToken t) => t.Wraps ? t.Balance + BigInteger.Max(0, t.Native - (WrapGas + ApproveGas + TransferGas) * gasPrice * 2) : t.Balance;

    private void UpdateEstimate()
    {
        if (Token is not { } t || Amount.Trim().Length == 0) { Estimate = ""; return; }
        BigInteger value;
        try { value = EvmUnits.Parse(Amount, t.Token.Decimals); } catch (FormatException e) { Estimate = e.Message; return; }
        if (value.Sign <= 0) { Estimate = ""; return; }
        var credited = EvmUnits.ToMinter(value, t.Token.Decimals);
        var steps = (t.Wraps && value > t.Balance ? 1 : 0) + 2;
        Estimate = $"Мост удержит 1 % — придёт ≈ {Display.Amount(credited - Bridge.HubFee(credited))} {t.MinterSymbol}."
            + (gasPrice.IsZero ? "" : $"\nГаз в {NetworkName}: до ≈ {EvmUnits.Format(GasLimit(steps) * gasPrice, 18)} {Bridge.NativeOf(t.Token.Network)} за {steps} транзакции.");
    }

    private static ulong GasLimit(int steps) => steps == 3 ? WrapGas + ApproveGas + TransferGas : ApproveGas + TransferGas;
    private static string Gwei(BigInteger wei) => Display.Amount(wei * BigInteger.Pow(10, 9));

    private async Task SendAsync()
    {
        if (!IsAvailable) { Say("Мост Minter Hub работает только в основной сети Minter.", Tone.Error); return; }
        if (main.Selected?.Account is not { } account) { Say("Выберите счёт.", Tone.Error); return; }
        if (account.WatchOnly) { Say("Этот счёт только для просмотра: подписать нечем.", Tone.Error); return; }
        if (Token is not { } t) { Say("Выберите монету.", Tone.Error); return; }
        if (!Address.TryParse(Recipient, out var to)) { Say("Адрес в Minter — Mx и 40 шестнадцатеричных символов.", Tone.Error); return; }
        BigInteger value;
        try { value = EvmUnits.Parse(Amount, t.Token.Decimals); } catch (FormatException e) { Say(e.Message, Tone.Error); return; }
        if (value.Sign <= 0) { Say("Укажите сумму.", Tone.Error); return; }
        var network = t.Token.Network;
        var net = Bridge.NameOf(network);
        var nativeName = Bridge.NativeOf(network);
        var evm = main.Shell.EvmOf(network);
        var from = Bridge.OwnAddress(account.Address);
        var bridge = Bridge.ContractOf(network);
        var chainId = Bridge.ChainIdOf(network);

        Say($"Проверка балансов в {net}…", Tone.Progress);
        if (await evm.ChainIdAsync() != chainId) { Say($"Узел сети {net} отвечает за другую сеть — ничего не отправлено.", Tone.Error); return; }
        var balance = Abi.ReadUint(await evm.CallAsync(t.Token.Contract, Bridge.BalanceOfCall(account.Address)));
        var nativeNow = await evm.BalanceAsync(from);
        var allowance = Abi.ReadUint(await evm.CallAsync(t.Token.Contract, Bridge.AllowanceCall(account.Address, bridge)));
        var price = await evm.GasPriceAsync() * 6 / 5;
        var wrap = t.Wraps && value > balance ? value - balance : BigInteger.Zero;
        if (!t.Wraps && value > balance) { Say($"На адресе {EvmUnits.Format(balance, t.Token.Decimals)} {t.Symbol} — меньше суммы.", Tone.Error); return; }

        // The steps with their gas limits; transferToChain is estimated again once the approval is in a block.
        var steps = new List<(string Name, string To, BigInteger Value, byte[] Data, ulong Gas)>();
        if (wrap.Sign > 0) steps.Add(($"обернуть {EvmUnits.Format(wrap, 18)} {nativeName} в W{nativeName}", t.Token.Contract, wrap, Bridge.WrapCall(), await GasOf(evm, from, t.Token.Contract, wrap, Bridge.WrapCall(), WrapGas)));
        if (allowance < value)
        {
            if (allowance.Sign > 0) steps.Add(("сбросить прежнее разрешение (USDT иначе не даст его изменить)", t.Token.Contract, 0, Bridge.ApproveCall(bridge, 0), ApproveGas));
            steps.Add(($"разрешить контракту моста списать ровно {EvmUnits.Format(value, t.Token.Decimals)} {t.Token.Denom.ToUpperInvariant()}", t.Token.Contract, 0, Bridge.ApproveCall(bridge, value),
                await GasOf(evm, from, t.Token.Contract, 0, Bridge.ApproveCall(bridge, value), ApproveGas)));
        }
        var deposit = Bridge.DepositCall(t.Token, to, value);
        steps.Add(($"передать мосту для {History.Short(to)} в Minter", bridge, 0, deposit, steps.Count == 0 ? await GasOf(evm, from, bridge, 0, deposit, TransferGas) : TransferGas));
        var gasCost = steps.Aggregate(BigInteger.Zero, (s, x) => s + x.Gas * price);
        if (nativeNow < wrap + gasCost)
        {
            Say(wrap.Sign > 0 ? $"Не хватает {nativeName}: на обёртку и газ нужно до {EvmUnits.Format(wrap + gasCost, 18)}, на адресе {EvmUnits.Format(nativeNow, 18)}."
                : $"Не хватает {nativeName} на газ: нужно до {EvmUnits.Format(gasCost, 18)}, на адресе {EvmUnits.Format(nativeNow, 18)}.", Tone.Error);
            return;
        }

        var credited = EvmUnits.ToMinter(value, t.Token.Decimals);
        var ours = main.Accounts.FirstOrDefault(a => a.Account.Address == to);
        var rows = new List<(string, string)>
        {
            ("Откуда", $"{net}, адрес счёта «{account.Name}»\n{from}"),
            ("Куда", $"Minter — через мост Minter Hub\n{(ours != null ? $"счёт «{ours.Name}»: {Display.Address(to)}" : Display.Address(to))}"),
            ("Монета", $"{EvmUnits.Format(value, t.Token.Decimals)} {t.Symbol} → {t.MinterSymbol} (id {t.Token.MinterId})"),
            ("Мост удержит", $"1 % — придёт ≈ {Units.FormatCoins(credited - Bridge.HubFee(credited))} {t.MinterSymbol}"),
            ("Транзакции", string.Join("\n", steps.Select((s, i) => $"{i + 1}. {s.Name}"))),
            ("Газ", $"до {EvmUnits.Format(gasCost, 18)} {nativeName} (gas price {Gwei(price)} gwei)"),
            ("Контракт моста", bridge),
        };
        var warnings = new List<string>
        {
            $"Ввод не отменить: монеты уйдут в мост и придут на {Display.Address(to)} в Minter.",
            "Minter Hub — мост команды Minter, не этого кошелька: кошелёк только подписывает его вызовы своим ключом.",
        };
        var password = main.Shell.Confirm == null ? null : await main.Shell.Confirm(new ConfirmRequest($"Ввод из {net}", rows, warnings));
        if (password == null) { Say("Отменено — ничего не отправлено.", Tone.Info); return; }
        Say("Проверка пароля…", Tone.Progress);
        if (!await main.Shell.CheckPasswordAsync(password)) { Say("Неверный пароль — ничего не отправлено.", Tone.Error); return; }
        var wallet = await main.WalletAsync();

        var started = DateTimeOffset.UtcNow;
        var nonce = await evm.NonceAsync(from);
        string hash = "";
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var gas = step.Gas;
            if (i == steps.Count - 1 && i > 0) gas = await GasOf(evm, from, step.To, step.Value, step.Data, TransferGas);
            var raw = wallet.SignEvm(account, new EvmTx(nonce++, price, gas, Convert.FromHexString(step.To[2..]), step.Value, step.Data), chainId);
            hash = await evm.SendRawAsync(raw);
            link = Bridge.TxExplorerOf(network, hash); Changed(nameof(HasLink));
            Say($"Шаг {i + 1} из {steps.Count}: {step.Name} — {hash}, ждём блок…", Tone.Progress);
            var receipt = await ReceiptAsync(evm, hash);
            if (receipt == null) { Say($"Транзакция {hash} за 10 минут не попала в блок {net}. Дальнейшие шаги не отправлены; проверьте её в обозревателе.", Tone.Warning); return; }
            if (!receipt.Succeeded) { Say($"Транзакция {hash} в блоке {Display.Integer(receipt.Block)} {net} не выполнена. Дальнейшие шаги не отправлены.", Tone.Error); return; }
        }
        Amount = "";
        Say($"Готово — монеты переданы мосту в {net}: {hash}.", Tone.Success);
        Tracking = $"Мост зачислит ≈ {Display.Amount(credited - Bridge.HubFee(credited))} {t.MinterSymbol} на {Display.Address(to)} — обычно за несколько минут…";
        _ = TrackAsync(to, t.Token.MinterId, t.MinterSymbol, started.AddMinutes(-1));
        _ = RefreshAsync();
    }

    private static async Task<ulong> GasOf(EvmClient evm, string from, string to, BigInteger value, byte[] data, ulong fallback)
    {
        try { return await evm.EstimateGasAsync(from, to, value, data) * 5 / 4; }
        catch (EvmRpcException) { return fallback; }
    }

    private static async Task<EvmReceipt?> ReceiptAsync(EvmClient evm, string hash)
    {
        var until = DateTime.UtcNow.AddMinutes(10);
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            try { if (await evm.ReceiptAsync(hash) is { } r) return r; }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or EvmRpcException) { }
        }
        return null;
    }

    // The bridge's multisend to the recipient on Minter, looked for every 20 s for up to 30 minutes.
    private async Task TrackAsync(Address to, uint coin, string symbol, DateTimeOffset since)
    {
        var until = DateTime.UtcNow.AddMinutes(30);
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(TimeSpan.FromSeconds(20));
            try
            {
                if (Bridge.ArrivalOf(to, coin, since, await main.Shell.Explorer.BridgeActivityAsync()) is { } arrived)
                {
                    Tracking = $"Мост зачислил {Units.FormatCoins(arrived.Value)} {symbol} на {Display.Address(to)}.";
                    main.Refresh.Execute(null);
                    return;
                }
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { }
        }
        Tracking = "За 30 минут мост зачисление не отметил — так бывает при загрузке сетей. Баланс в Minter обновится, когда монеты придут.";
    }
}
