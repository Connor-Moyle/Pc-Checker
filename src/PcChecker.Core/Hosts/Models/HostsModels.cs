namespace PcChecker.Core.Hosts;

/// <summary>What a line of the hosts file holds.</summary>
public enum HostsLineKind
{
    Blank,
    Comment,

    /// <summary>An address and host names; <see cref="HostsLine.Enabled"/> is false when the line is commented out.</summary>
    Entry,

    /// <summary>Text Windows cannot use (for example a host name without an address). Kept as it is.</summary>
    Invalid,
}

/// <summary>
/// One line of the hosts file. <see cref="Raw"/> is the original text and is written back unchanged; a line created or
/// edited on the page has no raw text and is written from its fields.
/// </summary>
public sealed record HostsLine
{
    public required HostsLineKind Kind { get; init; }

    /// <summary>The line exactly as read (no line break), or null for a new or edited line.</summary>
    public string? Raw { get; init; }

    public bool Enabled { get; init; } = true;

    public string Address { get; init; } = string.Empty;

    public IReadOnlyList<string> HostNames { get; init; } = [];

    /// <summary>The text after '#' on an entry line, trimmed; null when there is none.</summary>
    public string? Comment { get; init; }

    /// <summary>A new or edited entry, written as "address⇥names # comment" (with "# " in front when turned off).</summary>
    public static HostsLine CreateEntry(string address, IEnumerable<string> hostNames, string? comment, bool enabled) => new()
    {
        Kind = HostsLineKind.Entry,
        Address = address.Trim(),
        HostNames = hostNames.Select(h => h.Trim()).Where(h => h.Length > 0).ToList(),
        Comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim(),
        Enabled = enabled,
    };
}

/// <summary>The parsed hosts file with the line-break style needed to write it back the same way.</summary>
public sealed record HostsDocument(IReadOnlyList<HostsLine> Lines, string NewLine, bool EndsWithNewLine)
{
    public static HostsDocument Empty { get; } = new([], "\r\n", true);

    public IEnumerable<HostsLine> Entries => Lines.Where(l => l.Kind == HostsLineKind.Entry);
}

/// <summary>How the hosts file is encoded; saving keeps it.</summary>
public enum HostsEncoding
{
    /// <summary>UTF-8 without a byte order mark (plain ASCII files count as this).</summary>
    Utf8,
    Utf8Bom,

    /// <summary>The Windows ANSI code page (for example Windows-1252).</summary>
    Ansi,
    Utf16LittleEndian,
    Utf16BigEndian,
}

/// <summary>Why an entry looks unwanted.</summary>
public enum HostsConcern
{
    None,

    /// <summary>A well-known or bank-like site is sent to another address.</summary>
    RedirectsKnownSite,

    /// <summary>Windows Update, Microsoft Defender or a security vendor's update site is blocked.</summary>
    BlocksSecurityUpdates,
}

/// <summary>A "possibly unwanted" flag with a one-line reason.</summary>
public sealed record HostsFlag(HostsConcern Concern, string Reason);

/// <summary>Facts about the hosts file itself.</summary>
public sealed record HostsFileFacts(bool Exists, long Size, bool ReadOnly, bool Hidden, bool System, string Path, bool UnusualFolder);

/// <summary>The hosts file as loaded, with what is needed to save it back safely.</summary>
public sealed record HostsSnapshot(
    string Path,
    HostsDocument Document,
    string Text,
    HostsEncoding Encoding,
    HostsFileFacts Facts,
    IReadOnlyList<string> Warnings,
    string ContentHash,
    DateTimeOffset Loaded);

/// <summary>A backup copy PC Checker made before saving.</summary>
public sealed record HostsBackup(string Path, DateTime Created, long Size);
