using System.Globalization;
using System.Text;

namespace PcChecker.Core.RegistryHealth;

/// <summary>Registry value types a .reg file can carry.</summary>
public enum RegValueType
{
    None,
    String,
    ExpandString,
    MultiString,
    DWord,
    QWord,
    Binary,
}

/// <summary>One value to write. A null or empty <paramref name="Name"/> is the key's default value (<c>@</c>).</summary>
public sealed record RegExportValue(string? Name, RegValueType Type, object? Data);

/// <summary>One key section: <paramref name="FullKeyName"/> uses the regedit form (<c>HKEY_CURRENT_USER\…</c>).</summary>
public sealed record RegExportKey(string FullKeyName, IReadOnlyList<RegExportValue> Values);

/// <summary>
/// Builds and merges <c>.reg</c> files in the "Windows Registry Editor Version 5.00" format that <c>reg import</c> and
/// regedit read: strings are escaped, expandable and multi-strings are written as UTF-16 <c>hex(2)</c>/<c>hex(7)</c>,
/// QWORDs as <c>hex(b)</c>, long hex lines are wrapped like regedit does. Pure.
/// </summary>
public static class RegExportBuilder
{
    public const string Header = "Windows Registry Editor Version 5.00";
    private const string NewLine = "\r\n";
    private const int MaxLineLength = 80;

    /// <summary>A complete .reg document for the given keys.</summary>
    public static string Build(IEnumerable<RegExportKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var sb = new StringBuilder();
        sb.Append(Header).Append(NewLine);
        foreach (var key in keys)
        {
            sb.Append(NewLine).Append('[').Append(key.FullKeyName.Trim('\\')).Append(']').Append(NewLine);
            foreach (var value in key.Values)
            {
                sb.Append(FormatValue(value)).Append(NewLine);
            }
        }

        sb.Append(NewLine);
        return sb.ToString();
    }

