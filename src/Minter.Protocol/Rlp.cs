using System.Numerics;
using System.Text;

namespace Minter.Protocol;

// Recursive Length Prefix as encoded by go-ethereum rlp (used by minter-go-node). Encoding is always canonical;
// decoding rejects everything Go's rlp.DecodeBytes rejects for these shapes: non-minimal lengths, single bytes
// wrapped in a string header, integers with leading zeros, trailing bytes.
public static class Rlp
{
    public static byte[] Bytes(ReadOnlySpan<byte> value)
    {
        if (value.Length == 1 && value[0] < 0x80) return [value[0]];
        return [.. Header(0x80, value.Length), .. value];
    }
    public static byte[] String(string value) => Bytes(Encoding.UTF8.GetBytes(value));
    public static byte[] Uint(ulong value)
    {
        if (value == 0) return [0x80];
        Span<byte> buffer = stackalloc byte[8];
        var start = 8;
        while (value != 0) { buffer[--start] = (byte)value; value >>= 8; }
        return Bytes(buffer[start..]);
    }
    public static byte[] Big(BigInteger value)
    {
        if (value.Sign < 0) throw new ArgumentOutOfRangeException(nameof(value), "RLP integers are unsigned.");
        return value.IsZero ? [0x80] : Bytes(value.ToByteArray(isUnsigned: true, isBigEndian: true));
    }
    public static byte[] Bool(bool value) => value ? [0x01] : [0x80];
    public static byte[] List(params IEnumerable<byte[]> items)
    {
        var parts = items as byte[][] ?? items.ToArray();
        var length = 0;
        foreach (var part in parts) length += part.Length;
        var result = new byte[HeaderLength(length) + length];
        var offset = WriteHeader(result, 0xc0, length);
        foreach (var part in parts) { part.CopyTo(result, offset); offset += part.Length; }
        return result;
    }

    private static byte[] Header(byte offset, int length)
    {
        var header = new byte[HeaderLength(length)];
        WriteHeader(header, offset, length);
        return header;
    }
    private static int HeaderLength(int length) => length < 56 ? 1 : 1 + ByteCount(length);
    private static int WriteHeader(Span<byte> target, byte offset, int length)
    {
        if (length < 56) { target[0] = (byte)(offset + length); return 1; }
        var count = ByteCount(length);
        target[0] = (byte)(offset + 55 + count);
        for (var i = count; i >= 1; i--) { target[i] = (byte)length; length >>= 8; }
        return 1 + count;
    }
    private static int ByteCount(int value) { var n = 0; while (value != 0) { n++; value >>= 8; } return n; }

    public static RlpItem Decode(ReadOnlySpan<byte> data)
    {
        var item = Read(data.ToArray(), 0, data.Length, out var consumed);
        if (consumed != data.Length) throw new FormatException("Trailing bytes after the RLP value.");
        return item;
    }

    private static RlpItem Read(byte[] buffer, int offset, int end, out int consumed)
    {
        if (offset >= end) throw new FormatException("Unexpected end of RLP data.");
        var prefix = buffer[offset];
        if (prefix < 0x80) { consumed = 1; return new RlpItem(buffer.AsMemory(offset, 1)); }
        bool isList = prefix >= 0xc0;
        var b = isList ? prefix - 0xc0 : prefix - 0x80;
        int header, length;
        if (b < 56) { header = 1; length = b; }
        else
        {
            var count = b - 55;
            if (count > 4 || offset + 1 + count > end) throw new FormatException("Bad RLP length.");
            if (buffer[offset + 1] == 0) throw new FormatException("Non-canonical RLP length (leading zero).");
            long value = 0;
            for (var i = 1; i <= count; i++) value = (value << 8) | buffer[offset + i];
            if (value < 56 || value > int.MaxValue) throw new FormatException("Non-canonical RLP length.");
            header = 1 + count; length = (int)value;
        }
        if ((long)offset + header + length > end) throw new FormatException("RLP value exceeds its container.");
        consumed = header + length;
        var start = offset + header;
        if (!isList)
        {
            if (length == 1 && buffer[start] < 0x80) throw new FormatException("Non-canonical RLP: single byte below 0x80 in a string header.");
            return new RlpItem(buffer.AsMemory(start, length));
        }
        var items = new List<RlpItem>();
        var position = start;
        while (position < start + length)
        {
            items.Add(Read(buffer, position, start + length, out var used));
            position += used;
        }
        return new RlpItem(items);
    }
}

public sealed class RlpItem
{
    private readonly ReadOnlyMemory<byte> bytes;
    private readonly List<RlpItem>? items;

    public RlpItem(ReadOnlyMemory<byte> bytes) => this.bytes = bytes;
    public RlpItem(List<RlpItem> items) => this.items = items;

    public bool IsList => items != null;
    public IReadOnlyList<RlpItem> Items => items ?? throw new FormatException("Expected an RLP list.");
    public int Count => Items.Count;
    public RlpItem this[int index] => index < Items.Count ? Items[index] : throw new FormatException($"RLP list has no element {index}.");
    public ReadOnlySpan<byte> Span => IsList ? throw new FormatException("Expected an RLP string.") : bytes.Span;
    public byte[] ToBytes() => Span.ToArray();

    public RlpItem ExpectCount(int count)
    {
        if (Items.Count != count) throw new FormatException($"Expected {count} RLP elements, got {Items.Count}.");
        return this;
    }
    public byte[] Fixed(int length)
    {
        if (Span.Length != length) throw new FormatException($"Expected {length} bytes, got {Span.Length}.");
        return ToBytes();
    }
    public ulong UInt64() => (ulong)Integer(8);
    public uint UInt32() => (uint)Integer(4);
    public byte Byte() => (byte)Integer(1);
    public BigInteger Big()
    {
        var span = Span;
        if (span.Length > 0 && span[0] == 0) throw new FormatException("Non-canonical RLP integer (leading zero bytes).");
        return new BigInteger(span, isUnsigned: true, isBigEndian: true);
    }
    public bool Bool()
    {
        var span = Span;
        if (span.Length == 0) return false;
        if (span is [0x01]) return true;
        throw new FormatException("Invalid RLP boolean.");
    }
    public string Utf8() => Encoding.UTF8.GetString(Span);

    private ulong Integer(int maxBytes)
    {
        var span = Span;
        if (span.Length > maxBytes) throw new FormatException($"RLP integer larger than {maxBytes} bytes.");
        if (span.Length > 0 && span[0] == 0) throw new FormatException("Non-canonical RLP integer (leading zero bytes).");
        ulong value = 0;
        foreach (var b in span) value = (value << 8) | b;
        return value;
    }
}
