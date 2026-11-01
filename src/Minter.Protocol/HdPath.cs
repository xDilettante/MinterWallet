using System.Globalization;

namespace Minter.Protocol;

// Canonical BIP32 coordinates. Indices and hardening are separate in the editor; signatures always use Text.
public sealed class HdPath
{
    public const uint MaxIndex = 0x7fffffff;
    private readonly uint[] nodes;
    private HdPath(uint[] nodes)
    {
        this.nodes = nodes;
        Text = "m/" + string.Join('/', nodes.Select(n => (n & MaxIndex).ToString(CultureInfo.InvariantCulture) + ((n & 0x80000000) != 0 ? "'" : "")));
    }
    public string Text { get; }
    public int Depth => nodes.Length;
    public uint Index => ValueAt(Depth - 1);
    public uint ValueAt(int level) => nodes[level] & MaxIndex;
    public bool HardenedAt(int level) => (nodes[level] & 0x80000000) != 0;
    public bool IsStandardMinter => Depth == 5 && nodes[0] == (44u | 0x80000000) && nodes[1] == (60u | 0x80000000)
        && HardenedAt(2) && nodes[3] == 0 && !HardenedAt(4);
    public uint Account => Depth == 5 ? ValueAt(2) : 0;

    public static HdPath Parse(string text)
    {
        var parts = text.Trim().Split('/');
        if (parts.Length is < 2 or > 256 || parts[0] != "m") throw BadPath();
        var nodes = new uint[parts.Length - 1];
        for (var i = 1; i < parts.Length; i++)
        {
            var part = parts[i];
            var hard = part.EndsWith('\'') || part.EndsWith('h') || part.EndsWith('H');
            if (!uint.TryParse(hard ? part[..^1] : part, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n > MaxIndex) throw BadPath();
            nodes[i - 1] = n | (hard ? 0x80000000u : 0);
        }
        return new HdPath(nodes);
    }
    public static HdPath Minter(uint index = 0, uint account = 0) => Parse($"m/44'/60'/{account}'/0/{index}");
    public HdPath WithIndex(uint index)
    {
        if (index > MaxIndex) throw BadPath();
        var copy = (uint[])nodes.Clone();
        copy[^1] = index | (HardenedAt(Depth - 1) ? 0x80000000u : 0);
        return new HdPath(copy);
    }
    private static FormatException BadPath() => new("Путь: m/44'/60'/0'/0/0. Индексы — целые от 0 до 2147483647, уровней от 1 до 255.");
    public override string ToString() => Text;
}
