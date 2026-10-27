using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace MinterWallet;

// Text with Mx addresses whose first and last characters stand out (accent colour, semibold): the parts the short form
// "Mx3798…0149" shows elsewhere. Set AddressMarks.Text instead of Text on any TextBlock; the text is translated like
// any other, and copying a selection copies the plain text.
public static partial class AddressMarks
{
    public static readonly AttachedProperty<string?> TextProperty = AvaloniaProperty.RegisterAttached<TextBlock, string?>("Text", typeof(AddressMarks));
    private static readonly AttachedProperty<List<IDisposable>?> BindingsProperty = AvaloniaProperty.RegisterAttached<TextBlock, List<IDisposable>?>("Bindings", typeof(AddressMarks));

    static AddressMarks() => TextProperty.Changed.AddClassHandler<TextBlock>((block, _) => Build(block));

    public static string? GetText(TextBlock block) => block.GetValue(TextProperty);
    public static void SetText(TextBlock block, string? value) => block.SetValue(TextProperty, value);

    private static void Build(TextBlock block)
    {
        if (block.GetValue(BindingsProperty) is { } old) foreach (var binding in old) binding.Dispose();
        var bindings = new List<IDisposable>();
        block.SetValue(BindingsProperty, bindings);
        var text = Localization.Text(GetText(block) ?? "");
        var inlines = new InlineCollection();
        var at = 0;
        foreach (Match match in AddressRegex().Matches(text))
        {
            if (match.Index > at) inlines.Add(new Run(text[at..match.Index]));
            var (head, tail) = Edges(match.Value);
            inlines.Add(Marked(block, match.Value[..head], bindings));
            inlines.Add(new Run(match.Value[head..tail]));
            inlines.Add(Marked(block, match.Value[tail..], bindings));
            at = match.Index + match.Length;
        }
        if (at < text.Length) inlines.Add(new Run(text[at..]));
        block.Inlines = inlines;
    }

    // "Mx" with the next four hex digits, and the last four; an address in groups ("Mx 3798 … 0149") the same way.
    private static (int Head, int Tail) Edges(string address)
    {
        int head = 2, seen = 0;
        while (seen < 4) { if (address[head] != ' ') seen++; head++; }
        int tail = address.Length, back = 0;
        while (back < 4) { tail--; if (address[tail] != ' ') back++; }
        return (head, tail);
    }

    private static Run Marked(TextBlock block, string text, List<IDisposable> bindings)
    {
        var run = new Run(text) { FontWeight = FontWeight.SemiBold };
        bindings.Add(run.Bind(TextElement.ForegroundProperty, block.GetResourceObservable("Accent")));
        return run;
    }

    [GeneratedRegex(@"(?<![0-9A-Za-z])Mx(?: ?[0-9a-fA-F]){40}(?![0-9a-fA-F])")]
    private static partial Regex AddressRegex();
}
