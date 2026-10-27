namespace PcChecker.Core.Processes;

/// <summary>Columns the process list can be sorted by.</summary>
public enum ProcessSortColumn
{
    Name,
    Pid,
    Cpu,
    Memory,
    DiskRead,
    DiskWrite,
    Gpu,
    Publisher,
    User,
    StartTime,
    Path,
}

/// <summary>A set of processes of the same program (same file), with summed usage.</summary>
public sealed record ProcessGroup(string Key, string Title, IReadOnlyList<ProcessInfo> Members)
{
    public double CpuPercent { get; } = Members.Sum(m => m.CpuPercent);
    public long PrivateBytes { get; } = Members.Sum(m => m.PrivateBytes);
    public double ReadBytesPerSecond { get; } = Members.Sum(m => m.ReadBytesPerSecond);
    public double WriteBytesPerSecond { get; } = Members.Sum(m => m.WriteBytesPerSecond);
    public double? GpuPercent { get; } = Members.Any(m => m.GpuPercent is not null) ? Math.Min(100, Members.Sum(m => m.GpuPercent ?? 0)) : null;

    /// <summary>The member that started first; it stands for the group in the name, path and user columns.</summary>
    public ProcessInfo Lead { get; } = Members.OrderBy(m => m.StartTime ?? DateTimeOffset.MaxValue).ThenBy(m => m.Pid).First();
}

/// <summary>One line of the displayed list: a group header, a member of an expanded group, or a lone process.</summary>
public sealed record ProcessListEntry(ProcessInfo? Process, ProcessGroup? Group, bool IsChild)
{
    public bool IsGroup => Group is not null;

    public string Key => Group is not null ? "group:" + Group.Key : Process!.Key;
}

/// <summary>Pure sorting and grouping for the process list.</summary>
public static class ProcessSorting
{
    /// <summary>Maps a column's sort key ("Cpu", "Memory"…) to the enum; false for unknown names.</summary>
    public static bool TryParseColumn(string? name, out ProcessSortColumn column) =>
        Enum.TryParse(name, ignoreCase: true, out column) && Enum.IsDefined(column);

    /// <summary>Numbers and times start with the biggest or newest first; text starts A to Z.</summary>
    public static bool DefaultDescending(ProcessSortColumn column) =>
        column is ProcessSortColumn.Cpu or ProcessSortColumn.Memory or ProcessSortColumn.DiskRead
            or ProcessSortColumn.DiskWrite or ProcessSortColumn.Gpu or ProcessSortColumn.StartTime;

