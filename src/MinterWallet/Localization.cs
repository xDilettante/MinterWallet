using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;

namespace MinterWallet;

// The interface in English. Views and models speak Russian; when the language is English, every text a TextBlock is
// about to show (labels, buttons, field hints, tooltips, notices, the confirmation) is translated on its way to the
// screen: exact texts from Translations, texts built from values through its templates ({0}, {1}…), whose values are
// translated in turn. The models keep Russian, so switching the language only redraws the views.
public static partial class Localization
{
    private static readonly Dictionary<string, string> Exact = new(StringComparer.Ordinal);
    private static readonly List<(Regex Pattern, string English, int Count)> Templates = [];
    private static bool attached;

    public static bool English { get; private set; }

    static Localization()
    {
        foreach (var (ru, en) in Translations.Pairs)
        {
            var slots = PlaceholderRegex().Matches(ru);
            if (slots.Count == 0) { Exact[ru] = en; continue; }
            var pattern = new System.Text.StringBuilder("^");
            var at = 0; var count = 0;
            foreach (Match slot in slots)
            {
                pattern.Append(Regex.Escape(ru[at..slot.Index])).Append("(?<p").Append(slot.Groups[1].Value).Append(">.+?)");
                at = slot.Index + slot.Length;
                count = Math.Max(count, int.Parse(slot.Groups[1].Value, CultureInfo.InvariantCulture) + 1);
            }
            pattern.Append(Regex.Escape(ru[at..])).Append('$');
            Templates.Add((new Regex(pattern.ToString(), RegexOptions.Singleline | RegexOptions.CultureInvariant), en, count));
        }
        // The template with more fixed text wins: "Ещё 1 символ — …" before "Ещё {0} символа — …".
        Templates.Sort((a, b) => FixedLength(b.Pattern).CompareTo(FixedLength(a.Pattern)));
    }

    private static int FixedLength(Regex pattern) => PlaceholderGroupRegex().Replace(pattern.ToString(), "").Length;

    // "system" follows the OS (Russian for ru, uk, be, kk interfaces; English otherwise), "ru" and "en" are fixed.
    public static void Use(string? language) => English = language switch { "en" => true, "ru" => false, _ => !SystemSpeaksRussian() };

    // Once per process: the translation hook of every TextBlock (SelectableTextBlock included).
    public static void Attach()
    {
        if (attached) return;
        attached = true;
        TextBlock.TextProperty.Changed.AddClassHandler<TextBlock>((block, e) =>
        {
            if (!English || e.NewValue is not string text) return;
            var english = Translate(text);
            if (!string.Equals(english, text, StringComparison.Ordinal)) block.SetCurrentValue(TextBlock.TextProperty, english);
        });
    }

    public static string Text(string russian) => English ? Translate(russian) : russian;

    public static string Translate(string text)
    {
        if (!HasCyrillic(text)) return text;
        if (Exact.TryGetValue(text, out var english)) return english;
        foreach (var (pattern, en, count) in Templates)
        {
            var match = pattern.Match(text);
            if (!match.Success) continue;
            var values = new object[count];
            for (var i = 0; i < count; i++) values[i] = match.Groups["p" + i.ToString(CultureInfo.InvariantCulture)] is { Success: true } g ? Translate(g.Value) : "";
            return string.Format(CultureInfo.InvariantCulture, en, values);
        }
        // A multi-line text no template covers whole (a confirmation row): line by line.
        return text.Contains('\n') ? string.Join('\n', text.Split('\n').Select(Translate)) : text;
    }

    public static bool HasCyrillic(string text)
    {
        foreach (var c in text) if (c is >= 'А' and <= 'я' or 'Ё' or 'ё') return true;
        return false;
    }

    private static bool SystemSpeaksRussian()
    {
        if (OperatingSystem.IsWindows())
        {
            var language = GetUserDefaultUILanguage() & 0x3FF;   // primary language of the LANGID
            return language is 0x19 or 0x22 or 0x23 or 0x3F;     // Russian, Ukrainian, Belarusian, Kazakh
        }
        var locale = Environment.GetEnvironmentVariable("LC_ALL") is { Length: > 0 } all ? all
            : Environment.GetEnvironmentVariable("LC_MESSAGES") is { Length: > 0 } messages ? messages
            : Environment.GetEnvironmentVariable("LANG") ?? "";
        return locale.StartsWith("ru", StringComparison.Ordinal) || locale.StartsWith("uk", StringComparison.Ordinal)
            || locale.StartsWith("be", StringComparison.Ordinal) || locale.StartsWith("kk", StringComparison.Ordinal);
    }

    [DllImport("kernel32.dll")]
    private static extern ushort GetUserDefaultUILanguage();

    [GeneratedRegex(@"\{(\d+)\}")]
    private static partial Regex PlaceholderRegex();

    [GeneratedRegex(@"\(\?<p\d+>\.\+\?\)")]
    private static partial Regex PlaceholderGroupRegex();
}
