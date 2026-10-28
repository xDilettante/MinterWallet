using System.Globalization;
using System.Numerics;
using System.Text;
using Minter.Protocol;

namespace MinterWallet;

// Text the views show: amounts for reading (grouped, short), heights, and recovery phrases as people paste them.
public static class Display
{
    private const char Gap = ' ';

    // The settings' «Вид»: every decimal of an amount, and addresses in groups of four.
    public static bool AllDecimals { get; private set; }
    public static bool GroupAddresses { get; private set; }

    public static void Use(Core.WalletSettings settings) => (AllDecimals, GroupAddresses) = (settings.AllDecimals, settings.GroupAddresses);

    // "1 234 567.8912": the whole part in groups of three and `decimals` fraction digits; below 1, as many as it takes
    // to show `significant` digits (0.0001234), so a small amount is not shown as nothing. Cut rather than rounded, so
    // an amount is never shown larger than it is; AllDecimals shows every digit. The exact value: Units.FormatCoins.
    public static string Amount(BigInteger pip, int decimals = 4, int significant = 4) => Format(pip, decimals, significant, AllDecimals);

    // Dollars from an amount in pip of USD (18 decimals): cents from $1 up, three significant digits below ($0.000412).
    // An estimate, so never every decimal.
    public static string Usd(BigInteger pip) => "$" + Format(pip, 2, 3, false);

    // An address as the settings ask: in one piece (as everywhere in Minter) or in groups of four.
    public static string Address(Address address) => GroupAddresses ? Core.AddressBook.Grouped(address) : address.ToString();

    private static string Format(BigInteger pip, int decimals, int significant, bool all)
    {
        if (pip.Sign < 0) return "−" + Format(-pip, decimals, significant, all);
        var text = Units.FormatCoins(pip);
        var dot = text.IndexOf('.');
        var whole = dot < 0 ? text : text[..dot];
        var fraction = dot < 0 ? "" : text[(dot + 1)..];
        if (!all)
        {
            var keep = whole == "0" ? Math.Max(decimals, fraction.Length - fraction.TrimStart('0').Length + significant) : decimals;
            if (fraction.Length > keep) fraction = fraction[..keep].TrimEnd('0');
        }
        return Group(whole) + (fraction.Length > 0 ? "." + fraction : "");
    }

    public static string Integer(long value) => value < 0 ? "−" + Group((-value).ToString(CultureInfo.InvariantCulture)) : Group(value.ToString(CultureInfo.InvariantCulture));

    private static string Group(string digits)
    {
        var text = new StringBuilder(digits.Length + digits.Length / 3);
        for (var i = 0; i < digits.Length; i++)
        {
            if (i > 0 && (digits.Length - i) % 3 == 0) text.Append(Gap);
            text.Append(digits[i]);
        }
        return text.ToString();
    }

    // The words of a pasted phrase. BIP39 words are letters only, so numbering ("1." "2)"), commas, line breaks and
    // any other separators from another wallet's copy fall away.
    public static string[] PhraseWords(string text)
    {
        var letters = new StringBuilder(text.Length);
        foreach (var c in text) letters.Append(char.IsLetter(c) ? char.ToLowerInvariant(c) : ' ');
        return letters.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    public static string Phrase(string text) => string.Join(' ', PhraseWords(text));

    private static readonly Lazy<HashSet<string>> Known = new(() => [.. Mnemonic.Words]);

    // What is wrong with a phrase as typed so far, or null when it is a valid BIP39 phrase.
    public static string? PhraseProblem(string text)
    {
        var words = PhraseWords(text);
        if (words.Length == 0) return "";
        var unknown = words.Where(w => !Known.Value.Contains(w)).Distinct().Take(3).ToArray();
        if (unknown.Length > 0) return (unknown.Length == 1 ? "Нет такого слова в словаре BIP39: " : "Нет таких слов в словаре BIP39: ") + string.Join(", ", unknown);
        if (words.Length is not (12 or 15 or 18 or 21 or 24)) return $"{Words(words.Length)} — нужно 12, 15, 18, 21 или 24.";
        return Mnemonic.IsValid(string.Join(' ', words)) ? null : "Слова есть в словаре, но контрольная сумма не сходится: проверьте порядок и каждое слово.";
    }

    public static string Words(int count) => count % 10 == 1 && count % 100 != 11 ? $"{count} слово"
        : count % 10 is >= 2 and <= 4 && count % 100 is < 12 or > 14 ? $"{count} слова" : $"{count} слов";
}
