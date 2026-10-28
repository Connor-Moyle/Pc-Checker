using System.Globalization;

namespace PcChecker.Core.Crashes;

/// <summary>Formatting and parsing of stop codes, parameters and exception codes, independent of the Windows language.</summary>
public static class BugCheckFormat
{
    /// <summary>0x116 → "0x00000116", the way Windows and Microsoft's docs write a stop code.</summary>
    public static string Code(uint code) => "0x" + code.ToString("X8", CultureInfo.InvariantCulture);

    /// <summary>A stop-code parameter as 16 hex digits: "0xFFFFE00123456010".</summary>
    public static string Parameter(ulong value) => "0x" + value.ToString("X16", CultureInfo.InvariantCulture);

    /// <summary>"0x00000116 (0x…, 0x…, 0x…, 0x…)" for the details pane.</summary>
    public static string WithParameters(uint code, IReadOnlyList<ulong> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return parameters.Count == 0 ? Code(code) : $"{Code(code)} ({string.Join(", ", parameters.Select(Parameter))})";
    }

    /// <summary>An exception code as Windows Error Reporting writes it: "0xc0000005".</summary>
    public static string Exception(uint code) => "0x" + code.ToString("x8", CultureInfo.InvariantCulture);

    /// <summary>"0x1A", "1a", "0x0000001a" (hex) or a plain decimal when <paramref name="decimalWithoutPrefix"/>.</summary>
    public static ulong? ParseNumber(string? text, bool decimalWithoutPrefix = false)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var v = text.Trim();
        if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return ulong.TryParse(v.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hex) ? hex : null;
        }

        if (decimalWithoutPrefix)
        {
            return ulong.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var dec) ? dec : null;
        }

        return ulong.TryParse(v, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var h) ? h : null;
    }

    /// <summary>
    /// The BugCheck event's first value, "0x00000116 (0xffffe00123456010, 0xfffff80123456789, 0x0000000000000000,
    /// 0x000000000000000d)", into the code and its four parameters. Null when it does not start with a hex code.
    /// </summary>
    public static (uint Code, IReadOnlyList<ulong> Parameters)? ParseBugCheckLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var v = text.Trim();
        int open = v.IndexOf('(');
        var head = (open >= 0 ? v[..open] : v).Trim();
        int space = head.IndexOf(' ');
        if (space > 0)
        {
            head = head[..space];
        }

        if (!head.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || ParseNumber(head) is not { } code)
        {
            return null;
        }

        var parameters = new List<ulong>();
        if (open >= 0)
        {
            int close = v.IndexOf(')', open + 1);
            var inner = close > open ? v[(open + 1)..close] : v[(open + 1)..];
            foreach (var part in inner.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (ParseNumber(part) is not { } p)
                {
                    break;
                }

                parameters.Add(p);
            }
        }

        return ((uint)(code & 0xFFFFFFFF), parameters);
    }
}
