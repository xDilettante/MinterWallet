using System.Globalization;
using System.Numerics;
using System.Text;
using Minter.Protocol;

namespace MinterWallet;

// Text the views show: amounts for reading (grouped, short), heights, and recovery phrases as people paste them.
public static class Display
{
    private const char Gap = ' ';

    // "1 234 567.8912": the whole part in groups of three and at most `decimals` fraction digits, cut rather than rounded
    // so a balance is never shown larger than it is. The exact value stays available (Units.FormatCoins) for copying.
    public static string Amount(BigInteger pip, int decimals = 4)
    {
        if (pip.Sign < 0) return "−" + Amount(-pip, decimals);
        var text = Units.FormatCoins(pip);
        var dot = text.IndexOf('.');
        var whole = dot < 0 ? text : text[..dot];
        var fraction = dot < 0 ? "" : text[(dot + 1)..];
        if (fraction.Length > decimals) fraction = fraction[..decimals].TrimEnd('0');
        if (whole == "0" && fraction.Length == 0 && pip.Sign > 0) return decimals == 0 ? "< 1" : "< 0." + new string('0', decimals - 1) + "1";
        return Group(whole) + (fraction.Length > 0 ? "." + fraction : "");
    }

    // Dollars from an amount in pip of USD (18 decimals): two decimals, grouped; "< $0.01" for less than a cent.
    public static string Usd(BigInteger pip) => pip.Sign > 0 && pip < Units.PipPerCoin / 100 ? "< $0.01" : "$" + Amount(pip, 2);

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
        if (words.Length is not (12 or 15 or 18 or 21 or 24)) return $"{Words(words.Length)} — нужно 12 или 24.";
        return Mnemonic.IsValid(string.Join(' ', words)) ? null : "Слова есть в словаре, но контрольная сумма не сходится: проверьте порядок и каждое слово.";
    }

    public static string Words(int count) => count % 10 == 1 && count % 100 != 11 ? $"{count} слово"
        : count % 10 is >= 2 and <= 4 && count % 100 is < 12 or > 14 ? $"{count} слова" : $"{count} слов";
}
