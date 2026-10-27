namespace PcChecker.Core.Processes;

/// <summary>Columns the process list can be sorted by. The names double as column ids (see <see cref="ProcessColumns"/>).</summary>
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
    Status,
    Disk,
    Network,
    GpuEngine,
    Power,
    PowerTrend,
    Session,
    Architecture,
    Elevated,
    Priority,
    Handles,
    Threads,
    CommandLine,
    Description,
}

/// <summary>A set of processes shown under one line (one program, or an app and the helpers it started), with summed usage.</summary>
public sealed record ProcessGroup(string Key, string Title, IReadOnlyList<ProcessInfo> Members)
{
    public double CpuPercent { get; } = Members.Sum(m => m.CpuPercent);
    public long PrivateBytes { get; } = Members.Sum(m => m.PrivateBytes);
    public double ReadBytesPerSecond { get; } = Members.Sum(m => m.ReadBytesPerSecond);
    public double WriteBytesPerSecond { get; } = Members.Sum(m => m.WriteBytesPerSecond);
    public double? GpuPercent { get; } = Members.Any(m => m.GpuPercent is not null) ? Math.Min(100, Members.Sum(m => m.GpuPercent ?? 0)) : null;

    /// <summary>Open connections of all members, or null when none could be counted.</summary>
    public int? ConnectionCount { get; } = Members.Any(m => m.ConnectionCount is not null) ? Members.Sum(m => m.ConnectionCount ?? 0) : null;

    public double DiskBytesPerSecond => ReadBytesPerSecond + WriteBytesPerSecond;

    /// <summary>
    /// The member that stands for the group in the name, path and user columns: the one with a window (the app itself),
    /// otherwise the one that started first.
    /// </summary>
    public ProcessInfo Lead { get; } = Members.OrderByDescending(m => m.HasWindow)
        .ThenBy(m => m.StartTime ?? DateTimeOffset.MaxValue).ThenBy(m => m.Pid).First();

    /// <summary>Apps, background processes or Windows processes.</summary>
    public ProcessCategory Category { get; init; } = ProcessCategory.Background;

    /// <summary>"Not responding" when any member hangs, "Suspended" when every member is paused, otherwise running.</summary>
    public ProcessRunState State => ProcessStatusRules.GroupState(Members);

    /// <summary>True when every member runs in efficiency mode.</summary>
    public bool EfficiencyMode => Members.All(m => m.EfficiencyMode);
}

/// <summary>
/// One line of the displayed list: a section heading (Apps, Background processes, Windows processes), a group header,
/// a member of an expanded group, or a lone process.
/// </summary>
public sealed record ProcessListEntry(ProcessInfo? Process, ProcessGroup? Group, bool IsChild)
{
    /// <summary>Set on section headings only.</summary>
    public ProcessCategory? Section { get; init; }

    /// <summary>Number of programs listed under a section heading.</summary>
    public int SectionCount { get; init; }

    /// <summary>Category of the line (for lone processes and groups in the Processes view).</summary>
    public ProcessCategory? Category { get; init; }

    public bool IsGroup => Group is not null;

    public bool IsSection => Section is not null;

    public string Key => Section is { } section ? "section:" + section
        : Group is not null ? "group:" + Group.Key
        : Process!.Key;
}

/// <summary>Pure sorting and grouping for the process list.</summary>
public static class ProcessSorting
{
    /// <summary>Maps a column's sort key ("Cpu", "Memory"…) to the enum; false for unknown names.</summary>
    public static bool TryParseColumn(string? name, out ProcessSortColumn column) =>
        Enum.TryParse(name, ignoreCase: true, out column) && Enum.IsDefined(column);

    /// <summary>Numbers, times and states start with the biggest, newest or most urgent first; text starts A to Z.</summary>
    public static bool DefaultDescending(ProcessSortColumn column) =>
        column is ProcessSortColumn.Cpu or ProcessSortColumn.Memory or ProcessSortColumn.DiskRead
            or ProcessSortColumn.DiskWrite or ProcessSortColumn.Gpu or ProcessSortColumn.StartTime or ProcessSortColumn.Disk
            or ProcessSortColumn.Network or ProcessSortColumn.Power or ProcessSortColumn.PowerTrend or ProcessSortColumn.Handles
            or ProcessSortColumn.Threads or ProcessSortColumn.Status or ProcessSortColumn.Elevated;

