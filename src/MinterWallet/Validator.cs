using System.Collections.ObjectModel;
using System.Numerics;
using Minter.Protocol;
using MinterWallet.Core;

namespace MinterWallet;

// «Свой валидатор»: the candidates this account owns — on and off, commission, the reward, owner and control addresses,
// a new public key, the validators' votes (halt block, network update) — and declaring a new candidate.
public sealed class ValidatorViewModel : ScreenModel
{
    private readonly MainViewModel main;
    private CandidateItem? selected;
    private string commission = "", reward = "", owner = "", control = "", newKey = "", haltHeight = "", version = "", updateHeight = "";
    private string declareKey = "", declareCommission = "10", declareStake = "", declareReward = "";
    private BalanceItem? declareCoin;

    public ValidatorViewModel(MainViewModel main)
    {
        this.main = main;
        Load = new AsyncCommand(LoadAsync, Fail);
        On = new AsyncCommand(() => SimpleAsync("Включение кандидата", c => new SetCandidateOnlineData(c.Info.PublicKey), []), Fail, () => Selected != null);
        Off = new AsyncCommand(() => SimpleAsync("Выключение кандидата", c => new SetCandidateOfflineData(c.Info.PublicKey),
            ["Выключенный кандидат не подписывает блоки и не получает наград, пока его не включат снова."]), Fail, () => Selected != null);
        SaveCommission = new AsyncCommand(SaveCommissionAsync, Fail, () => Selected != null);
        SaveAddresses = new AsyncCommand(SaveAddressesAsync, Fail, () => Selected != null);
        ChangeKey = new AsyncCommand(ChangeKeyAsync, Fail, () => Selected != null);
        Halt = new AsyncCommand(HaltAsync, Fail, () => Selected != null);
        Vote = new AsyncCommand(VoteAsync, Fail, () => Selected != null);
        Declare = new AsyncCommand(DeclareAsync, Fail);
    }

    public ObservableCollection<CandidateItem> Mine { get; } = [];
    public bool HasMine => Mine.Count > 0;
    public CandidateItem? Selected
    {
        get => selected;
        set
        {
            if (!Set(ref selected, value)) return;
            commission = value?.Info.Commission.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
            reward = value?.Info.Reward?.ToString() ?? ""; owner = value?.Info.Owner.ToString() ?? ""; control = value?.Info.Control?.ToString() ?? "";
            foreach (var n in new[] { nameof(Commission), nameof(Reward), nameof(Owner), nameof(Control), nameof(HasSelected), nameof(SelectedCaption) }) Changed(n);
            foreach (var c in new[] { On, Off, SaveCommission, SaveAddresses, ChangeKey, Halt, Vote }) c.Refresh();
        }
    }
    public bool HasSelected => Selected != null;
    public string SelectedCaption => Selected == null ? "" : $"{Selected.State} · {Selected.CommissionText} · стейк {Selected.Stake}";
    public string Commission { get => commission; set => Set(ref commission, value); }
    public string Reward { get => reward; set => Set(ref reward, value); }
    public string Owner { get => owner; set => Set(ref owner, value); }
    public string Control { get => control; set => Set(ref control, value); }
    public string NewKey { get => newKey; set => Set(ref newKey, value); }
    public string HaltHeight { get => haltHeight; set => Set(ref haltHeight, value); }
    public string Version { get => version; set => Set(ref version, value); }
    public string UpdateHeight { get => updateHeight; set => Set(ref updateHeight, value); }
    public AsyncCommand Load { get; }
    public AsyncCommand On { get; }
    public AsyncCommand Off { get; }
    public AsyncCommand SaveCommission { get; }
    public AsyncCommand SaveAddresses { get; }
    public AsyncCommand ChangeKey { get; }
    public AsyncCommand Halt { get; }
    public AsyncCommand Vote { get; }

