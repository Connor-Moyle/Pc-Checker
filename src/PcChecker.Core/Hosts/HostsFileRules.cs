using System.Globalization;
using PcChecker.Core.Common;

namespace PcChecker.Core.Hosts;

/// <summary>
/// Checks on the hosts file itself, Microsoft's default content, backup names and a change summary for the
/// confirmation dialog.
/// </summary>
public static class HostsFileRules
{
    /// <summary>A hosts file above this size is unusual and can slow down name lookups.</summary>
    public const long LargeFileBytes = 1024 * 1024;

    /// <summary>Microsoft's default hosts file for Windows 10 and 11, as given in its "reset the hosts file" article.</summary>
    public const string WindowsDefault =
        "# Copyright (c) 1993-2009 Microsoft Corp.\r\n" +
        "#\r\n" +
        "# This is a sample HOSTS file used by Microsoft TCP/IP for Windows.\r\n" +
        "#\r\n" +
        "# This file contains the mappings of IP addresses to host names. Each\r\n" +
        "# entry should be kept on an individual line. The IP address should\r\n" +
        "# be placed in the first column followed by the corresponding host name.\r\n" +
        "# The IP address and the host name should be separated by at least one\r\n" +
        "# space.\r\n" +
        "#\r\n" +
        "# Additionally, comments (such as these) may be inserted on individual\r\n" +
        "# lines or following the machine name denoted by a '#' symbol.\r\n" +
        "#\r\n" +
        "# For example:\r\n" +
        "#\r\n" +
        "#      102.54.94.97     rhino.acme.com          # source server\r\n" +
        "#       38.25.63.10     x.acme.com              # x client host\r\n" +
        "\r\n" +
        "# localhost name resolution is handled within DNS itself.\r\n" +
        "#\t127.0.0.1       localhost\r\n" +
        "#\t::1             localhost\r\n";

    /// <summary>The folder Windows reads the hosts file from unless the DataBasePath setting says otherwise.</summary>
    public static string DefaultFolder(string systemRoot) => Path.Combine(systemRoot, "System32", "drivers", "etc");

    /// <summary>
    /// True when the Tcpip DataBasePath setting is missing or points at the usual folder. Malware sometimes points it at
    /// a folder with its own hosts file.
    /// </summary>
    public static bool IsDefaultDatabasePath(string? value, string systemRoot)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        string expanded = value.Trim().Replace("%SystemRoot%", systemRoot, StringComparison.OrdinalIgnoreCase)
            .Replace("%windir%", systemRoot, StringComparison.OrdinalIgnoreCase);
        return Normalize(expanded).Equals(Normalize(DefaultFolder(systemRoot)), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>One line per unusual fact, for the warning bar.</summary>
    public static IReadOnlyList<string> Warnings(HostsFileFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var list = new List<string>();
        if (!facts.Exists)
        {
            list.Add("There is no hosts file. Windows works fine without one; saving here creates it.");
            return list;
        }

        if (facts.UnusualFolder)
        {
            list.Add($"Windows reads the hosts file from an unusual folder ({Path.GetDirectoryName(facts.Path)}). Malware does this; if you did not set it up, run a malware scan.");
        }

        if (facts.ReadOnly)
        {
            list.Add("The file is read-only. Security or ad-blocking programs often set this. PC Checker lifts it while saving and sets it again afterwards.");
        }

        if (facts.Hidden || facts.System)
        {
            list.Add("The file is marked hidden or system, which is unusual for the hosts file. Malware sometimes hides its changes this way.");
        }

        if (facts.Size > LargeFileBytes)
        {
            list.Add($"The file is very large ({ByteSize.Format(facts.Size)}). Huge block lists can slow down every website lookup; consider a smaller list.");
        }

        return list;
    }

    /// <summary>True when the text is Microsoft's default (line breaks and trailing spaces ignored).</summary>
    public static bool IsWindowsDefault(string? text) =>
        text is not null && Canonical(text) == Canonical(WindowsDefault);

    /// <summary>"hosts-2026-10-27_14-05-33.bak".</summary>
    public static string BackupFileName(DateTime local) =>
        "hosts-" + local.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture) + ".bak";

    /// <summary>The time in a backup file name, or null for other files.</summary>
    public static DateTime? ParseBackupFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || !fileName.StartsWith("hosts-", StringComparison.OrdinalIgnoreCase)
            || !fileName.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string stamp = fileName["hosts-".Length..^".bak".Length];
        int suffix = stamp.IndexOf(' ');
        if (suffix > 0)
        {
            stamp = stamp[..suffix];
        }

        return DateTime.TryParseExact(stamp, "yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var when) ? when : null;
    }

    /// <summary>Entries added, removed, and turned on or off between two versions, for the confirmation dialog.</summary>
    public static (int Added, int Removed, int Toggled) Compare(HostsDocument before, HostsDocument after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        static string Key(HostsLine l) => l.Address.ToLowerInvariant() + " " + string.Join(' ', l.HostNames.Select(h => h.ToLowerInvariant()));

        var oldEntries = before.Entries.GroupBy(Key).ToDictionary(g => g.Key, g => g.ToList());
        var newEntries = after.Entries.GroupBy(Key).ToDictionary(g => g.Key, g => g.ToList());
        int added = newEntries.Where(kv => !oldEntries.ContainsKey(kv.Key)).Sum(kv => kv.Value.Count);
        int removed = oldEntries.Where(kv => !newEntries.ContainsKey(kv.Key)).Sum(kv => kv.Value.Count);
        int toggled = newEntries.Where(kv => oldEntries.ContainsKey(kv.Key))
            .Count(kv => kv.Value.Any(l => l.Enabled) != oldEntries[kv.Key].Any(l => l.Enabled));
        return (added, removed, toggled);
    }

    private static string Canonical(string text) =>
        string.Join("\n", text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(l => l.TrimEnd())).Trim();

    private static string Normalize(string path) => path.Replace('/', '\\').TrimEnd('\\');
}
