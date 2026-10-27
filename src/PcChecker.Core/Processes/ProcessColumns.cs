namespace PcChecker.Core.Processes;

/// <summary>A column of the process list: its id (the sort key), header, one-line explanation and where it shows by default.</summary>
public sealed record ProcessColumnInfo(string Id, string Header, string Description, bool InProcessesByDefault, bool InDetailsByDefault)
{
    /// <summary>The Name column is always shown.</summary>
    public bool CanHide => Id != nameof(ProcessSortColumn.Name);
}

/// <summary>
/// The columns the list can show and which ones are visible. The visible set is stored in settings as a list of ids per
/// view; a missing list means "the defaults", unknown ids (from another version) are ignored and Name is always shown.
/// </summary>
public static class ProcessColumns
{
    public static IReadOnlyList<ProcessColumnInfo> All { get; } =
    [
        new(nameof(ProcessSortColumn.Name), "Name", "The program and what it is.", true, true),
        new(nameof(ProcessSortColumn.Status), "Status", "Not responding, Suspended or Efficiency mode. Empty while running normally.", true, true),
        new(nameof(ProcessSortColumn.Pid), "PID", "Process ID: the number Windows gives each running program.", false, true),
        new(nameof(ProcessSortColumn.Cpu), "CPU", "Share of the whole processor.", true, true),
        new(nameof(ProcessSortColumn.Memory), "Memory", "Memory only this program uses (private working set, as in Task Manager).", true, true),
        new(nameof(ProcessSortColumn.Disk), "Disk", "Reading and writing files, per second.", true, false),
        new(nameof(ProcessSortColumn.DiskRead), "Disk read", "Reading files, per second.", false, false),
        new(nameof(ProcessSortColumn.DiskWrite), "Disk write", "Writing files, per second.", false, false),
        new(nameof(ProcessSortColumn.Network), "Network", "Open network connections. Windows does not report data per program without a trace, so connections are counted.", true, false),
        new(nameof(ProcessSortColumn.Gpu), "GPU", "Use of the busiest graphics engine.", true, false),
        new(nameof(ProcessSortColumn.GpuEngine), "GPU engine", "Which graphics engine it uses most (3D, video decode…).", false, false),
        new(nameof(ProcessSortColumn.Power), "Power usage", "Estimated energy use right now, from processor, graphics and disk use.", true, false),
        new(nameof(ProcessSortColumn.PowerTrend), "Power usage trend", "Estimated energy use over the last two minutes.", false, false),
        new(nameof(ProcessSortColumn.Publisher), "Publisher", "Who signed the program file.", false, false),
        new(nameof(ProcessSortColumn.User), "User name", "The account the program runs as.", false, true),
        new(nameof(ProcessSortColumn.Session), "Session", "0 for Windows services, 1 and up for people signed in.", false, false),
        new(nameof(ProcessSortColumn.Architecture), "Architecture", "x64, x86 (32-bit) or ARM64.", false, true),
        new(nameof(ProcessSortColumn.Elevated), "Elevated", "Yes when it runs with administrator rights.", false, false),
        new(nameof(ProcessSortColumn.Priority), "Priority", "Which program gets the processor first when the PC is busy.", false, false),
        new(nameof(ProcessSortColumn.Handles), "Handles", "Open files, registry keys and other Windows objects.", false, false),
        new(nameof(ProcessSortColumn.Threads), "Threads", "Parts of the program that run at the same time.", false, false),
        new(nameof(ProcessSortColumn.StartTime), "Started", "When the program started.", false, false),
        new(nameof(ProcessSortColumn.Description), "Description", "The description inside the program file.", false, true),
        new(nameof(ProcessSortColumn.CommandLine), "Command line", "The program file and the options it was started with.", false, false),
        new(nameof(ProcessSortColumn.Path), "File", "Where the program file is.", false, false),
    ];

    /// <summary>The column with <paramref name="id"/>, or null.</summary>
    public static ProcessColumnInfo? Find(string? id) => All.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The default visible columns of a view, in catalogue order.</summary>
    public static IReadOnlyList<string> Defaults(bool detailsView) =>
        All.Where(c => detailsView ? c.InDetailsByDefault : c.InProcessesByDefault).Select(c => c.Id).ToList();

    /// <summary>
    /// The visible columns from saved settings: null means the defaults; unknown ids and duplicates are dropped; Name is
    /// always included; the result follows catalogue order.
    /// </summary>
    public static IReadOnlyList<string> Resolve(IEnumerable<string>? saved, bool detailsView)
    {
        if (saved is null)
        {
            return Defaults(detailsView);
        }

        var wanted = new HashSet<string>(saved.Where(s => s is not null), StringComparer.OrdinalIgnoreCase) { nameof(ProcessSortColumn.Name) };
        return All.Where(c => wanted.Contains(c.Id)).Select(c => c.Id).ToList();
    }

    /// <summary>The visible columns after showing or hiding <paramref name="id"/> (Name cannot be hidden; unknown ids change nothing).</summary>
    public static List<string> Toggle(IEnumerable<string> visible, string id, bool show)
    {
        ArgumentNullException.ThrowIfNull(visible);
        var set = new HashSet<string>(visible, StringComparer.OrdinalIgnoreCase);
        var column = Find(id);
        if (column is not null && (show || column.CanHide))
        {
            if (show)
            {
                set.Add(column.Id);
            }
            else
            {
                set.Remove(column.Id);
            }
        }

        return [.. Resolve(set, detailsView: false)];
    }
}

/// <summary>Task Manager's update speeds.</summary>
public static class UpdateSpeedRules
{
    /// <summary>Time between refreshes, or null when paused.</summary>
    public static TimeSpan? Interval(ProcessUpdateSpeed speed) => speed switch
    {
        ProcessUpdateSpeed.High => TimeSpan.FromMilliseconds(500),
        ProcessUpdateSpeed.Low => TimeSpan.FromSeconds(4),
        ProcessUpdateSpeed.Paused => null,
        _ => TimeSpan.FromSeconds(1),
    };
}
