using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Minter.Protocol;

namespace MinterWallet.Core;

public enum VanitySecret { PrivateKey, Mnemonic12, Mnemonic24 }

// What a vanity address should look like: the 40 hex digits after "Mx" (lower case) and how many tries it takes on
// average to meet one by chance.
public interface IVanityTemplate
{
    bool Matches(string hex);
    double ExpectedTries { get; }
}

// A beginning, an end, and text anywhere between them — hex digits only.
public sealed record VanityPattern(string Start = "", string End = "", string Anywhere = "") : IVanityTemplate
{
    public int Length => Start.Length + End.Length + Anywhere.Length;

    // 16 per fixed hex digit; text anywhere fits into more places.
    public double ExpectedTries => Anywhere.Length == 0 ? Math.Pow(16, Start.Length + End.Length)
        : Math.Pow(16, Length) / Math.Max(1, 40 - Length + 1);

    public bool Matches(string hex) =>
        hex.StartsWith(Start, StringComparison.Ordinal) && hex.EndsWith(End, StringComparison.Ordinal)
        && (Anywhere.Length == 0 || hex.AsSpan(Start.Length, 40 - Start.Length - End.Length).Contains(Anywhere, StringComparison.Ordinal));
}

// A mask over the whole address: hex digits as they are, ? any one digit, * any run (cafe*, *beef, cafe*beef, *c0ffee*).
public sealed class VanityMask : IVanityTemplate
{
    private readonly Regex regex;
    public string Text { get; }

    public VanityMask(string mask)
    {
        Text = mask;
        var pattern = new StringBuilder("^");
        foreach (var c in mask) pattern.Append(c switch { '?' => "[0-9a-f]", '*' => "[0-9a-f]*", _ => c.ToString() });
        regex = new Regex(pattern.Append('$').ToString(), RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }

    public bool Matches(string hex) => regex.IsMatch(hex);

    // 16 per fixed digit, fewer when a part floats between stars: it has the slack to move in.
    public double ExpectedTries
    {
        get
        {
            var fixedDigits = Text.Count(Uri.IsHexDigit);
            var slack = Math.Max(0, 40 - Text.Count(c => c != '*'));
            var parts = Text.Split('*');
            var floating = parts.Select((p, i) => p.Length > 0 && i > 0 && i < parts.Length - 1).Count(f => f);
            return Math.Pow(16, fixedDigits) / Math.Pow(slack + 1, floating);
        }
    }

    // What is wrong with a mask, or null.
    public static string? Problem(string mask)
    {
        foreach (var c in mask) if (!Uri.IsHexDigit(c) && c is not ('?' or '*')) return $"«{c}» в маске быть не может: цифры 0–9, буквы a–f, ? и *.";
        if (!mask.Contains('*') && mask.Length != 40) return "Маска без * должна быть ровно 40 символов: допишите * в конце, чтобы задать только начало.";
        if (mask.Count(c => c != '*') > 40) return "В маске больше 40 символов, а в адресе их 40.";
        return null;
    }
}

public enum VanityPlace { Start, End, Anywhere }

// The same digit repeated: Count of them at the beginning, at the end, or in a row anywhere.
public sealed record VanityRepeat(VanityPlace Place, int Count) : IVanityTemplate
{
    public bool Matches(string hex) => Place switch
    {
        VanityPlace.Start => Vanity.LeadingRun(hex) >= Count,
        VanityPlace.End => Vanity.TrailingRun(hex) >= Count,
        _ => Vanity.LongestRun(hex) >= Count,
    };
    public double ExpectedTries => Place == VanityPlace.Anywhere ? Math.Pow(16, Count - 1) / Math.Max(1, 41 - Count) : Math.Pow(16, Count - 1);
}

// The first Count digits equal to the last Count (Mx1234…1234), or mirrored (Mx1234…4321).
public sealed record VanityEdges(int Count, bool Mirror) : IVanityTemplate
{
    public bool Matches(string hex) => (Mirror ? Vanity.MirroredEdges(hex) : Vanity.EqualEdges(hex)) >= Count;
    public double ExpectedTries => Math.Pow(16, Count);
}

public sealed record VanityResult(Address Address, byte[]? PrivateKey, string? Mnemonic, long Tries, TimeSpan Elapsed);

// An address found by chance that has something popular about it, and how rare that is (1 in Rarity).
public sealed record VanityNotable(VanityResult Result, string What, double Rarity);

// The search: random keys (fast) or random recovery phrases (each needs the BIP39 seed: about a thousand times slower)
// on every core until one address fits. On the way every address is also rated for popular features (repeats, words,
// sequences, equal or mirrored ends); the rare ones go to notable(). Everything stays in memory on this device; keys
// that are not kept are wiped.
public static class Vanity
{
    // Hex digits only. Null when it is fine.
    public static string? Problem(string text)
    {
        foreach (var c in text) if (!Uri.IsHexDigit(c)) return $"«{c}» в адресе быть не может: только цифры 0–9 и латинские буквы a–f.";
        return null;
    }

    // The chance to meet a template within a number of tries.
    public static double Chance(double expectedTries, double tries) => 1 - Math.Exp(-tries / Math.Max(expectedTries, 1));