    public ObservableCollection<BalanceItem> Coins { get; } = [];
    public string DeclareKey { get => declareKey; set => Set(ref declareKey, value); }
    public string DeclareCommission { get => declareCommission; set => Set(ref declareCommission, value); }
    public BalanceItem? DeclareCoin { get => declareCoin; set => Set(ref declareCoin, value); }
    public string DeclareStake { get => declareStake; set => Set(ref declareStake, value); }
    public string DeclareReward { get => declareReward; set => Set(ref declareReward, value); }
    public AsyncCommand Declare { get; }

    public void BalancesChanged()
    {
        var keep = DeclareCoin?.Coin.Id;
        Coins.Clear();
        foreach (var b in main.Balances) Coins.Add(b);
        DeclareCoin = Coins.FirstOrDefault(c => c.Coin.Id == keep) ?? Coins.FirstOrDefault(c => c.Coin.Id == 0) ?? Coins.FirstOrDefault();
    }

    public void Reset() { Mine.Clear(); Selected = null; Changed(nameof(HasMine)); DeclareReward = ""; }

    private async Task LoadAsync()
    {
        if (main.Selected == null) return;
        var me = main.Selected.Account.Address;
        var wallet = await main.WalletAsync();
        var keep = Selected?.Key;
        var list = await wallet.Backend.CandidatesAsync();
        Mine.Clear();
        foreach (var c in list.Where(c => c.Owner == me || c.Control == me)) Mine.Add(new CandidateItem(c, main.Symbol(0)));
        Selected = Mine.FirstOrDefault(c => c.Key == keep) ?? Mine.FirstOrDefault();
        Changed(nameof(HasMine));
        if (DeclareReward.Length == 0) DeclareReward = me.ToString();
        Say(Mine.Count == 0 ? "Этот счёт не владеет ни одним кандидатом. Выдвинуть нового можно ниже." : "", Tone.Info);
    }

    private async Task SimpleAsync(string title, Func<CandidateItem, ITxData> make, List<string> warnings)
    {
        if (Selected is not { } c) return;
        var outcome = await main.ConfirmAndSendAsync(title, make(c), 0, [], [("Кандидат", c.Key)], warnings, this);
        if (outcome?.Succeeded == true) await AfterAsync();
    }

    private async Task SaveCommissionAsync()
    {
        if (Selected is not { } c) return;
        if (!uint.TryParse(Commission.Trim(), out var value) || value > 100) { Say("Комиссия — целое число от 0 до 100.", Tone.Error); return; }
        var outcome = await main.ConfirmAndSendAsync("Комиссия кандидата", new EditCandidateCommissionData(c.Info.PublicKey, value), 0, [],
            [("Кандидат", c.Key), ("Комиссия", $"{c.Info.Commission} % → {value} %")], ["Сеть позволяет менять комиссию не чаще, чем раз в установленный период."], this);
        if (outcome?.Succeeded == true) await AfterAsync();
    }

    private async Task SaveAddressesAsync()
    {
        if (Selected is not { } c) return;
        if (!Minter.Protocol.Address.TryParse(Reward.Trim(), out var r) || !Minter.Protocol.Address.TryParse(Owner.Trim(), out var o) || !Minter.Protocol.Address.TryParse(Control.Trim(), out var k))
        { Say("Все три адреса — Mx и 40 шестнадцатеричных символов.", Tone.Error); return; }
        var warnings = new List<string>();
        if (o != main.Selected!.Account.Address) warnings.Add("Владелец меняется: после этого управлять кандидатом сможет только новый владелец.");
        foreach (var a in new[] { r, o, k }.Distinct()) if (main.LookalikeOf(a) is { } like) warnings.Add(MainViewModel.LookalikeWarning(like));
        var outcome = await main.ConfirmAndSendAsync("Адреса кандидата", new EditCandidateData(c.Info.PublicKey, r, o, k), 0, [],
            [("Кандидат", c.Key), ("Награды", Display.Address(r)), ("Владелец", Display.Address(o)), ("Управление", Display.Address(k))], warnings, this);
        if (outcome?.Succeeded == true) await AfterAsync();
    }

