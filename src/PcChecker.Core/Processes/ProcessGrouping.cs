namespace PcChecker.Core.Processes;

/// <summary>
/// Task Manager's Processes tab in pure .NET: sorts every process into Apps, Background processes or Windows
/// processes and puts an app's helpers under it, so "Steam (5)" holds Steam and the web helpers it started.
/// </summary>
public static class ProcessGrouping
{
    /// <summary>Section order, as Task Manager shows them.</summary>
    public static IReadOnlyList<ProcessCategory> SectionOrder { get; } = [ProcessCategory.App, ProcessCategory.Background, ProcessCategory.Windows];

    /// <summary>
    /// Apps have a window on the taskbar. Windows processes are Windows' own programs: the kernel's processes, and
    /// known Windows programs or Microsoft-signed programs that run from the Windows folder (drivers' helpers in
    /// DriverStore count as background processes, as in Task Manager). Everything else runs in the background.
    /// </summary>
    public static ProcessCategory Classify(ProcessInfo process, string windowsDirectory)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (process.HasWindow)
        {
            return ProcessCategory.App;
        }

        return IsWindowsProcess(process, windowsDirectory) ? ProcessCategory.Windows : ProcessCategory.Background;
    }

    /// <summary>True for Windows' own processes (see <see cref="Classify"/>).</summary>
    public static bool IsWindowsProcess(ProcessInfo process, string windowsDirectory)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (process.Pid <= CriticalProcessRules.SystemPid)
        {
            return true;
        }

        var entry = ProcessCatalog.Lookup(process.Name);
        string? path = process.Path;
        if (!ProcessSuspicionRules.IsFullPath(path))
        {
            // Kernel processes (Registry, Memory Compression, Secure System) report a bare name.
            return entry?.RunsFromWindowsFolder == true || CriticalProcessRules.IsCritical(process.Name, process.Pid, ownPid: -1);
        }

        if (!ProcessSuspicionRules.IsUnder(path, windowsDirectory) || IsDriverStore(path, windowsDirectory))
        {
            return false;
        }

        if (entry is { RunsFromWindowsFolder: true } || entry?.Category is ProcessCatalog.Windows or ProcessCatalog.Security)
        {
            return true;
        }

        return process.Signature.IsSigned && (process.Signature.Publisher?.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) ?? false);
    }

    /// <summary>
    /// The groups of the Processes view. Every program with a window becomes an app; the processes of the same file and
    /// the background helpers it started (directly or through other helpers) join it. Programs from the Windows folder
    /// (File Explorer, Command Prompt) do not adopt the programs they start, or every app opened from the desktop would
    /// end up under Windows Explorer. The rest is grouped by program file within its own category.
    /// </summary>
    public static IReadOnlyList<ProcessGroup> Build(IReadOnlyCollection<ProcessInfo> processes, string windowsDirectory)
    {
        ArgumentNullException.ThrowIfNull(processes);
        var keyOf = processes.ToDictionary(p => p.Key, ProcessSorting.GroupKey, StringComparer.Ordinal);
        var appKeys = new HashSet<string>(processes.Where(p => p.HasWindow).Select(p => keyOf[p.Key]), StringComparer.OrdinalIgnoreCase);
        var adopting = new HashSet<string>(processes
            .Where(p => appKeys.Contains(keyOf[p.Key]) && !RunsFromWindows(p, windowsDirectory))
            .Select(p => keyOf[p.Key]), StringComparer.OrdinalIgnoreCase);

        var byPid = processes.GroupBy(p => p.Pid).ToDictionary(g => g.Key, g => g.First());
        var appMembers = appKeys.ToDictionary(k => k, _ => new List<ProcessInfo>(), StringComparer.OrdinalIgnoreCase);
        var rest = new List<ProcessInfo>();
        foreach (var process in processes)
        {
            string key = keyOf[process.Key];
            if (appMembers.TryGetValue(key, out var own))
            {
                own.Add(process);
            }
            else if (!IsWindowsProcess(process, windowsDirectory) && AdoptingAncestor(process, byPid, keyOf, adopting) is { } parentKey)
            {
                appMembers[parentKey].Add(process);
            }
            else
            {
                rest.Add(process);
            }
        }

        var groups = appMembers.Select(kv =>
        {
            var lead = kv.Value.OrderByDescending(m => m.HasWindow).ThenBy(m => m.StartTime ?? DateTimeOffset.MaxValue).First();
            return new ProcessGroup(kv.Key, ProcessSorting.TitleOf(lead), kv.Value) { Category = ProcessCategory.App };
        }).ToList();

        groups.AddRange(rest
            .GroupBy(p => (Key: keyOf[p.Key], Category: Classify(p, windowsDirectory)))
            .Select(g =>
            {
                var members = g.ToList();
                return new ProcessGroup(g.Key.Key, ProcessSorting.TitleOf(members[0]), members) { Category = g.Key.Category };
            }));
        return groups;
    }

    /// <summary>
    /// The lines of the Processes view: a heading per category ("Apps (6)"), then its groups sorted by
    /// <paramref name="column"/> (a group with one process is a plain line), with the members of expanded groups.
    /// Empty categories are left out.
    /// </summary>
    public static IReadOnlyList<ProcessListEntry> Arrange(IReadOnlyCollection<ProcessInfo> processes, string windowsDirectory, ProcessSortColumn column,
        bool descending, IReadOnlySet<string>? expanded = null, Func<ProcessInfo, double>? trend = null)
    {
        ArgumentNullException.ThrowIfNull(processes);
        var groups = Build(processes, windowsDirectory);
        var lines = new List<ProcessListEntry>();
        foreach (var category in SectionOrder)
        {
            var inSection = groups.Where(g => g.Category == category).ToList();
            if (inSection.Count == 0)
            {
                continue;
            }

            lines.Add(new ProcessListEntry(null, null, false) { Section = category, SectionCount = inSection.Count, Category = category });
            ProcessSorting.AddGroups(lines, inSection, column, descending, expanded, category, trend);
        }

        return lines;
    }

    /// <summary>Plain heading: "Apps", "Background processes", "Windows processes".</summary>
    public static string SectionTitle(ProcessCategory category) => category switch
    {
        ProcessCategory.App => "Apps",
        ProcessCategory.Windows => "Windows processes",
        _ => "Background processes",
    };

    private static string? AdoptingAncestor(ProcessInfo process, IReadOnlyDictionary<int, ProcessInfo> byPid,
        IReadOnlyDictionary<string, string> keyOf, IReadOnlySet<string> adopting)
    {
        var current = process;
        var seen = new HashSet<int> { process.Pid };
        for (int depth = 0; depth < 16; depth++)
        {
            if (!byPid.TryGetValue(current.ParentPid, out var parent) || !seen.Add(parent.Pid) || !StartedBefore(parent, current))
            {
                return null;
            }

            string parentKey = keyOf[parent.Key];
            if (adopting.Contains(parentKey))
            {
                return parentKey;
            }

            current = parent;
        }

        return null;
    }

    // Windows reuses process ids: a "parent" that started after its child is an unrelated program.
    private static bool StartedBefore(ProcessInfo parent, ProcessInfo child) =>
        parent.StartTime is { } p && child.StartTime is { } c && p <= c;

    private static bool RunsFromWindows(ProcessInfo process, string windowsDirectory) =>
        !ProcessSuspicionRules.IsFullPath(process.Path) || ProcessSuspicionRules.IsUnder(process.Path, windowsDirectory);

    private static bool IsDriverStore(string path, string windowsDirectory) =>
        ProcessSuspicionRules.IsUnder(path, System.IO.Path.Combine(windowsDirectory, "System32", "DriverStore"));
}