    public static async Task<VanityResult?> FindAsync(IVanityTemplate template, VanitySecret kind, Action<long>? progress = null, CancellationToken ct = default,
        Action<VanityNotable>? notable = null, double notableFrom = 65536)
    {
        var clock = Stopwatch.StartNew();
        long tries = 0;
        VanityResult? found = null;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var workers = Enumerable.Range(0, Math.Max(1, Environment.ProcessorCount - 1)).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                var (address, key, phrase) = Candidate(kind);
                var n = Interlocked.Increment(ref tries);
                if (n % 256 == 0) progress?.Invoke(n);
                var hex = address.ToString()[2..];
                if (template.Matches(hex))
                {
                    if (Interlocked.CompareExchange(ref found, new VanityResult(address, key, phrase, n, clock.Elapsed), null) == null) stop.Cancel();
                    else if (key != null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
                    return;
                }
                if (notable != null && Rate(hex) is { } rated && rated.Rarity >= notableFrom)
                {
                    notable(new VanityNotable(new VanityResult(address, key, phrase, n, clock.Elapsed), rated.What, rated.Rarity));
                    continue;   // the key now belongs to the notable list
                }
                if (key != null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
            }
        }, CancellationToken.None)).ToArray();
        await Task.WhenAll(workers);
        progress?.Invoke(Interlocked.Read(ref tries));
        return found;
    }

    private static (Address Address, byte[]? Key, string? Phrase) Candidate(VanitySecret kind)
    {
        if (kind == VanitySecret.PrivateKey)
        {
            using var key = PrivateKey.Generate();
            return (key.Address, key.ExportBytes(), null);
        }
        var phrase = Mnemonic.Generate(kind == VanitySecret.Mnemonic24 ? 24 : 12);
        using var derived = HdKey.FromMnemonic(phrase, 0);
        return (derived.Address, null, phrase);
    }

    // Popular words in hex (0 for o, 1 for i, 5 for s, 7 for t) and numbers people like.
    public static readonly string[] Words =
    [
        "deadbeef", "cafebabe", "c0ffee", "decade", "facade", "faded", "beef", "cafe", "dead", "face", "babe", "fade", "feed", "f00d",
        "c0de", "deaf", "bead", "abba", "5afe", "7ea", "1dea", "b0b", "ace", "bad", "dad", "add", "1337", "777777", "888888",
        "1234567", "abcdef", "100500", "2026", "2025",
    ];

    // The rarest popular feature of an address (1 in Rarity by chance), or null when it has none.
    public static (string What, double Rarity)? Rate(string hex)
    {
        var best = 0.0; var what = "";
        void Take(double rarity, string text) { if (rarity > best) { best = rarity; what = text; } }

        var lead = LeadingRun(hex);
        if (lead >= 4) Take(Math.Pow(16, lead - 1), $"{lead} одинаковых в начале");
        var trail = TrailingRun(hex);
        if (trail >= 4) Take(Math.Pow(16, trail - 1), $"{trail} одинаковых в конце");
        var run = LongestRun(hex);
        if (run >= 6) Take(Math.Pow(16, run - 1) / (41 - run), $"{run} одинаковых подряд");
        var equal = EqualEdges(hex);
        if (equal >= 4) Take(Math.Pow(16, equal), $"начало и конец совпадают: {equal} символов");
        var mirror = MirroredEdges(hex);
        if (mirror >= 4) Take(Math.Pow(16, mirror), $"зеркальные края: {mirror} символов");
        var up = Sequence(hex, 1);
        if (up >= 5) Take(Math.Pow(16, up - 1), $"последовательность {hex[..up]} в начале");
        var down = Sequence(hex, -1);
        if (down >= 5) Take(Math.Pow(16, down - 1), $"последовательность {hex[..down]} в начале");
        foreach (var word in Words)
        {
            if (hex.StartsWith(word, StringComparison.Ordinal)) Take(Math.Pow(16, word.Length), $"«{word}» в начале");
            else if (hex.EndsWith(word, StringComparison.Ordinal)) Take(Math.Pow(16, word.Length), $"«{word}» в конце");
        }
        return best > 0 ? (what, best) : null;
    }

    public static int LeadingRun(string hex) { var n = 1; while (n < hex.Length && hex[n] == hex[0]) n++; return n; }
    public static int TrailingRun(string hex) { var n = 1; while (n < hex.Length && hex[^(n + 1)] == hex[^1]) n++; return n; }
    public static int LongestRun(string hex)
    {
        int best = 1, run = 1;
        for (var i = 1; i < hex.Length; i++) { run = hex[i] == hex[i - 1] ? run + 1 : 1; if (run > best) best = run; }
        return best;
    }
    public static int EqualEdges(string hex) { var k = 0; for (var n = 1; n <= 20; n++) if (hex.AsSpan(0, n).SequenceEqual(hex.AsSpan(hex.Length - n))) k = n; return k; }
    public static int MirroredEdges(string hex) { var k = 0; while (k < 20 && hex[k] == hex[^(k + 1)]) k++; return k; }
    // How many digits at the start go up (step 1) or down (step -1) one by one: 01234…, abcdef…, 98765…
    private static int Sequence(string hex, int step)
    {
        var n = 1;
        while (n < hex.Length && Digit(hex[n]) - Digit(hex[n - 1]) == step) n++;
        return n;
    }
    private static int Digit(char c) => c <= '9' ? c - '0' : c - 'a' + 10;
}
