using System.Collections.ObjectModel;
using System.Numerics;

namespace MinterWallet;

public sealed record CoinOption(uint Id, string Symbol, string Name, BigInteger Liquidity, string Note)
{
    public string Initial => Symbol.Length > 0 ? Symbol[..1] : "?";
    public string Caption => Name.Length > 0 && !string.Equals(Name, Symbol, StringComparison.OrdinalIgnoreCase) ? Name : "";
    public bool HasCaption => Caption.Length > 0;
    public bool HasNote => Note.Length > 0;
    public override string ToString() => Symbol;
}

// A coin chosen by its ticker or name, never by id. With a MinterGate as the source the list is every coin that can be
// bought, most liquid first; a plain node has no such list, so a ticker typed in full is looked up instead.
public sealed class CoinPickerViewModel : Observable
{
    private readonly Func<IReadOnlyList<CoinOption>> options;
    private readonly Func<bool> complete;
    private readonly Func<string, Task<CoinOption?>> lookup;
    private string query = "";
    private CoinOption? selected;
    private bool searching;
    private int generation;

    public CoinPickerViewModel(Func<IReadOnlyList<CoinOption>> options, Func<bool> complete, Func<string, Task<CoinOption?>> lookup)
    {
        this.options = options; this.complete = complete; this.lookup = lookup;
    }

    public ObservableCollection<CoinOption> Results { get; } = [];
    public string Query { get => query; set { if (Set(ref query, value)) Refresh(); } }
    public CoinOption? Selected
    {
        get => selected;
        set
        {
            if (!Set(ref selected, value)) return;
            Changed(nameof(Title)); Changed(nameof(Subtitle)); Changed(nameof(HasSelection));
            SelectedChanged?.Invoke();
        }
    }
    public bool HasSelection => Selected != null;
    public string Title => Selected?.Symbol ?? "Выберите монету";
    public string Subtitle => Selected?.Caption ?? "";
    public bool Searching { get => searching; private set { Set(ref searching, value); Changed(nameof(EmptyText)); Changed(nameof(HasEmptyText)); } }
    public string EmptyText => Searching ? "Поиск…" : Results.Count > 0 ? ""
        : Query.Trim().Length == 0 ? "Источник не отдаёт список монет — введите тикер, например BIP."
        : "Не нашлось. Проверьте тикер — его можно узнать у отправителя или в обозревателе Minter.";
    public bool HasEmptyText => EmptyText.Length > 0;
    public event Action? SelectedChanged;

    // Ticker matches first, then tickers and names that contain the text; a full ticker missing from the list is
    // asked from the source.
    public void Refresh()
    {
        var text = Query.Trim();
        var all = options();
        var found = text.Length == 0 ? all
            : all.Where(c => c.Symbol.StartsWith(text, StringComparison.OrdinalIgnoreCase))
                .Concat(all.Where(c => !c.Symbol.StartsWith(text, StringComparison.OrdinalIgnoreCase)
                    && (c.Symbol.Contains(text, StringComparison.OrdinalIgnoreCase) || c.Name.Contains(text, StringComparison.OrdinalIgnoreCase))));
        Results.Clear();
        foreach (var c in found.Take(60)) Results.Add(c);
        var mine = ++generation;
        var exact = Results.Any(c => c.Symbol.Equals(text, StringComparison.OrdinalIgnoreCase));
        if (!exact && !complete() && text.Length is >= 1 and <= 14 && text.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) _ = LookupAsync(text, mine);
        else Searching = false;
        Changed(nameof(EmptyText)); Changed(nameof(HasEmptyText));
    }

    private async Task LookupAsync(string text, int mine)
    {
        Searching = true;
        await Task.Delay(300);
        if (mine != generation) return;
        CoinOption? found = null;
        try { found = await lookup(text); }
        catch (Exception) { }
        if (mine != generation) return;
        if (found != null && Results.All(c => c.Id != found.Id)) Results.Insert(0, found);
        Searching = false;
    }
}
