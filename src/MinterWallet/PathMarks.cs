using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace MinterWallet;

// The same path in cards and beside the selected address. Colour groups: standard/profile, account, branch, index.
public static class PathMarks
{
    public static readonly AttachedProperty<string?> TextProperty = AvaloniaProperty.RegisterAttached<TextBlock, string?>("Text", typeof(PathMarks));
    private static readonly AttachedProperty<List<IDisposable>?> BindingsProperty = AvaloniaProperty.RegisterAttached<TextBlock, List<IDisposable>?>("Bindings", typeof(PathMarks));
    static PathMarks() => TextProperty.Changed.AddClassHandler<TextBlock>((block, _) =>
    {
        if (block.GetValue(BindingsProperty) is { } old) foreach (var binding in old) binding.Dispose();
        var bindings = new List<IDisposable>();
        var parts = (GetText(block) ?? "").Split('/');
        var inlines = new InlineCollection();
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0) inlines.Add(new Run("/"));
            var run = new Run(parts[i]);
            var brush = i is 1 or 2 ? "Muted" : i == 3 ? "Prefix" : i == 4 ? "Success" : "Accent";
            bindings.Add(run.Bind(TextElement.ForegroundProperty, block.GetResourceObservable(brush)));
            inlines.Add(run);
        }
        block.SetValue(BindingsProperty, bindings);
        block.Inlines = inlines;
    });
    public static string? GetText(TextBlock block) => block.GetValue(TextProperty);
    public static void SetText(TextBlock block, string? text) => block.SetValue(TextProperty, text);
}
