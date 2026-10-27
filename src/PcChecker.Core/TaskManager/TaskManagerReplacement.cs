namespace PcChecker.Core.TaskManager;

/// <summary>What the Task Manager Debugger value currently is, from PC Checker's point of view.</summary>
public enum DebuggerValueKind
{
    /// <summary>No value: Windows Task Manager opens.</summary>
    None,

    /// <summary>Points at this PcChecker.exe with <c>--taskmgr</c>.</summary>
    Ours,

    /// <summary>Points at a different PC Checker copy that still exists (an old install folder, another spelling).</summary>
    OtherCopy,

    /// <summary>Carries <c>--taskmgr</c> but its file no longer exists: a leftover that may be cleaned up.</summary>
    Stale,

    /// <summary>Another program (Process Explorer, System Informer, ...) is set in Task Manager's place.</summary>
    ThirdParty,
}

/// <summary>
/// Pure logic for the "open PC Checker instead of Windows Task Manager" option. Like Sysinternals Process Explorer,
/// it works through the Image File Execution Options "Debugger" value for <c>taskmgr.exe</c>: when Windows launches
/// Task Manager it instead launches the debugger and passes taskmgr's path as an extra argument (which PC Checker
/// ignores). This class only builds, reads and checks that value; it never touches the registry, so it is unit-tested.
/// </summary>
public static class TaskManagerReplacement
{
    /// <summary>The Image File Execution Options key for Task Manager (under HKLM).</summary>
    public const string IfeoKeyPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\taskmgr.exe";

    /// <summary>The value name Windows reads as the stand-in debugger.</summary>
    public const string DebuggerValueName = "Debugger";

    /// <summary>The argument PC Checker passes to itself so it knows it was launched in place of Task Manager.</summary>
    public const string TaskManagerSwitch = "--taskmgr";

    /// <summary>
    /// Builds the Debugger value for a given PC Checker path: the quoted executable followed by <c>--taskmgr</c>.
    /// </summary>
    public static string BuildDebuggerValue(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            throw new ArgumentException("A path to PcChecker.exe is required.", nameof(exePath));
        }

