using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PcChecker.App.Localization;
using PcChecker.Core.ScheduledTasks;

namespace PcChecker.App.ViewModels;

/// <summary>One task on the Scheduled tasks page. Updated in place on refresh so the selection and scroll position stay.</summary>
public sealed partial class ScheduledTaskRow : ObservableObject
{
    public ScheduledTaskRow(ScheduledTaskInfo task, bool hasLiveState)
    {
        Task = task;
        HasLiveState = hasLiveState;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name), nameof(Folder), nameof(StateText), nameof(TriggerText), nameof(TriggerDetails), nameof(LastRunText),
        nameof(ResultText), nameof(ResultIsProblem), nameof(NextRunText), nameof(PublisherText), nameof(ActionText), nameof(RunsAsText),
        nameof(FlagText), nameof(IsFlagged), nameof(NotesText), nameof(HasNotes), nameof(IsOn), nameof(Description), nameof(HasDescription),
        nameof(IsMicrosoft), nameof(FromText), nameof(IsRunning))]
    private ScheduledTaskInfo _task;

    public bool HasLiveState { get; private set; }

    public string Key => Task.Path;

    public string Name => Task.Name;

    public string Folder => Task.Folder == "\\" ? Loc.T("Top level") : Task.Folder.TrimStart('\\');

    public bool IsOn => Task.State != TaskRunState.Disabled && Task.Definition.Enabled;

    public bool IsRunning => Task.State == TaskRunState.Running;

    public bool IsMicrosoft => Task.IsMicrosoft;

    public string StateText => Task.State switch
    {
        TaskRunState.Running => Loc.T("Running"),
        TaskRunState.Disabled => Loc.T("Off"),
        TaskRunState.Queued => Loc.T("Waiting"),
        TaskRunState.Ready => Loc.T("Ready"),
        _ => IsOn ? Loc.T("On") : Loc.T("Off"),
    };

    public string TriggerText => TaskTriggerText.Summary(Task.Definition.Triggers);

    /// <summary>Every trigger on its own line, for the details card.</summary>
    public string TriggerDetails => Task.Definition.Triggers.Count == 0
        ? TaskTriggerText.Summary([])
        : string.Join(Environment.NewLine, Task.Definition.Triggers.Select(TaskTriggerText.Describe));

    public string LastRunText => HasLiveState ? When(Task.LastRun, Loc.T("Never")) : "—";

    public string NextRunText => !HasLiveState ? "—" : !IsOn ? Loc.T("Turned off") : When(Task.NextRun, Loc.T("Not scheduled"));

    public string ResultText => HasLiveState
        ? TaskResultText.Describe(Task.LastResult, Task.LastRun, SystemMessage)
        : Loc.T("Not known");

    public bool ResultIsProblem => HasLiveState && Task.LastRun is not null && TaskResultText.IsProblem(Task.LastResult);

    public string PublisherText => TaskPublisherRules.Display(Task.Path, Task.Definition.Author, Task.Signature, Task.IsMicrosoft);

    public string FromText => Task.IsMicrosoft ? Loc.T("Microsoft") : Loc.T("Other company");

    public string ActionText => TaskActionText.Describe(Task.Definition);

    public string RunsAsText => TaskActionText.RunsAs(Task.Definition);

    public bool IsFlagged => Task.IsFlagged;

    public string FlagText => string.Join(Environment.NewLine, Task.Flags);

    public bool HasNotes => Task.Notes.Count > 0;

    public string NotesText => string.Join(Environment.NewLine, Task.Notes);

    public string? Description => string.IsNullOrWhiteSpace(Task.Definition.Description) || Task.Definition.Description.StartsWith("$(", StringComparison.Ordinal)
        ? null
        : Task.Definition.Description;

    public bool HasDescription => Description is not null;

    public void Update(ScheduledTaskInfo task, bool hasLiveState)
    {
        HasLiveState = hasLiveState;
        Task = task;
    }

    /// <summary>Everything about the task as text, for the clipboard.</summary>
    public string CopyText() => string.Join(Environment.NewLine,
        Task.Path,
        Loc.T("Status") + ": " + StateText,
        Loc.T("When") + ": " + TriggerDetails.Replace(Environment.NewLine, "; ", StringComparison.Ordinal),
        Loc.T("Runs") + ": " + ActionText,
        Loc.T("As") + ": " + RunsAsText,
        Loc.T("Last run") + ": " + LastRunText + " (" + ResultText + ")",
        Loc.T("Next run") + ": " + NextRunText,
        Loc.T("From") + ": " + PublisherText);

    internal static string When(DateTimeOffset? time, string none)
    {
        if (time is not { } t)
        {
            return none;
        }

        var local = t.ToLocalTime();
        var today = DateTime.Today;
        string clock = local.ToString("t", CultureInfo.CurrentCulture);
        return (local.Date - today).Days switch
        {
            0 => Loc.F("Today {0}", clock),
            -1 => Loc.F("Yesterday {0}", clock),
            1 => Loc.F("Tomorrow {0}", clock),
            _ => local.ToString("g", CultureInfo.CurrentCulture),
        };
    }

    private static string? SystemMessage(int code)
    {
        try
        {
            return new System.ComponentModel.Win32Exception(TaskResultText.MessageCode(code)).Message;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>A deleted task that can be put back.</summary>
public sealed class TaskBackupRow
{
    public TaskBackupRow(TaskBackupEntry entry)
    {
        Entry = entry;
    }

    public TaskBackupEntry Entry { get; }

    public string Name => Entry.Name;

    public string Path => Entry.TaskPath;

    public string WhenText => Loc.F("Deleted {0}", ScheduledTaskRow.When(Entry.Created, "—"));

    public string AccessibleName => Loc.F("Restore {0}", Entry.Name);
}
