namespace PcChecker.Core.Processes;

/// <summary>What the Processes page remembers: the view, the update speed, the visible columns and recent "Run new task" commands.</summary>
public sealed class ProcessesSettings
{
    /// <summary>True for the flat Details list, false for Task Manager's grouped Processes list.</summary>
    public bool DetailsView { get; set; }

    /// <summary>How often the list refreshes.</summary>
    public ProcessUpdateSpeed UpdateSpeed { get; set; } = ProcessUpdateSpeed.Normal;

    /// <summary>Column ids shown in the Processes view, or null for the defaults (see <see cref="ProcessColumns"/>).</summary>
    public List<string>? ProcessesColumns { get; set; }

    /// <summary>Column ids shown in the Details view, or null for the defaults.</summary>
    public List<string>? DetailsColumns { get; set; }

    /// <summary>Commands typed into "Run new task", newest first.</summary>
    public List<string> RecentRunCommands { get; set; } = [];
}
