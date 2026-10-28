using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Threading;
using Minter.Protocol;
using MinterWallet.Core;

namespace MinterWallet;

// An address met on the way that has something popular about it, kept so it can be added too.
public sealed class NotableItem
{
    public NotableItem(VanityNotable notable, Action<NotableItem> add)
    {
        Notable = notable;
        Add = new Command(() => add(this));
    }

    public VanityNotable Notable { get; }
    public string What => Notable.What;
    public string Address => Notable.Result.Address.ToString();
    public string RarityText => $"такое попадается 1 раз на {VanityViewModel.Big(Notable.Rarity)}";
    public Command Add { get; }
}

// «Красивый адрес»: a template for the address after Mx (a beginning, an end, both, text anywhere, a mask with ? and *,
// a repeated digit, equal or mirrored ends), a key (fast) or a recovery phrase (slow, restores like any phrase), the odds
// and the expected time on this computer, the search, and the addresses with popular features met on the way.
public sealed class VanityViewModel : ScreenModel
{
    private const int NotablesKept = 10;
    private readonly MainViewModel main;
    private readonly Dictionary<VanitySecret, double> speed = [];
    private int templateIndex, kindIndex, repeatPlaceIndex;
    private decimal? count = 6;
    private string start = "", end = "", anywhere = "", mask = "", name = "", progressText = "", found = "", odds = "";
    private bool running;
    private CancellationTokenSource? stop;
    private VanityResult? result;

    public VanityViewModel(MainViewModel main)
    {
        this.main = main;
        Start = new AsyncCommand(SearchAsync, Fail, () => !Running);
        Stop = new Command(() => stop?.Cancel(), () => Running);
        Add = new Command(() => { if (result is { } r && AddResult(r)) Clear(); }, () => result != null);
    }

    // 0 beginning, 1 end, 2 both, 3 anywhere, 4 mask, 5 repeated digit, 6 equal ends, 7 mirrored ends.
    public int TemplateIndex
    {
        get => templateIndex;
        set
        {
            if (value < 0 || !Set(ref templateIndex, value)) return;
            foreach (var n in new[] { nameof(UsesStart), nameof(UsesEnd), nameof(UsesAnywhere), nameof(UsesMask), nameof(UsesRepeat), nameof(UsesCount), nameof(CountLabel) }) Changed(n);
            TemplateChanged();
        }
    }
    public bool UsesStart => templateIndex is 0 or 2;
    public bool UsesEnd => templateIndex is 1 or 2;
    public bool UsesAnywhere => templateIndex == 3;
    public bool UsesMask => templateIndex == 4;
    public bool UsesRepeat => templateIndex == 5;
    public bool UsesCount => templateIndex is 5 or 6 or 7;
    public string CountLabel => templateIndex == 5 ? "Сколько одинаковых подряд" : "Сколько символов с каждого края";
    public string StartText { get => start; set { if (Set(ref start, value.Trim().ToLowerInvariant())) TemplateChanged(); } }
    public string EndText { get => end; set { if (Set(ref end, value.Trim().ToLowerInvariant())) TemplateChanged(); } }
    public string AnywhereText { get => anywhere; set { if (Set(ref anywhere, value.Trim().ToLowerInvariant())) TemplateChanged(); } }
    public string MaskText { get => mask; set { if (Set(ref mask, value.Trim().ToLowerInvariant())) TemplateChanged(); } }
    public int RepeatPlaceIndex { get => repeatPlaceIndex; set { if (value >= 0 && Set(ref repeatPlaceIndex, value)) TemplateChanged(); } }
    public decimal? Count { get => count; set { if (Set(ref count, value)) TemplateChanged(); } }
    public int KindIndex { get => kindIndex; set { if (value >= 0 && Set(ref kindIndex, value)) _ = OddsAsync(); } }
    public VanitySecret Kind => (VanitySecret)kindIndex;
    public string Name { get => name; set => Set(ref name, value); }
    public string Preview => templateIndex switch
    {
        0 or 1 or 2 => $"Mx{(UsesStart ? start : "")}…{(UsesEnd ? end : "")}",
        3 => $"Mx…{anywhere}…",
        4 => $"Mx{mask}",
        5 => repeatPlaceIndex switch { 0 => $"Mx{new string('7', N)}…", 1 => $"Mx…{new string('7', N)}", _ => $"Mx…{new string('7', N)}…" },
        6 => $"Mx{"1234567890abcdef1234567890"[..N]}…{"1234567890abcdef1234567890"[..N]}",
        _ => $"Mx{"1234567890abcdef1234567890"[..N]}…{new string("1234567890abcdef1234567890"[..N].Reverse().ToArray())}",
    };
    public string Problem
    {
        get
        {
            if (templateIndex == 4) return mask.Length == 0 ? "" : VanityMask.Problem(mask) ?? "";
            var text = (UsesStart ? start : "") + (UsesEnd ? end : "") + (UsesAnywhere ? anywhere : "");
            if (Vanity.Problem(text) is { } p) return p;
            return text.Length > 40 ? "В адресе 40 символов — больше не уместить." : "";
        }
    }
    public string Odds { get => odds; private set => Set(ref odds, value); }
    public bool Running { get => running; private set { if (!Set(ref running, value)) return; Start.Refresh(); Stop.Refresh(); } }
    public string ProgressText { get => progressText; private set => Set(ref progressText, value); }
    public string Found { get => found; private set { Set(ref found, value); Changed(nameof(HasFound)); } }
    public bool HasFound => Found.Length > 0;
    public ObservableCollection<NotableItem> Notables { get; } = [];
    public bool HasNotables => Notables.Count > 0;
    public AsyncCommand Start { get; }
    public Command Stop { get; }
    public Command Add { get; }