        return $"\"{exePath.Trim().Trim('"')}\" {TaskManagerSwitch}";
    }

    /// <summary>
    /// Reads the executable path and whether <c>--taskmgr</c> is present out of a Debugger value. Returns null when
    /// the value is empty. Handles a quoted or unquoted path.
    /// </summary>
    public static (string ExePath, bool HasSwitch)? ParseDebuggerValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        string path;
        string rest;
        if (text.StartsWith('"'))
        {
            int close = text.IndexOf('"', 1);
            if (close < 0)
            {
                path = text.Trim('"');
                rest = string.Empty;
            }
            else
            {
                path = text[1..close];
                rest = text[(close + 1)..];
            }
        }
        else
        {
            int space = text.IndexOf(' ');
            if (space < 0)
            {
                path = text;
                rest = string.Empty;
            }
            else
            {
                path = text[..space];
                rest = text[space..];
            }
        }

        bool hasSwitch = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(a => a.Equals(TaskManagerSwitch, StringComparison.OrdinalIgnoreCase));
        return (path, hasSwitch);
    }

    /// <summary>
    /// True when the Debugger value is one PC Checker owns: it points at <paramref name="exePath"/> and carries the
    /// <c>--taskmgr</c> switch. Used before turning the option off, so a third party's debugger is never removed.
    /// </summary>
    public static bool OwnsValue(string? value, string exePath)
    {
        if (ParseDebuggerValue(value) is not { } parsed || string.IsNullOrWhiteSpace(exePath))
        {
            return false;
        }

        return parsed.HasSwitch && PathsEqual(parsed.ExePath, exePath);
    }

    /// <summary>
    /// True when the Debugger value points at a copy of PC Checker (carries <c>--taskmgr</c>) but <em>not</em> the one
    /// running now, or at a file that no longer exists — a stale value this option left behind and may clean up.
    /// </summary>
    public static bool IsStaleOwnedValue(string? value, string exePath, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        if (ParseDebuggerValue(value) is not { } parsed || !parsed.HasSwitch)
        {
            return false;
        }

        // Points at this exe but the file is gone (uninstalled), or points at a different PcChecker.exe that is gone.
        if (PathsEqual(parsed.ExePath, exePath))
        {
            return !fileExists(parsed.ExePath);
        }

        return IsPcCheckerExe(parsed.ExePath) && !fileExists(parsed.ExePath);
    }

    /// <summary>
    /// Sorts a Debugger value into <see cref="DebuggerValueKind"/>. A value with <c>--taskmgr</c> is treated as a PC
    /// Checker value (only this option writes that switch), so a moved or reinstalled copy can be repointed.
    /// </summary>
    public static DebuggerValueKind Classify(string? value, string exePath, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        if (ParseDebuggerValue(value) is not { } parsed)
        {
            return DebuggerValueKind.None;
        }

        if (!parsed.HasSwitch)
        {
            return DebuggerValueKind.ThirdParty;
        }

        if (!fileExists(parsed.ExePath))
        {
            return DebuggerValueKind.Stale;
        }

        return PathsEqual(parsed.ExePath, exePath) ? DebuggerValueKind.Ours : DebuggerValueKind.OtherCopy;
    }

    /// <summary>
    /// True when a saved third-party Debugger value (Process Explorer, ...) may be put back when the option is turned
    /// off: its program still exists. A value whose program was removed would leave Task Manager unable to open, so it
    /// is dropped instead. A bare file name (found through the search path) cannot be checked and is kept. An unquoted
    /// path with spaces is tried the way Windows reads it, one space at a time.
    /// </summary>
    public static bool ShouldRestoreBackup(string? backup, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        if (ParseDebuggerValue(backup) is not { } parsed)
        {
            return false;
        }

        var candidates = new List<string> { parsed.ExePath };
        var text = backup!.Trim();
        if (!text.StartsWith('"'))
        {
            for (int space = text.IndexOf(' '); space >= 0; space = text.IndexOf(' ', space + 1))
            {
                candidates.Add(text[..space]);
            }

            candidates.Add(text);
        }

        foreach (var candidate in candidates)
        {
            var path = candidate.Trim();
            if (path.Length == 0)
            {
                continue;
            }

            if (path.IndexOfAny(['\\', '/', ':']) < 0 || path.Contains('%'))
            {
                return true;
            }

            if (fileExists(path))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when PC Checker runs from a location that is not writable by a standard user, which is the only place the
    /// option may be turned on: an installer under Program Files. A user-writable location would let someone drop a
    /// different exe there and have Windows run it elevated in place of Task Manager.
    /// </summary>
    public static bool IsInstalledLocation(string exePath, IEnumerable<string> programFilesRoots)
    {
        ArgumentNullException.ThrowIfNull(programFilesRoots);
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return false;
        }

        var full = Normalize(exePath);
        foreach (var root in programFilesRoots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            var normalizedRoot = Normalize(root).TrimEnd('\\') + "\\";
            if (full.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The plain reason the option cannot be turned on from this location, or null when it can.</summary>
    public static string? IneligibleReason(string exePath, IEnumerable<string> programFilesRoots) =>
        IsInstalledLocation(exePath, programFilesRoots)
            ? null
            : "This option is only available when PC Checker is installed in Program Files. Running it from a "
              + "download or user folder could let another program take Task Manager's place, so it stays off. "
              + "Use \"Install in Program Files\" above to put PC Checker there.";

    private static bool IsPcCheckerExe(string path)
    {
        // Split on both separators so the check is the same on Windows and on the Linux test runner.
        var name = Normalize(path);
        int slash = name.LastIndexOf('\\');
        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }

        return name.Equals("PcChecker.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string a, string b) =>
        Normalize(a).Equals(Normalize(b), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path) => CollapseDotSegments(path.Trim().Trim('"').Replace('/', '\\')).TrimEnd('\\');

    /// <summary>
    /// Resolves <c>.</c> and <c>..</c> segments in a Windows path without touching the disk (so it behaves the same on
    /// the Linux test runner), so <c>C:\Program Files\..\Users\x</c> is not mistaken for a Program Files path. A
    /// <c>..</c> that would climb above the drive root is dropped, as Windows does.
    /// </summary>
    public static string CollapseDotSegments(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        var text = path.Replace('/', '\\');
        string prefix = string.Empty;
        if (text.StartsWith(@"\\", StringComparison.Ordinal))
        {
            prefix = @"\\";
            text = text[2..];
        }
        else if (text.Length >= 2 && text[1] == ':')
        {
            prefix = text[..2] + (text.Length > 2 && text[2] == '\\' ? "\\" : string.Empty);
            text = text[prefix.Length..];
        }
        else if (text.StartsWith('\\'))
        {
            prefix = "\\";
            text = text[1..];
        }

        var parts = new List<string>();
        foreach (var part in text.Split('\\'))
        {
            if (part.Length == 0 || part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }

                continue;
            }

            parts.Add(part);
        }

        return prefix + string.Join('\\', parts);
    }
}