    private async Task ChangeKeyAsync()
    {
        if (Selected is not { } c) return;
        if (!TryKey(NewKey, out var key)) return;
        var outcome = await main.ConfirmAndSendAsync("Новый ключ кандидата", new EditCandidatePublicKeyData(c.Info.PublicKey, key), 0, [],
            [("Было", c.Key), ("Станет", key.ToString())],
            ["Нода валидатора должна уже работать с новым ключом, иначе он перестанет подписывать блоки и будет оштрафован."], this);
        if (outcome?.Succeeded == true) { NewKey = ""; await AfterAsync(); }
    }

    private async Task HaltAsync()
    {
        if (Selected is not { } c) return;
        if (!ulong.TryParse(HaltHeight.Trim(), out var height)) { Say("Высота — целое число.", Tone.Error); return; }
        var outcome = await main.ConfirmAndSendAsync("Голос за остановку сети", new SetHaltBlockData(c.Info.PublicKey, height), 0, [],
            [("Кандидат", c.Key), ("Остановить на блоке", Display.Integer((long)height))],
            ["Это голос валидатора: если за остановку на этом блоке проголосуют валидаторы с 2/3 силы, сеть остановится на нём."], this);
        if (outcome?.Succeeded == true) HaltHeight = "";
    }

    private async Task VoteAsync()
    {
        if (Selected is not { } c) return;
        if (Version.Trim().Length == 0 || !ulong.TryParse(UpdateHeight.Trim(), out var height)) { Say("Укажите версию и высоту — целое число.", Tone.Error); return; }
        var outcome = await main.ConfirmAndSendAsync("Голос за обновление сети", new VoteUpdateData(Version.Trim(), c.Info.PublicKey, height), 0, [],
            [("Кандидат", c.Key), ("Версия", Version.Trim()), ("С блока", Display.Integer((long)height))],
            ["Это голос валидатора за переход сети на новую версию с указанного блока."], this);
        if (outcome?.Succeeded == true) { Version = ""; UpdateHeight = ""; }
    }

    private async Task DeclareAsync()
    {
        if (!TryKey(DeclareKey, out var key)) return;
        if (!uint.TryParse(DeclareCommission.Trim(), out var fee) || fee > 100) { Say("Комиссия — целое число от 0 до 100.", Tone.Error); return; }
        if (!Minter.Protocol.Address.TryParse(DeclareReward.Trim(), out var reward)) { Say("Адрес для наград — Mx и 40 шестнадцатеричных символов.", Tone.Error); return; }
        if (DeclareCoin == null) { Say("Выберите монету стейка.", Tone.Error); return; }
        if (!MainViewModel.TryAmount(DeclareStake, this, out BigInteger stake)) return;
        var outcome = await main.ConfirmAndSendAsync("Выдвижение кандидата", new DeclareCandidacyData(reward, key, fee, DeclareCoin.Coin.Id, stake), 0, [],
            [("Ключ ноды", key.ToString()), ("Комиссия", $"{fee} %"), ("Награды", Display.Address(reward)), ("Стейк", $"{Units.FormatCoins(stake)} {DeclareCoin.Coin.Symbol} (id {DeclareCoin.Coin.Id})"),
             ("Владелец", Display.Address(main.Selected!.Account.Address))],
            ["Кандидат начинает выключенным. Включайте его, когда нода с этим ключом уже работает: иначе пропущенные блоки обернутся штрафом."], this);
        if (outcome?.Succeeded == true) { DeclareKey = ""; DeclareStake = ""; await AfterAsync(); }
    }

    private bool TryKey(string text, out Pubkey key)
    {
        key = default;
        try { key = Pubkey.Parse(text.Trim()); return true; }
        catch (FormatException) { Say("Ключ ноды — Mp и 64 шестнадцатеричных символа.", Tone.Error); return false; }
    }

    private async Task AfterAsync()
    {
        var status = Status; var tone = Tone;
        await LoadAsync();
        Say(status, tone);
    }
}
