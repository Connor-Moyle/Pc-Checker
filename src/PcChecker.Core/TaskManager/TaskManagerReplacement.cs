namespace PcChecker.Core.TaskManager;

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
              + "download or user folder could let another program take Task Manager's place, so it stays off.";

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

    private static string Normalize(string path) => path.Trim().Trim('"').Replace('/', '\\').TrimEnd('\\');
}
