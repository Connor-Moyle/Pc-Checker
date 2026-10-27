using System.Text;

namespace PcChecker.Core.Hosts;

/// <summary>
/// Detects how the hosts file is encoded and reads and writes it the same way: UTF-8 with or without a byte order
/// mark, UTF-16, or the Windows ANSI code page when the bytes are not valid UTF-8.
/// </summary>
public static class HostsEncodingDetector
{
    /// <summary>Windows-1252, used when the PC's ANSI code page is unknown (for example in tests).</summary>
    public const int DefaultAnsiCodePage = 1252;

    static HostsEncodingDetector() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static HostsEncoding Detect(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return HostsEncoding.Utf8Bom;
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return HostsEncoding.Utf16LittleEndian;
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return HostsEncoding.Utf16BigEndian;
        }

        try
        {
            _ = StrictUtf8.GetCharCount(bytes);
            return HostsEncoding.Utf8;
        }
        catch (DecoderFallbackException)
        {
            return HostsEncoding.Ansi;
        }
    }

    /// <summary>Decodes without the byte order mark.</summary>
    public static string Decode(byte[] bytes, HostsEncoding encoding, int ansiCodePage = DefaultAnsiCodePage)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        int skip = encoding switch
        {
            HostsEncoding.Utf8Bom when bytes.Length >= 3 => 3,
            HostsEncoding.Utf16LittleEndian or HostsEncoding.Utf16BigEndian when bytes.Length >= 2 => 2,
            _ => 0,
        };
        return EncodingFor(encoding, ansiCodePage).GetString(bytes, skip, bytes.Length - skip);
    }

    /// <summary>Encodes with the byte order mark the file had.</summary>
    public static byte[] Encode(string text, HostsEncoding encoding, int ansiCodePage = DefaultAnsiCodePage)
    {
        ArgumentNullException.ThrowIfNull(text);
        var body = EncodingFor(encoding, ansiCodePage).GetBytes(text);
        byte[] bom = encoding switch
        {
            HostsEncoding.Utf8Bom => [0xEF, 0xBB, 0xBF],
            HostsEncoding.Utf16LittleEndian => [0xFF, 0xFE],
            HostsEncoding.Utf16BigEndian => [0xFE, 0xFF],
            _ => [],
        };
        return [.. bom, .. body];
    }

    /// <summary>
    /// The encoding to save with: the file's own, unless the text has characters the ANSI code page cannot hold, in
    /// which case UTF-8 (which Windows also reads) keeps them.
    /// </summary>
    public static HostsEncoding ForSaving(string text, HostsEncoding current, int ansiCodePage = DefaultAnsiCodePage)
    {
        if (current != HostsEncoding.Ansi)
        {
            return current;
        }

        try
        {
            var strict = Encoding.GetEncoding(ansiCodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            _ = strict.GetByteCount(text);
            return HostsEncoding.Ansi;
        }
        catch (EncoderFallbackException)
        {
            return HostsEncoding.Utf8;
        }
    }

    /// <summary>A short name for the page ("UTF-8", "UTF-8 with BOM", "ANSI").</summary>
    public static string Describe(HostsEncoding encoding) => encoding switch
    {
        HostsEncoding.Utf8 => "UTF-8",
        HostsEncoding.Utf8Bom => "UTF-8 with BOM",
        HostsEncoding.Ansi => "ANSI",
        HostsEncoding.Utf16LittleEndian => "UTF-16",
        _ => "UTF-16 big-endian",
    };

    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    private static Encoding EncodingFor(HostsEncoding encoding, int ansiCodePage) => encoding switch
    {
        HostsEncoding.Utf8 or HostsEncoding.Utf8Bom => new UTF8Encoding(false),
        HostsEncoding.Utf16LittleEndian => new UnicodeEncoding(false, false),
        HostsEncoding.Utf16BigEndian => new UnicodeEncoding(true, false),
        _ => Encoding.GetEncoding(ansiCodePage),
    };
}
