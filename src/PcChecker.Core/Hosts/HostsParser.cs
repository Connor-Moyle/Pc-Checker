using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PcChecker.Core.Hosts;

/// <summary>
/// Reads and writes the hosts file. Every line is kept: comments, blank lines and lines Windows cannot use are written
/// back exactly as they were, and only lines changed on the page are written from their fields. A commented-out line
/// that holds a valid address and host names is shown as a turned-off entry; Microsoft's sample lines stay comments.
/// </summary>
public static class HostsParser
{
    /// <summary>Host names in the examples of Microsoft's default file; those lines are comments, not entries.</summary>
    private static readonly HashSet<string> SampleHosts = new(StringComparer.OrdinalIgnoreCase) { "rhino.acme.com", "x.acme.com" };

    public static HostsDocument Parse(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return HostsDocument.Empty;
        }

        string newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        bool endsWithNewLine = text.EndsWith('\n') || text.EndsWith('\r');
        var raw = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n').ToList();
        if (endsWithNewLine && raw.Count > 0 && raw[^1].Length == 0)
        {
            raw.RemoveAt(raw.Count - 1);
        }

        return new HostsDocument(raw.Select(ParseLine).ToList(), newLine, endsWithNewLine);
    }

    public static HostsLine ParseLine(string line)
    {
        string trimmed = line.Trim();
        if (trimmed.Length == 0)
        {
            return new HostsLine { Kind = HostsLineKind.Blank, Raw = line };
        }

        if (trimmed.StartsWith('#'))
        {
            var disabled = TryParseEntry(trimmed.TrimStart('#'), strict: true);
            if (disabled is not null && !disabled.Value.Names.Any(SampleHosts.Contains))
            {
                return Entry(line, disabled.Value, enabled: false);
            }

            return new HostsLine { Kind = HostsLineKind.Comment, Raw = line };
        }

        var entry = TryParseEntry(trimmed, strict: false);
        return entry is null
            ? new HostsLine { Kind = HostsLineKind.Invalid, Raw = line }
            : Entry(line, entry.Value, enabled: true);
    }

    /// <summary>Writes the document back: unchanged lines as read, new and edited ones from their fields.</summary>
    public static string Serialize(HostsDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var sb = new StringBuilder();
        for (int i = 0; i < document.Lines.Count; i++)
        {
            sb.Append(Format(document.Lines[i]));
            if (i < document.Lines.Count - 1 || document.EndsWithNewLine)
            {
                sb.Append(document.NewLine);
            }
        }

        return sb.ToString();
    }

    /// <summary>The text of one line.</summary>
    public static string Format(HostsLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.Raw is not null)
        {
            return line.Raw;
        }

        if (line.Kind != HostsLineKind.Entry)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        if (!line.Enabled)
        {
            sb.Append("# ");
        }

        sb.Append(line.Address).Append('\t').Append(string.Join(' ', line.HostNames));
        if (!string.IsNullOrWhiteSpace(line.Comment))
        {
            sb.Append("\t# ").Append(line.Comment.Trim());
        }

        return sb.ToString();
    }

    /// <summary>
    /// True for an IPv4 address in dotted form (four parts) or an IPv6 address. "1" and "1.2" parse as IPv4 in .NET but
    /// Windows does not accept them in the hosts file, so they are refused.
    /// </summary>
    public static bool IsValidAddress(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string value = text.Trim();
        if (!IPAddress.TryParse(value, out var address))
        {
            return false;
        }

        return address.AddressFamily == AddressFamily.InterNetworkV6
            ? value.Contains(':')
            : value.Count(c => c == '.') == 3 && value.All(c => char.IsAsciiDigit(c) || c == '.');
    }

    /// <summary>A host name Windows can use: letters, digits, '-' and '_', labels of 1–63 characters, no wildcards.</summary>
    public static bool IsValidHostName(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string name = text.Trim().TrimEnd('.');
        if (name.Length is 0 or > 253)
        {
            return false;
        }

        foreach (var label in name.Split('.'))
        {
            if (label.Length is 0 or > 63 || label.StartsWith('-') || label.EndsWith('-'))
            {
                return false;
            }

            if (!label.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Why a typed entry cannot be saved, or null when it is fine. Used by the add and edit box.
    /// </summary>
    public static string? Validate(string? address, string? hostNames)
    {
        if (!IsValidAddress(address))
        {
            return "Type an IP address such as 0.0.0.0, 127.0.0.1, 192.168.1.20 or ::1.";
        }

        var names = SplitNames(hostNames);
        if (names.Count == 0)
        {
            return "Type at least one host name, such as example.com.";
        }

        if (names.FirstOrDefault(n => n.Contains('*')) is { } wildcard)
        {
            return $"\"{wildcard}\": Windows does not support wildcards in the hosts file. Add each name on its own.";
        }

        if (names.FirstOrDefault(n => !IsValidHostName(n)) is { } bad)
        {
            return $"\"{bad}\" is not a valid host name. Use letters, digits, dots and dashes, without http:// or a path.";
        }

        return null;
    }

    /// <summary>Splits typed host names on spaces, commas and semicolons, and strips "http://" and paths people paste.</summary>
    public static IReadOnlyList<string> SplitNames(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return text.Split([' ', '\t', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(StripUrl)
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string StripUrl(string name)
    {
        string value = name;
        int scheme = value.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            value = value[(scheme + 3)..];
        }

        int slash = value.IndexOf('/');
        return (slash >= 0 ? value[..slash] : value).Trim().ToLowerInvariant();
    }

    private static HostsLine Entry(string raw, (string Address, List<string> Names, string? Comment) parsed, bool enabled) => new()
    {
        Kind = HostsLineKind.Entry,
        Raw = raw,
        Enabled = enabled,
        Address = parsed.Address,
        HostNames = parsed.Names,
        Comment = parsed.Comment,
    };

    /// <summary>
    /// "address name [name…] [# comment]". Strict (for commented-out lines) also needs every name to be valid, so
    /// prose such as "# For example:" stays a comment.
    /// </summary>
    private static (string Address, List<string> Names, string? Comment)? TryParseEntry(string text, bool strict)
    {
        string body = text;
        string? comment = null;
        int hash = body.IndexOf('#');
        if (hash >= 0)
        {
            comment = body[(hash + 1)..].Trim();
            body = body[..hash];
            if (comment.Length == 0)
            {
                comment = null;
            }
        }

        var tokens = body.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2 || !IsValidAddress(tokens[0]))
        {
            return null;
        }

        var names = tokens.Skip(1).ToList();
        if (strict && !names.All(IsValidHostName))
        {
            return null;
        }

        return (tokens[0], names, comment);
    }
}