    /// <summary>Sorts processes by <paramref name="column"/>; ties are broken by name, then process id.</summary>
    public static IReadOnlyList<ProcessInfo> Sort(IEnumerable<ProcessInfo> processes, ProcessSortColumn column, bool descending)
    {
        ArgumentNullException.ThrowIfNull(processes);
        return Order(processes, column, descending, p => Number(column, p), p => Text(column, p))
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Pid).ToList();
    }

    /// <summary>
    /// Groups processes by program file (path when known, otherwise the name). The title comes from the catalogue when
    /// the program is known, otherwise from the file name.
    /// </summary>
    public static IReadOnlyList<ProcessGroup> GroupByApp(IEnumerable<ProcessInfo> processes)
    {
        ArgumentNullException.ThrowIfNull(processes);
        return processes
            .GroupBy(GroupKey, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var members = g.ToList();
                var lead = members[0];
                string title = ProcessCatalog.Lookup(lead.Name)?.Title ?? DisplayName(lead.Name);
                return new ProcessGroup(g.Key, title, members);
            })
            .ToList();
    }

    /// <summary>
    /// The lines to show: without grouping every process sorted; with grouping one header per program with two or
    /// more processes (members listed under it when its key is in <paramref name="expanded"/>) and lone processes as
    /// plain lines. Groups sort by their totals, members inside a group by the same column.
    /// </summary>
    public static IReadOnlyList<ProcessListEntry> Arrange(IEnumerable<ProcessInfo> processes, bool groupByApp, ProcessSortColumn column, bool descending,
        IReadOnlySet<string>? expanded = null)
    {
        ArgumentNullException.ThrowIfNull(processes);
        if (!groupByApp)
        {
            return Sort(processes, column, descending).Select(p => new ProcessListEntry(p, null, false)).ToList();
        }

        var groups = GroupByApp(processes);
        var lines = new List<ProcessListEntry>();
        var orderedGroups = Order(groups, column, descending, g => Number(column, g), g => Text(column, g))
            .ThenBy(g => g.Title, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Lead.Pid);
        foreach (var group in orderedGroups)
        {
            if (group.Members.Count == 1)
            {
                lines.Add(new ProcessListEntry(group.Members[0], null, false));
                continue;
            }

            lines.Add(new ProcessListEntry(null, group, false));
            if (expanded?.Contains(group.Key) == true)
            {
                lines.AddRange(Sort(group.Members, column, descending).Select(m => new ProcessListEntry(m, null, true)));
            }
        }

        return lines;
    }

    /// <summary>Key used to group processes of the same program.</summary>
    public static string GroupKey(ProcessInfo process) =>
        ProcessSuspicionRules.IsFullPath(process.Path) ? process.Path : ProcessCatalog.Normalize(process.Name);

    private static string DisplayName(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    private static bool IsText(ProcessSortColumn column) =>
        column is ProcessSortColumn.Name or ProcessSortColumn.Publisher or ProcessSortColumn.User or ProcessSortColumn.Path;

    private static double Number(ProcessSortColumn column, ProcessInfo p) => column switch
    {
        ProcessSortColumn.Pid => p.Pid,
        ProcessSortColumn.Cpu => p.CpuPercent,
        ProcessSortColumn.Memory => p.PrivateBytes,
        ProcessSortColumn.DiskRead => p.ReadBytesPerSecond,
        ProcessSortColumn.DiskWrite => p.WriteBytesPerSecond,
        ProcessSortColumn.Gpu => p.GpuPercent ?? -1,
        ProcessSortColumn.StartTime => (p.StartTime ?? DateTimeOffset.MinValue).UtcTicks,
        _ => 0,
    };

    private static double Number(ProcessSortColumn column, ProcessGroup g) => column switch
    {
        ProcessSortColumn.Cpu => g.CpuPercent,
        ProcessSortColumn.Memory => g.PrivateBytes,
        ProcessSortColumn.DiskRead => g.ReadBytesPerSecond,
        ProcessSortColumn.DiskWrite => g.WriteBytesPerSecond,
        ProcessSortColumn.Gpu => g.GpuPercent ?? -1,
        _ => Number(column, g.Lead),
    };

    // Missing text sorts after every real value.
    private static string Text(ProcessSortColumn column, ProcessInfo p) => column switch
    {
        ProcessSortColumn.Name => p.Name,
        ProcessSortColumn.Publisher => p.Signature.Publisher ?? "\uffff",
        ProcessSortColumn.User => p.User ?? "\uffff",
        ProcessSortColumn.Path => p.Path ?? "\uffff",
        _ => string.Empty,
    };

    private static string Text(ProcessSortColumn column, ProcessGroup g) =>
        column == ProcessSortColumn.Name ? g.Title : Text(column, g.Lead);

    private static IOrderedEnumerable<T> Order<T>(IEnumerable<T> items, ProcessSortColumn column, bool descending, Func<T, double> number, Func<T, string> text)
    {
        if (IsText(column))
        {
            return descending ? items.OrderByDescending(text, StringComparer.OrdinalIgnoreCase) : items.OrderBy(text, StringComparer.OrdinalIgnoreCase);
        }

        return descending ? items.OrderByDescending(number) : items.OrderBy(number);
    }
}
