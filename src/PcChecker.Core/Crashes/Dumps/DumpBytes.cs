using System.Buffers.Binary;
using System.Text;

namespace PcChecker.Core.Crashes.Dumps;

/// <summary>
/// Bounds-checked little-endian reads over a dump file's bytes. Every read returns null instead of throwing when it
/// would run past the end, so a truncated or damaged file can never crash the parser.
/// </summary>
internal readonly ref struct DumpBytes
{
    private readonly ReadOnlySpan<byte> _data;

    public DumpBytes(ReadOnlySpan<byte> data) => _data = data;

    public int Length => _data.Length;

    public bool Has(long offset, long count) => offset >= 0 && count >= 0 && offset <= _data.Length && count <= _data.Length - offset;

    public ushort? U16(long offset) => Has(offset, 2) ? BinaryPrimitives.ReadUInt16LittleEndian(_data.Slice((int)offset, 2)) : null;

    public uint? U32(long offset) => Has(offset, 4) ? BinaryPrimitives.ReadUInt32LittleEndian(_data.Slice((int)offset, 4)) : null;

    public ulong? U64(long offset) => Has(offset, 8) ? BinaryPrimitives.ReadUInt64LittleEndian(_data.Slice((int)offset, 8)) : null;

    public bool Matches(long offset, string ascii) =>
        Has(offset, ascii.Length) && Encoding.ASCII.GetString(_data.Slice((int)offset, ascii.Length)) == ascii;

    /// <summary>UTF-16 text of <paramref name="chars"/> characters, cut at the first NUL; null when out of range or not printable.</summary>
    public string? Utf16(long offset, long chars)
    {
        if (chars <= 0 || chars > 1024 || !Has(offset, chars * 2))
        {
            return null;
        }

        var text = Encoding.Unicode.GetString(_data.Slice((int)offset, (int)(chars * 2)));
        int nul = text.IndexOf('\0');
        if (nul >= 0)
        {
            text = text[..nul];
        }

        return text.Length > 0 && text.All(c => !char.IsControl(c)) ? text : null;
    }

    /// <summary>A Windows FILETIME (100 ns ticks since 1601, UTC) as local time; null when zero or not a plausible date.</summary>
    public static DateTimeOffset? FileTime(ulong? value)
    {
        if (value is not { } ft || ft == 0)
        {
            return null;
        }

        // 2000-01-01 .. 2100-01-01 in FILETIME ticks.
        if (ft < 125_911_584_000_000_000UL || ft > 157_469_184_000_000_000UL)
        {
            return null;
        }

        return DateTimeOffset.FromFileTime((long)ft);
    }
}
