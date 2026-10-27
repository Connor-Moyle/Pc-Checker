using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Localization;
using PcChecker.App.Services;
using PcChecker.Core.ScheduledTasks;
using PcChecker.Core.Settings;

namespace PcChecker.App.ViewModels;

/// <summary>
/// Scheduled tasks page: every Windows Task Scheduler task with when it runs, its last result and what it runs, other
/// companies' tasks first, warnings with a plain reason, and the actions people need (run, end, turn off or on, delete
/// with a saved copy, restore). Windows' own tasks are read-only outside Advanced mode.
/// </summary>
public sealed partial class ScheduledTasksViewModel : ViewModelBase
{
    private readonly ITaskSchedulerService _tasks;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly ILogger<ScheduledTasksViewModel> _logger;
    private readonly Dictionary<string, ScheduledTaskRow> _rowByPath = new(StringComparer.OrdinalIgnoreCase);
    private ScheduledTaskList _list = ScheduledTaskList.Empty(null);

    public ScheduledTasksViewModel(ITaskSchedulerService tasks, ISettingsService settings, IDialogService dialogs,
        INotificationService notifications, ILogger<ScheduledTasksViewModel> logger)
        : base(settings)
    {
        _tasks = tasks;
        _dialogs = dialogs;
        _notifications = notifications;
        _logger = logger;
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsAdvancedMode))
            {
                NotifySelectionCommands();
            }
        };
    }

    public ObservableCollection<ScheduledTaskRow> Rows { get; } = [];

    public ObservableCollection<TaskBackupRow> Backups { get; } = [];

    /// <summary>The filter options, in the order of <see cref="TaskListFilter"/>.</summary>
    public IReadOnlyList<string> FilterOptions { get; } =
    [
        Loc.T("Not from Microsoft"),
        Loc.T("All tasks"),
        Loc.T("Microsoft"),
        Loc.T("With warnings"),
        Loc.T("Turned off"),
    ];

    [ObservableProperty]
    private int _selectedFilterIndex;

    [ObservableProperty]
    private string? _searchText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(SelectedReason), nameof(HasSelectedReason))]
    [NotifyCanExecuteChangedFor(nameof(RunNowCommand), nameof(EndCommand), nameof(TurnOffCommand), nameof(TurnOnCommand), nameof(DeleteCommand),
        nameof(OpenFileLocationCommand), nameof(SearchOnlineCommand), nameof(CopyDetailsCommand))]
    private ScheduledTaskRow? _selectedRow;

    [ObservableProperty]
    private string _thirdPartyText = "—";

    [ObservableProperty]
    private string _runningText = "—";

    [ObservableProperty]
    private string _flaggedText = "—";

    [ObservableProperty]
    private string _turnedOffText = "—";

    [ObservableProperty]
    private string _statusText = Loc.T("Reading scheduled tasks…");

    [ObservableProperty]
    private string? _warningText;

    [ObservableProperty]
    private bool _hasNoRows;

    [ObservableProperty]
    private bool _hasBackups;

    public bool HasWarning => !string.IsNullOrWhiteSpace(WarningText);

    partial void OnWarningTextChanged(string? value) => OnPropertyChanged(nameof(HasWarning));

    public bool HasSelection => SelectedRow is not null;

    /// <summary>Why the selected task is (partly) locked, in one line, or null.</summary>
    public string? SelectedReason => SelectedRow?.Task is not { } task ? null
        : task.IsPcCheckerTask ? TaskSafetyRules.OwnTaskReason
        : task.IsWindowsTask && !IsAdvancedMode ? TaskSafetyRules.WindowsReadOnlyReason
        : task.IsWindowsTask ? Loc.T("One of Windows' own tasks. Advanced mode lets you turn it off or on; it cannot be deleted.")
        : task.IsMicrosoft ? Loc.T("A Microsoft task: you can turn it off, but it cannot be deleted here.")
        : null;

    public bool HasSelectedReason => SelectedReason is not null;

    public override Task InitializeAsync(CancellationToken cancellationToken) => LoadAsync();

    public override Task RefreshAsync(CancellationToken cancellationToken) => LoadAsync();

    [RelayCommand]
    private Task Refresh() => LoadAsync();

    private Task LoadAsync() =>
        RunBusyAsync(async ct =>
        {
            _list = await _tasks.GetTasksAsync(ct);
            ApplyList();
            await LoadBackupsAsync(ct);
        }, Loc.T("Reading scheduled tasks…"));

    private void ApplyList()
    {
        RebuildRows();
        int others = _list.Tasks.Count(t => !t.IsMicrosoft);
        ThirdPartyText = others.ToString(CultureInfo.CurrentCulture);
        RunningText = _list.HasLiveState ? _list.Tasks.Count(t => t.State == TaskRunState.Running).ToString(CultureInfo.CurrentCulture) : "—";
        FlaggedText = _list.Tasks.Count(t => t.IsFlagged).ToString(CultureInfo.CurrentCulture);
        TurnedOffText = _list.Tasks.Count(t => t.State == TaskRunState.Disabled || !t.Definition.Enabled).ToString(CultureInfo.CurrentCulture);
        StatusText = Loc.F("{0} tasks, {1} from other companies", _list.Tasks.Count, others);
        WarningText = _list.Warning;
        NotifySelectionCommands();
    }

    private async Task LoadBackupsAsync(CancellationToken ct)
    {
        try
        {
            var backups = await _tasks.GetBackupsAsync(ct);
            Backups.Clear();
            foreach (var entry in backups)
            {
                Backups.Add(new TaskBackupRow(entry));
            }

            HasBackups = Backups.Count > 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "The list of deleted tasks could not be read.");
        }
    }

    partial void OnSearchTextChanged(string? value) => RebuildRows();

    partial void OnSelectedFilterIndexChanged(int value) => RebuildRows();

    private void RebuildRows()
    {
        var filter = (TaskListFilter)Math.Clamp(SelectedFilterIndex, 0, FilterOptions.Count - 1);
        var visible = TaskSafetyRules.Filter(_list.Tasks, filter, SearchText);
        var target = new List<ScheduledTaskRow>(visible.Count);
        foreach (var task in visible)
        {
            if (_rowByPath.TryGetValue(task.Path, out var row))
            {
                row.Update(task, _list.HasLiveState);
            }
            else
            {
                row = new ScheduledTaskRow(task, _list.HasLiveState);
                _rowByPath[task.Path] = row;
            }

            target.Add(row);
        }

        Sync(target);
        HasNoRows = Rows.Count == 0;
        if (SelectedRow is not null && !Rows.Contains(SelectedRow))
        {
            SelectedRow = null;
        }
    }

    private void Sync(IReadOnlyList<ScheduledTaskRow> target)
    {
        var keep = new HashSet<ScheduledTaskRow>(target);
        for (int i = Rows.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(Rows[i]))
            {
                Rows.RemoveAt(i);
            }
        }

        for (int i = 0; i < target.Count; i++)
        {
            if (i < Rows.Count && ReferenceEquals(Rows[i], target[i]))
            {
                continue;
            }

            int current = Rows.IndexOf(target[i]);
            if (current >= 0)
            {
                Rows.Move(current, i);
            }
            else
            {
                Rows.Insert(i, target[i]);
            }
        }
    }

    [RelayCommand]
    private void ShowAll()
    {
        SearchText = null;
        SelectedFilterIndex = (int)TaskListFilter.All;
    }

    private bool HasSelectionForCommand() => SelectedRow is not null;

    private bool CanRunNow() => SelectedRow is { } row && TaskSafetyRules.RunRefusal(row.Task) is null;

    private bool CanEnd() => SelectedRow is { } row && TaskSafetyRules.EndRefusal(row.Task) is null;

    private bool CanTurnOff() => SelectedRow is { IsOn: true } row && TaskSafetyRules.ToggleRefusal(row.Task, IsAdvancedMode) is null;

    private bool CanTurnOn() => SelectedRow is { IsOn: false } row && TaskSafetyRules.ToggleRefusal(row.Task, IsAdvancedMode) is null;

    private bool CanDelete() => SelectedRow is { } row && TaskSafetyRules.DeleteRefusal(row.Task) is null;

    private bool CanOpenFileLocation() => SelectedRow?.Task.ProgramExists == true;

    [RelayCommand(CanExecute = nameof(CanRunNow))]
    private async Task RunNow()
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        string message = Loc.F("Start {0} now? It runs {1} as {2}.", row.Name, row.ActionText, row.RunsAsText);
        if (row.IsFlagged)
        {
            message += "\n\n" + Loc.T("This task has warnings. Only start it if you trust it:") + "\n" + row.FlagText;
        }

        if (await _dialogs.ConfirmAsync(Loc.T("Run task now?"), message, Loc.T("Run now"), isDanger: row.IsFlagged))
        {
            await RunActionAsync(() => _tasks.RunAsync(row.Task, CancellationToken.None));
        }
    }

    [RelayCommand(CanExecute = nameof(CanEnd))]
    private async Task End()
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        if (await _dialogs.ConfirmAsync(Loc.T("End task?"),
                Loc.F("Stop {0}? The program it started is closed, and unsaved work in it may be lost.", row.Name), Loc.T("End"), isDanger: true))
        {
            await RunActionAsync(() => _tasks.EndAsync(row.Task, CancellationToken.None));
        }
    }

    [RelayCommand(CanExecute = nameof(CanTurnOff))]
    private async Task TurnOff()
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        string message = Loc.F("{0} will not run until you turn it on again. You can turn it back on here at any time, and the change is recorded in the Action Journal.", row.Name);
        var warning = TaskSafetyRules.TurnOffWarning(row.Task);
        if (warning is not null)
        {
            message = warning + "\n\n" + message;
        }

        if (row.Task.IsWindowsTask)
        {
            message += "\n\n" + Loc.T("A restore point is created first.");
        }

        if (await _dialogs.ConfirmAsync(Loc.T("Turn off task?"), message, Loc.T("Turn off"), isDanger: row.Task.IsWindowsTask))
        {
            await RunActionAsync(() => _tasks.SetEnabledAsync(row.Task, false, IsAdvancedMode, CancellationToken.None));
        }
    }

    [RelayCommand(CanExecute = nameof(CanTurnOn))]
    private async Task TurnOn()
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        string message = Loc.F("Turn {0} on again? It runs on its schedule from now on.", row.Name);
        if (row.IsFlagged)
        {
            message += "\n\n" + Loc.T("This task has warnings:") + "\n" + row.FlagText;
        }

        if (await _dialogs.ConfirmAsync(Loc.T("Turn on task?"), message, Loc.T("Turn on"), isDanger: row.IsFlagged))
        {
            await RunActionAsync(() => _tasks.SetEnabledAsync(row.Task, true, IsAdvancedMode, CancellationToken.None));
        }
    }

    /// <summary>Ctrl+D: turns the selected task off, or on when it is off.</summary>
    [RelayCommand]
    private Task ToggleOnOff()
    {
        if (CanTurnOff())
        {
            return TurnOff();
        }

        if (CanTurnOn())
        {
            return TurnOn();
        }

        if (SelectedRow is { } row && TaskSafetyRules.ToggleRefusal(row.Task, IsAdvancedMode) is { } reason)
        {
            _notifications.Info(Loc.T("Not available"), reason);
        }

        return Task.CompletedTask;
    }

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task Delete()
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        string message = Loc.F("Delete {0}? PC Checker saves a copy first, so you can put it back under Deleted tasks. If you only want it to stop running, Turn off is enough.", row.Name);
        if (row.Task.Definition.NeedsPassword)
        {
            message += "\n\n" + Loc.T("It runs with a saved password, so putting it back later needs that password.");
        }

        if (await _dialogs.ConfirmAsync(Loc.T("Delete task?"), message, Loc.T("Delete"), isDanger: true))
        {
            await RunActionAsync(() => _tasks.DeleteAsync(row.Task, CancellationToken.None));
        }
    }

    [RelayCommand]
    private async Task Restore(TaskBackupRow? backup)
    {
        if (backup is null)
        {
            return;
        }

        if (await _dialogs.ConfirmAsync(Loc.T("Put task back?"),
                Loc.F("Register {0} in Task Scheduler again, exactly as it was when you deleted it?", backup.Path), Loc.T("Restore")))
        {
            await RunActionAsync(() => _tasks.RestoreAsync(backup.Entry, CancellationToken.None));
        }
    }

    [RelayCommand(CanExecute = nameof(CanOpenFileLocation))]
    private void OpenFileLocation()
    {
        if (SelectedRow is { } row)
        {
            Report(_tasks.OpenFileLocation(row.Task), quietSuccess: true);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelectionForCommand))]
    private void SearchOnline()
    {
        if (SelectedRow is { } row)
        {
            Report(_tasks.SearchOnline(row.Task), quietSuccess: true);
        }
    }

    [RelayCommand]
    private void OpenTaskScheduler() => Report(_tasks.OpenTaskScheduler(), quietSuccess: true);

    [RelayCommand(CanExecute = nameof(HasSelectionForCommand))]
    private void CopyDetails()
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        try
        {
            Clipboard.SetText(row.CopyText());
            _notifications.Success(Loc.T("Copied"), Loc.F("The details of {0} are on the clipboard.", row.Name));
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Clipboard copy failed.");
        }
    }

    private async Task RunActionAsync(Func<Task<Core.Common.OperationResult>> action)
    {
        await RunBusyAsync(async ct =>
        {
            var result = await action();
            Report(result);
            _list = await _tasks.GetTasksAsync(ct);
            ApplyList();
            await LoadBackupsAsync(ct);
        }, Loc.T("Working…"));
    }

    private void Report(Core.Common.OperationResult result, bool quietSuccess = false)
    {
        if (!result.Success)
        {
            _notifications.Error(Loc.T("Could not complete"), result.Message);
        }
        else if (!quietSuccess || result.Message.StartsWith("Demo mode", StringComparison.Ordinal))
        {
            _notifications.Success(Loc.T("Done"), result.Message);
        }
    }

    private void NotifySelectionCommands()
    {
        RunNowCommand.NotifyCanExecuteChanged();
        EndCommand.NotifyCanExecuteChanged();
        TurnOffCommand.NotifyCanExecuteChanged();
        TurnOnCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        OpenFileLocationCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(SelectedReason));
        OnPropertyChanged(nameof(HasSelectedReason));
    }

    protected override void OnBusyError(Exception exception) => _logger.LogError(exception, "Scheduled tasks page operation failed.");
}