    private int N => (int)Math.Clamp(count ?? 6, 2, 20);

    private IVanityTemplate? Template => templateIndex switch
    {
        4 => mask.Length == 0 || VanityMask.Problem(mask) != null ? null : new VanityMask(mask),
        5 => new VanityRepeat((VanityPlace)repeatPlaceIndex, N),
        6 => new VanityEdges(N, false),
        7 => new VanityEdges(N, true),
        _ => new VanityPattern(UsesStart ? start : "", UsesEnd ? end : "", UsesAnywhere ? anywhere : "") is { Length: > 0 } p && Problem.Length == 0 ? p : null,
    };

    private void TemplateChanged()
    {
        foreach (var n in new[] { nameof(Preview), nameof(Problem) }) Changed(n);
        _ = OddsAsync();
    }

    // The odds of the template and the time it takes on average here, at the speed measured for this kind of secret.
    private async Task OddsAsync()
    {
        if (Template is not { } template) { Odds = ""; return; }
        var expected = template.ExpectedTries;
        if (!speed.TryGetValue(Kind, out var rate))
        {
            Odds = $"Шанс — 1 к {Big(expected)} на попытку; измеряю скорость этого компьютера…";
            rate = speed[Kind] = await MeasureAsync(Kind);
        }
        if (Template is not { } still || still.ExpectedTries != expected) return;   // changed meanwhile
        var mean = expected / Math.Max(rate, 1);
        // Lines, each translated on its own.
        Odds = $"Шанс — 1 к {Big(expected)} на попытку; в среднем — {Duration(mean)} на этом компьютере ({Display.Integer((long)rate)} в секунду)."
            + $"\nВероятность найти за минуту — {Percent(Vanity.Chance(expected, rate * 60))}, за час — {Percent(Vanity.Chance(expected, rate * 3600))}, за сутки — {Percent(Vanity.Chance(expected, rate * 86400))}."
            + (mean > 365.25 * 86400 ? "\nНа этом компьютере такой адрес практически недостижим: уберите символ-другой или выберите шаблон попроще." : "");
    }