    /// <summary>
    /// Sorts processes by <paramref name="column"/>; ties are broken by name, then process id. <paramref name="trend"/>
    /// supplies the power usage trend score for that column (the current score is used without it).
    /// </summary>
    public static IReadOnlyList<ProcessInfo> Sort(IEnumerable<ProcessInfo> processes, ProcessSortColumn column, bool descending,
        Func<ProcessInfo, double>? trend = null)
    {
        ArgumentNullException.ThrowIfNull(processes);
        return Order(processes, column, descending, p => Number(column, p, trend), p => Text(column, p))
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Pid).ToList();
    }

    /// <summary>
    /// Groups processes by program file (path when known, otherwise the name). The title comes from the catalogue when
    /// the program is known, otherwise from the file description or the file name.
    /// </summary>
    public static IReadOnlyList<ProcessGroup> GroupByApp(IEnumerable<ProcessInfo> processes)
    {
        ArgumentNullException.ThrowIfNull(processes);
        return processes
            .GroupBy(GroupKey, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var members = g.ToList();
                return new ProcessGroup(g.Key, TitleOf(members[0]), members);
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

        var lines = new List<ProcessListEntry>();
        AddGroups(lines, GroupByApp(processes), column, descending, expanded, null, null);
        return lines;
    }

    /// <summary>Adds sorted groups (header plus expanded members) or lone processes to <paramref name="lines"/>.</summary>
    internal static void AddGroups(List<ProcessListEntry> lines, IEnumerable<ProcessGroup> groups, ProcessSortColumn column, bool descending,
        IReadOnlySet<string>? expanded, ProcessCategory? category, Func<ProcessInfo, double>? trend)
    {
        var orderedGroups = Order(groups, column, descending, g => Number(column, g, trend), g => Text(column, g))
            .ThenBy(g => g.Title, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Lead.Pid);
        foreach (var group in orderedGroups)
        {
            if (group.Members.Count == 1)
            {
                lines.Add(new ProcessListEntry(group.Members[0], null, false) { Category = category });
                continue;
            }

            lines.Add(new ProcessListEntry(null, group, false) { Category = category });
            if (expanded?.Contains(group.Key) == true)
            {
                lines.AddRange(Sort(group.Members, column, descending, trend).Select(m => new ProcessListEntry(m, null, true) { Category = category }));
            }
        }
    }

    /// <summary>Key used to group processes of the same program.</summary>
    public static string GroupKey(ProcessInfo process) =>
        ProcessSuspicionRules.IsFullPath(process.Path) ? process.Path : ProcessCatalog.Normalize(process.Name);

    /// <summary>Friendly name: the catalogue title, the file description, or the file name without ".exe".</summary>
    public static string TitleOf(ProcessInfo process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (ProcessCatalog.Lookup(process.Name)?.Title is { } title)
        {
            return title;
        }

        return string.IsNullOrWhiteSpace(process.Description) ? DisplayName(process.Name) : process.Description.Trim();
    }

    private static string DisplayName(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    private static bool IsText(ProcessSortColumn column) =>
        column is ProcessSortColumn.Name or ProcessSortColumn.Publisher or ProcessSortColumn.User or ProcessSortColumn.Path
            or ProcessSortColumn.GpuEngine or ProcessSortColumn.CommandLine or ProcessSortColumn.Description or ProcessSortColumn.Architecture;

    private static double Number(ProcessSortColumn column, ProcessInfo p, Func<ProcessInfo, double>? trend) => column switch
    {
        ProcessSortColumn.Pid => p.Pid,
        ProcessSortColumn.Cpu => p.CpuPercent,
        ProcessSortColumn.Memory => p.PrivateBytes,
        ProcessSortColumn.DiskRead => p.ReadBytesPerSecond,
        ProcessSortColumn.DiskWrite => p.WriteBytesPerSecond,
        ProcessSortColumn.Disk => p.DiskBytesPerSecond,
        ProcessSortColumn.Network => p.ConnectionCount ?? -1,
        ProcessSortColumn.Gpu => p.GpuPercent ?? -1,
        ProcessSortColumn.Power => PowerUsageRules.Score(p),
        ProcessSortColumn.PowerTrend => trend?.Invoke(p) ?? PowerUsageRules.Score(p),
        ProcessSortColumn.StartTime => (p.StartTime ?? DateTimeOffset.MinValue).UtcTicks,
        ProcessSortColumn.Status => StatusRank(p.State, p.EfficiencyMode),
        ProcessSortColumn.Session => p.SessionId ?? -1,
        ProcessSortColumn.Elevated => p.IsElevated switch { true => 1, false => 0, _ => -1 },
        ProcessSortColumn.Priority => p.Priority is { } priority ? (int)priority : -1,
        ProcessSortColumn.Handles => p.HandleCount ?? -1,
        ProcessSortColumn.Threads => p.ThreadCount ?? -1,
        _ => 0,
    };

    private static double Number(ProcessSortColumn column, ProcessGroup g, Func<ProcessInfo, double>? trend) => column switch
    {
        ProcessSortColumn.Cpu => g.CpuPercent,
        ProcessSortColumn.Memory => g.PrivateBytes,
        ProcessSortColumn.DiskRead => g.ReadBytesPerSecond,
        ProcessSortColumn.DiskWrite => g.WriteBytesPerSecond,
        ProcessSortColumn.Disk => g.DiskBytesPerSecond,
        ProcessSortColumn.Network => g.ConnectionCount ?? -1,
        ProcessSortColumn.Gpu => g.GpuPercent ?? -1,
        ProcessSortColumn.Power => PowerUsageRules.Score(g),
        ProcessSortColumn.PowerTrend => trend is null ? PowerUsageRules.Score(g) : g.Members.Sum(trend),
        ProcessSortColumn.Status => StatusRank(g.State, g.EfficiencyMode),
        ProcessSortColumn.Handles => g.Members.Sum(m => m.HandleCount ?? 0),
        ProcessSortColumn.Threads => g.Members.Sum(m => m.ThreadCount ?? 0),
        _ => Number(column, g.Lead, trend),
    };

    // Not responding first, then suspended, then efficiency mode, then running (descending order).
    private static int StatusRank(ProcessRunState state, bool efficiency) => state switch
    {
        ProcessRunState.NotResponding => 3,
        ProcessRunState.Suspended => 2,
        _ => efficiency ? 1 : 0,
    };

    // Missing text sorts after every real value.
    private static string Text(ProcessSortColumn column, ProcessInfo p) => column switch
    {
        ProcessSortColumn.Name => p.Name,
        ProcessSortColumn.Publisher => p.Signature.Publisher ?? "￿",
        ProcessSortColumn.User => p.User ?? "￿",
        ProcessSortColumn.Path => p.Path ?? "￿",
        ProcessSortColumn.GpuEngine => p.GpuEngine ?? "￿",
        ProcessSortColumn.CommandLine => p.CommandLine ?? "￿",
        ProcessSortColumn.Description => p.Description ?? "￿",
        ProcessSortColumn.Architecture => p.Architecture == ProcessArchitecture.Unknown ? "￿" : p.Architecture.ToString(),
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
