using System.Globalization;
using System.Text;

namespace PcChecker.Core.AppHistory;

/// <summary>
/// Decodes SRUM's <c>SruDbIdMapTable</c>. Every usage row names its app and user by a number (<c>AppId</c>,
/// <c>UserId</c>); this table maps the number (<c>IdIndex</c>) to a blob (<c>IdBlob</c>) whose meaning depends on
/// <c>IdType</c>: 0 is a program (a UTF-16 path such as <c>\device\harddiskvolume3\...\chrome.exe</c>), 1 and 2 are
/// Store apps and Windows services (UTF-16 names), and 3 is a user account stored as a binary SID. Windows versions
/// differ a little, so a blob of type 1 or 3 is read as a SID only when it really has a SID's shape, and as text
/// otherwise. Pure, so it is tested on any OS.
/// </summary>
public static class SrumIdMapDecoder
{
    /// <summary>Decodes one row; null when the blob is empty.</summary>
    public static SrumIdEntry? Decode(int index, long? idType, byte[]? blob)
    {
        if (blob is null || blob.Length == 0)
        {
            return null;
        }

        if (idType is 1 or 3 && TryDecodeSid(blob, out var sid))
        {
            return new SrumIdEntry(index, SrumIdKind.User, sid);
        }

        var text = DecodeUtf16(blob);
        if (text.Length == 0)
        {
            return null;
        }

        var kind = idType switch
        {
            0 => SrumIdKind.Program,
            1 or 2 => LooksLikePath(text) ? SrumIdKind.Program : SrumIdKind.PackageOrService,
            3 => SrumIdKind.Unknown,
            _ => LooksLikePath(text) ? SrumIdKind.Program : SrumIdKind.Unknown,
        };
        return new SrumIdEntry(index, kind, text);
    }

    /// <summary>A little-endian UTF-16 string without the trailing NULs Windows stores with it.</summary>
    public static string DecodeUtf16(byte[] blob)
    {
        ArgumentNullException.ThrowIfNull(blob);
        int length = blob.Length - (blob.Length % 2);
        var text = Encoding.Unicode.GetString(blob, 0, length);
        int end = text.IndexOf('\0');
        return (end >= 0 ? text[..end] : text).Trim();
    }

    /// <summary>
    /// Reads a binary SID (revision 1, a sub-authority count, a 48-bit big-endian authority and that many 32-bit
    /// little-endian sub-authorities) as its text form, for example <c>S-1-5-21-1004336348-1177238915-682003330-1001</c>.
    /// </summary>
    public static bool TryDecodeSid(byte[] blob, out string sid)
    {
        sid = string.Empty;
        if (blob is null || blob.Length < 8 || blob[0] != 1)
        {
            return false;
        }

        int count = blob[1];
        if (count > 15 || blob.Length != 8 + 4 * count)
        {
            return false;
        }

        ulong authority = 0;
        for (int i = 2; i < 8; i++)
        {
            authority = (authority << 8) | blob[i];
        }

        var text = new StringBuilder("S-1-");
        text.Append(authority.ToString(CultureInfo.InvariantCulture));
        for (int i = 0; i < count; i++)
        {
            uint sub = BitConverter.ToUInt32(blob, 8 + 4 * i);
            if (!BitConverter.IsLittleEndian)
            {
                sub = System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(sub);
            }

            text.Append('-').Append(sub.ToString(CultureInfo.InvariantCulture));
        }

        sid = text.ToString();
        return true;
    }

    /// <summary>Builds the lookup from <c>IdIndex</c> to entry; later duplicates replace earlier ones.</summary>
    public static IReadOnlyDictionary<int, SrumIdEntry> ToLookup(IEnumerable<SrumIdEntry?> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var map = new Dictionary<int, SrumIdEntry>();
        foreach (var entry in entries)
        {
            if (entry is not null)
            {
                map[entry.Index] = entry;
            }
        }

        return map;
    }

    private static bool LooksLikePath(string text) => text.Contains('\\', StringComparison.Ordinal);
}