    private static async Task<double> MeasureAsync(VanitySecret kind)
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(kind == VanitySecret.PrivateKey ? 400 : 1200));
        long tries = 0;
        var clock = Stopwatch.StartNew();
        await Vanity.FindAsync(new VanityPattern(new string('0', 40)), kind, n => Interlocked.Exchange(ref tries, n), cancel.Token);
        return tries / Math.Max(clock.Elapsed.TotalSeconds, 0.001);
    }

    // 1 234 567 up to a quadrillion, then 1.2·10²⁴.
    public static string Big(double value)
    {
        if (value < 1e15) return Display.Integer((long)Math.Round(value));
        var exponent = (int)Math.Floor(Math.Log10(value));
        var mantissa = value / Math.Pow(10, exponent);
        var sup = string.Concat(exponent.ToString(CultureInfo.InvariantCulture).Select(c => "⁰¹²³⁴⁵⁶⁷⁸⁹"[c - '0']));
        return $"{mantissa.ToString("0.#", CultureInfo.InvariantCulture)}·10{sup}";
    }

    private static string Percent(double p) => p >= 0.9995 ? "почти 100 %" : p < 0.0001 ? "меньше 0.01 %" : $"{(p * 100).ToString(p < 0.01 ? "0.##" : "0.#", CultureInfo.InvariantCulture)} %";

    private static string Duration(double seconds) => seconds switch
    {
        < 2 => "секунды",
        < 90 => $"{Math.Round(seconds)} с",
        < 5400 => $"{Math.Round(seconds / 60)} мин",
        < 172800 => $"{Math.Round(seconds / 3600, 1).ToString(CultureInfo.InvariantCulture)} ч",
        < 2 * 365.25 * 86400 => $"{Math.Round(seconds / 86400)} дн.",
        _ => $"{Big(seconds / (365.25 * 86400))} лет",
    };

    private async Task SearchAsync()
    {
        if (Template is not { } template) { Say(Problem.Length > 0 ? Problem : "Укажите, что должно быть в адресе.", Tone.Error); return; }
        Clear();
        Running = true;
        stop = new CancellationTokenSource();
        var clock = Stopwatch.StartNew();
        var kind = Kind;
        Say("Ищу… Можно продолжать пользоваться кошельком.", Tone.Progress);
        try
        {
            // Keys are fast, so only rarer finds are worth showing; phrases are slow, so smaller ones are.
            result = await Vanity.FindAsync(template, kind, n => Dispatcher.UIThread.Post(() =>
                ProgressText = $"Перебрано {Display.Integer(n)} · {Display.Integer((long)(n / Math.Max(clock.Elapsed.TotalSeconds, 0.001)))} в секунду · {clock.Elapsed:hh\\:mm\\:ss}"),
                stop.Token, n => Dispatcher.UIThread.Post(() => Keep(n)), kind == VanitySecret.PrivateKey ? 1048576 : 4096);
            if (result == null) { Say(HasNotables ? "Остановлено. Попутные находки — ниже." : "Остановлено.", Tone.Info); return; }
            Found = result.Address.ToString();
            Name = $"Красивый {Core.History.Short(result.Address)}";
            Say($"Найден за {Display.Integer(result.Tries)} попыток. Добавьте его в кошелёк — {(kind == VanitySecret.PrivateKey ? "ключ" : "фраза")} сохранится под паролем кошелька.", Tone.Success);
        }
        finally { Running = false; stop.Dispose(); stop = null; Add.Refresh(); }
    }

    // The rarest finds stay, ten at most; the keys of those that drop out are wiped.
    private void Keep(VanityNotable notable)
    {
        if (Notables.Count >= NotablesKept && notable.Rarity <= Notables[^1].Notable.Rarity) { Wipe(notable.Result); return; }
        var at = 0;
        while (at < Notables.Count && Notables[at].Notable.Rarity >= notable.Rarity) at++;
        Notables.Insert(at, new NotableItem(notable, item => { if (AddResult(item.Notable.Result)) { Notables.Remove(item); Changed(nameof(HasNotables)); } }));
        while (Notables.Count > NotablesKept) { Wipe(Notables[^1].Notable.Result); Notables.RemoveAt(Notables.Count - 1); }
        Changed(nameof(HasNotables));
    }

    private bool AddResult(VanityResult r)
    {
        if (main.Shell.Session is not { } session) return false;
        var title = Name.Trim().Length > 0 && r == result ? Name.Trim() : $"Красивый {Core.History.Short(r.Address)}";
        try
        {
            var account = r.PrivateKey != null ? session.AddPrivateKey(r.PrivateKey, title) : session.AddMnemonic(r.Mnemonic!, title);
            Wipe(r);
            main.LoadAccounts(account.Id);
            Say($"Добавлен счёт «{account.Name}»: {Display.Address(account.Address)}. Он уже выбран.", Tone.Success);
            return true;
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException) { Fail(e); return false; }
    }

    private static void Wipe(VanityResult r) { if (r.PrivateKey is { } key) System.Security.Cryptography.CryptographicOperations.ZeroMemory(key); }

    // A new search starts clean: the last result and the finds go, their keys wiped.
    private void Clear()
    {
        if (result is { } r) Wipe(r);
        foreach (var n in Notables) Wipe(n.Notable.Result);
        Notables.Clear(); Changed(nameof(HasNotables));
        result = null; Found = ""; ProgressText = "";
        Add.Refresh();
    }
}
