using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace MinterWallet;

// Minter values in text: the prefix of an address, transaction hash, public key or check (Mx, Mt, Mp, Mc) in its own
// colour, full or short ("Mx3798…0149"); a full address also has its first and last characters stand out (accent,
// semibold), the parts the short form shows. Attach() does it for every TextBlock; AddressMarks.Text instead of Text
// does it for a text that is translated here too. Copying a selection copies the plain text.
public static partial class AddressMarks
{
    public static readonly AttachedProperty<string?> TextProperty = AvaloniaProperty.RegisterAttached<TextBlock, string?>("Text", typeof(AddressMarks));
    private static readonly AttachedProperty<List<IDisposable>?> BindingsProperty = AvaloniaProperty.RegisterAttached<TextBlock, List<IDisposable>?>("Bindings", typeof(AddressMarks));
    private static readonly AttachedProperty<bool> MarkedProperty = AvaloniaProperty.RegisterAttached<TextBlock, bool>("Marked", typeof(AddressMarks));
    private static bool attached;

    static AddressMarks() => TextProperty.Changed.AddClassHandler<TextBlock>((block, _) => Fill(block, Localization.Text(GetText(block) ?? "")));

    public static string? GetText(TextBlock block) => block.GetValue(TextProperty);
    public static void SetText(TextBlock block, string? value) => block.SetValue(TextProperty, value);

    // Once per process, after Localization.Attach: the text a TextBlock shows (already translated) is marked.
    public static void Attach()
    {
        if (attached) return;
        attached = true;
        TextBlock.TextProperty.Changed.AddClassHandler<TextBlock>((block, _) =>
        {
            if (block.IsSet(TextProperty)) return;
            var text = block.Text ?? "";
            if (ValueRegex().IsMatch(text)) Fill(block, text);
            else if (block.GetValue(MarkedProperty)) { Release(block); block.Inlines?.Clear(); block.SetValue(MarkedProperty, false); }
        });
    }

    private static void Fill(TextBlock block, string text)
    {
        Release(block);
        var bindings = new List<IDisposable>();
        block.SetValue(BindingsProperty, bindings);
        var inlines = new InlineCollection();
        var at = 0;
        foreach (Match match in ValueRegex().Matches(text))
        {
            if (match.Index > at) inlines.Add(new Run(text[at..match.Index]));
            var value = match.Value;
            inlines.Add(Colored(block, value[..2], "Prefix", bindings));
            if (IsFullAddress(value))
            {
                var (head, tail) = Edges(value);
                inlines.Add(Colored(block, value[2..head], "Accent", bindings));
                inlines.Add(new Run(value[head..tail]));
                inlines.Add(Colored(block, value[tail..], "Accent", bindings));
            }
            else inlines.Add(new Run(value[2..]));
            at = match.Index + match.Length;
        }
        if (at < text.Length) inlines.Add(new Run(text[at..]));
        block.Inlines = inlines;
        block.SetValue(MarkedProperty, true);
    }

    private static void Release(TextBlock block)
    {
        if (block.GetValue(BindingsProperty) is { } old) foreach (var binding in old) binding.Dispose();
        block.SetValue(BindingsProperty, null);
    }

    private static bool IsFullAddress(string value) => value[1] == 'x' && !value.Contains('…') && value.Count(Uri.IsHexDigit) == 40;

    // The four hex digits after "Mx" and the last four; an address in groups ("Mx 3798 … 0149") the same way.
    private static (int Head, int Tail) Edges(string address)
    {
        int head = 2, seen = 0;
        while (seen < 4) { if (address[head] != ' ') seen++; head++; }
        int tail = address.Length, back = 0;
        while (back < 4) { tail--; if (address[tail] != ' ') back++; }
        return (head, tail);
    }

    private static Run Colored(TextBlock block, string text, string brush, List<IDisposable> bindings)
    {
        var run = new Run(text) { FontWeight = FontWeight.SemiBold };
        bindings.Add(run.Bind(TextElement.ForegroundProperty, block.GetResourceObservable(brush)));
        return run;
    }

    // A full address (40 hex digits, or in groups of four), a hash, key or check (64 and more), or a short form.
    [GeneratedRegex(@"(?<![0-9A-Za-z])(?:Mx(?: ?[0-9a-fA-F]){40}(?![0-9a-fA-F])|M[xtpc][0-9a-fA-F]{64,}|M[xtpc][0-9a-fA-F]{2,10}…[0-9a-fA-F]{2,8})")]
    private static partial Regex ValueRegex();
}
