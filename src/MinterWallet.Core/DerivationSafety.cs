using Minter.Protocol;

namespace MinterWallet.Core;

// Only observed activity on the standard branch extends BIP44 recovery's 20-address gap. Saving a distant path
// cannot bridge unused addresses before it. The first unused account ends account discovery as well.
public static class DerivationSafety
{
    public const uint Gap = 20;
    public static uint Frontier(IEnumerable<uint> used)
    {
        var frontier = Gap - 1;
        foreach (var index in used.Distinct().Order())
        {
            if (index > frontier) break;
            frontier = (uint)Math.Min(HdPath.MaxIndex, (ulong)index + Gap);
        }
        return frontier;
    }

    public static bool Recoverable(HdPath path, IEnumerable<string> usedPaths)
    {
        if (!path.IsStandardMinter) return false;
        var groups = usedPaths.Select(HdPath.Parse).Where(p => p.IsStandardMinter)
            .GroupBy(p => p.Account).ToDictionary(g => g.Key, g => g.Select(p => p.Index).ToArray());
        if (path.Account > groups.Count) return false;
        for (uint account = 0; account < path.Account; account++)
            if (!groups.TryGetValue(account, out var indices) || !indices.Any(i => i <= Frontier(indices))) return false;
        return path.Index <= Frontier(groups.GetValueOrDefault(path.Account) ?? []);
    }
}
