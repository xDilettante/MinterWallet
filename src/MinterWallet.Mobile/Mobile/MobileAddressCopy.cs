using System.Text.RegularExpressions;
using Avalonia.Controls;

namespace MinterWallet.Mobile;

public static partial class MobileAddressCopy
{
    // Never copy an ellipsis or guess which party the user meant in a line containing several addresses.
    public static string? AddressOf(TextBlock block)
    {
        var addresses = AddressPattern().Matches(AddressMarks.GetOriginalText(block) ?? "").Select(m => m.Value.Replace(" ", "", StringComparison.Ordinal)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return addresses.Length == 1 ? addresses[0] : null;
    }

    [GeneratedRegex(@"(?<![0-9A-Za-z])(?:Mx(?: ?[0-9a-fA-F]){40}|0x[0-9a-fA-F]{40})(?![0-9a-fA-F])")]
    private static partial Regex AddressPattern();
}