    /// <summary>One <c>"Name"=data</c> line (possibly wrapped over several lines with a trailing backslash).</summary>
    public static string FormatValue(RegExportValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        string name = string.IsNullOrEmpty(value.Name) ? "@" : "\"" + Escape(value.Name) + "\"";
        string prefix = name + "=";

        switch (value.Type)
        {
            case RegValueType.String:
            {
                string text = value.Data as string ?? Convert.ToString(value.Data, CultureInfo.InvariantCulture) ?? string.Empty;
                // Line breaks and embedded nulls cannot be written as a quoted string; regedit uses hex(1) then.
                return text.IndexOfAny(['\r', '\n', '\0']) >= 0
                    ? Hex(prefix + "hex(1):", Utf16WithTerminator(text))
                    : prefix + "\"" + Escape(text) + "\"";
            }

            case RegValueType.ExpandString:
                return Hex(prefix + "hex(2):", Utf16WithTerminator(value.Data as string ?? string.Empty));

            case RegValueType.MultiString:
                return Hex(prefix + "hex(7):", MultiStringBytes(value.Data));

            case RegValueType.DWord:
                return prefix + "dword:" + ToUInt32(value.Data).ToString("x8", CultureInfo.InvariantCulture);

            case RegValueType.QWord:
            {
                // Registry QWORDs are little-endian whatever the machine.
                var bytes = new byte[8];
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes, ToUInt64(value.Data));
                return Hex(prefix + "hex(b):", bytes);
            }

            case RegValueType.Binary:
                return Hex(prefix + "hex:", value.Data as byte[] ?? []);

            default:
                return Hex(prefix + "hex(0):", value.Data as byte[] ?? []);
        }
    }

    /// <summary>Escapes backslashes and quotes for a quoted .reg string or value name.</summary>
    public static string Escape(string text) => (text ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>
    /// Joins several .reg documents (for example one <c>reg export</c> per key) into one: a single header, byte-order
    /// marks removed, sections separated by one blank line, CRLF line endings.
    /// </summary>
    public static string Merge(IEnumerable<string> regTexts)
    {
        ArgumentNullException.ThrowIfNull(regTexts);
        var bodies = new List<string>();
        foreach (var text in regTexts)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var lines = text.Replace("﻿", string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
                .Where(l => !l.Trim().Equals(Header, StringComparison.OrdinalIgnoreCase) && !l.Trim().Equals("REGEDIT4", StringComparison.OrdinalIgnoreCase))
                .ToList();

            while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[0]))
            {
                lines.RemoveAt(0);
            }

            while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
            {
                lines.RemoveAt(lines.Count - 1);
            }

            if (lines.Count > 0)
            {
                bodies.Add(string.Join(NewLine, lines));
            }
        }

        return Header + NewLine + NewLine + string.Join(NewLine + NewLine, bodies) + NewLine + NewLine;
    }

    /// <summary>The key names (<c>[…]</c> sections) in a .reg document, in order; deletions (<c>[-…]</c>) excluded.</summary>
    public static IReadOnlyList<string> KeyNames(string regText)
    {
        if (string.IsNullOrEmpty(regText))
        {
            return [];
        }

        return regText.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith('[') && l.EndsWith(']') && !l.StartsWith("[-", StringComparison.Ordinal))
            .Select(l => l[1..^1])
            .ToList();
    }

    /// <summary>
    /// True when a .reg document is an export of <paramref name="fullKeyName"/> (HKEY_CURRENT_USER\Software\Vendor):
    /// it holds that key, and nothing outside it. A backup of some other key must never stand in for the one removed.
    /// </summary>
    public static bool IsExportOf(string? regText, string fullKeyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullKeyName);
        var keys = KeyNames(regText ?? string.Empty);
        var target = fullKeyName.Trim().TrimEnd('\\');
        return keys.Any(k => k.Equals(target, StringComparison.OrdinalIgnoreCase)) &&
               keys.All(k => k.Equals(target, StringComparison.OrdinalIgnoreCase) || k.StartsWith(target + "\\", StringComparison.OrdinalIgnoreCase));
    }

    private static string Hex(string prefix, byte[] bytes)
    {
        var sb = new StringBuilder(prefix);
        int lineLength = prefix.Length;
        for (int i = 0; i < bytes.Length; i++)
        {
            string token = bytes[i].ToString("x2", CultureInfo.InvariantCulture) + (i < bytes.Length - 1 ? "," : string.Empty);

            // regedit wraps so no line (including the trailing backslash) is longer than 80 characters.
            if (lineLength + token.Length > MaxLineLength - 1 && lineLength > 2)
            {
                sb.Append('\\').Append(NewLine).Append("  ");
                lineLength = 2;
            }

            sb.Append(token);
            lineLength += token.Length;
        }

        return sb.ToString();
    }

    private static byte[] Utf16WithTerminator(string text) => Encoding.Unicode.GetBytes(text + "\0");

    private static byte[] MultiStringBytes(object? data)
    {
        IEnumerable<string> items = data switch
        {
            string[] array => array,
            IEnumerable<string> list => list,
            string single => [single],
            _ => [],
        };

        var sb = new StringBuilder();
        foreach (var item in items)
        {
            sb.Append(item).Append('\0');
        }

        sb.Append('\0');
        return Encoding.Unicode.GetBytes(sb.ToString());
    }

    private static uint ToUInt32(object? data) => data switch
    {
        int i => unchecked((uint)i),
        uint u => u,
        long l => unchecked((uint)l),
        ulong ul => unchecked((uint)ul),
        short s => unchecked((uint)s),
        byte b => b,
        string s when uint.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => 0,
    };

    private static ulong ToUInt64(object? data) => data switch
    {
        long l => unchecked((ulong)l),
        ulong ul => ul,
        int i => unchecked((ulong)(long)i),
        uint u => u,
        string s when ulong.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => 0,
    };
}
