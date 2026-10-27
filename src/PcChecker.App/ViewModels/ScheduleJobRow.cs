using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcChecker.App.Converters;
using PcChecker.Core.Common;
using PcChecker.Core.Scheduling;
using Wpf.Ui.Controls;

namespace PcChecker.App.ViewModels;

/// <summary>
/// One job in Settings → Schedules: the on/off switch (creates or removes the Windows task), frequency, day, time and
/// job options (edited, then saved with Save), the next and last run, the task's state and "Run now".
/// </summary>
public sealed partial class ScheduleJobRow : ObservableObject
{
    private readonly Func<ScheduleJobRow, bool, Task> _onScheduledChanged;
    private readonly Func<ScheduleJobRow, Task> _onSave;
    private readonly Func<ScheduleJobRow, Task> _onRunNow;
    private readonly Action<ScheduleJobRow> _onOpenOutput;
    private bool _loading;

    [ObservableProperty] private bool _isScheduled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWeekly))]
    private ScheduleFrequency _frequency;

    [ObservableProperty] private DayOfWeek _day;
    [ObservableProperty] private string _time = "10:00";
    [ObservableProperty] private bool _dryRun;
    [ObservableProperty] private double _keepReports = 10;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _isDirty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isWorking;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunNowCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isRunning;

    [ObservableProperty] private string _scheduleText = string.Empty;
    [ObservableProperty] private string? _nextRunText;
    [ObservableProperty] private string? _taskStateText;
    [ObservableProperty] private string? _validationError;
    [ObservableProperty] private string? _progressText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLastRun))]
    private string? _lastRunTitle;

    [ObservableProperty] private string? _lastRunSummary;
    [ObservableProperty] private string? _lastRunWhen;
    [ObservableProperty] private HealthStatus _lastRunStatus;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenOutputCommand))]
    [NotifyPropertyChangedFor(nameof(HasOutput))]
    private string? _outputPath;

    public ScheduleJobRow(ScheduledJobKind job, Func<ScheduleJobRow, bool, Task> onScheduledChanged, Func<ScheduleJobRow, Task> onSave,
        Func<ScheduleJobRow, Task> onRunNow, Action<ScheduleJobRow> onOpenOutput)
    {
        Job = job;
        _onScheduledChanged = onScheduledChanged;
        _onSave = onSave;
        _onRunNow = onRunNow;
        _onOpenOutput = onOpenOutput;
    }

    public ScheduledJobKind Job { get; }

    public string Title => ScheduledJobIds.DisplayName(Job);

    public string Description => ScheduledJobIds.Description(Job, SkipRecentMinutes);

    /// <summary>The Cleanup setting "skip files changed in the last N minutes", shown in the cleanup description.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Description))]
    private int _skipRecentMinutes = 60;

    public SymbolRegular Symbol => Job switch
    {
        ScheduledJobKind.Health => SymbolRegular.Heart24,
        ScheduledJobKind.Cleanup => SymbolRegular.Broom24,
        ScheduledJobKind.Drivers => SymbolRegular.ArrowSync24,
        _ => SymbolRegular.DocumentText24,
    };

    public static IReadOnlyList<ScheduleFrequency> Frequencies { get; } = [ScheduleFrequency.Daily, ScheduleFrequency.Weekly];

    public static IReadOnlyList<DayOfWeek> Days { get; } =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

    public bool IsWeekly => Frequency == ScheduleFrequency.Weekly;

    public bool ShowDryRun => Job == ScheduledJobKind.Cleanup;

    public bool ShowKeepReports => Job == ScheduledJobKind.Report;

    public bool HasLastRun => LastRunTitle is not null;

    public bool HasOutput => !string.IsNullOrEmpty(OutputPath);

    public bool IsIdle => !IsWorking && !IsRunning;

    public string RunNowText => Job == ScheduledJobKind.Cleanup ? "Clean now" : "Run now";

    /// <summary>Loads the saved schedule unless the user has unsaved edits in this row.</summary>
    public void Load(JobSchedule schedule)
    {
        if (IsDirty || IsWorking)
        {
            return;
        }

        _loading = true;
        try
        {
            IsScheduled = schedule.Enabled;
            Frequency = schedule.Frequency;
            Day = schedule.Day;
            Time = schedule.Time;
            DryRun = schedule.DryRun;
            KeepReports = schedule.KeepReports;
            IsDirty = false;
            ValidationError = null;
        }
        finally
        {
            _loading = false;
        }

        UpdateScheduleText();
    }

    /// <summary>The row's values as a schedule (the switch state included).</summary>
    public JobSchedule ToSchedule(bool? enabled = null) => new()
    {
        Job = Job,
        Enabled = enabled ?? IsScheduled,
        Frequency = Frequency,
        Day = Day,
        Time = ScheduleRules.TryParseTime(Time, out var t) ? ScheduleRules.FormatTime(t) : Time,
        DryRun = DryRun,
        KeepReports = (int)Math.Round(KeepReports),
    };

    /// <summary>Sets the switch without triggering the create/remove flow (used to undo a cancelled change).</summary>
    public void SetScheduledSilently(bool value)
    {
        _loading = true;
        try
        {
            IsScheduled = value;
        }
        finally
        {
            _loading = false;
        }

        UpdateScheduleText();
    }

    public void MarkSaved()
    {
        IsDirty = false;
        ValidationError = null;
        UpdateScheduleText();
    }

    public void ApplyResult(ScheduledJobResult? result)
    {
        if (result is null)
        {
            LastRunTitle = null;
            LastRunSummary = null;
            LastRunWhen = null;
            LastRunStatus = HealthStatus.Unknown;
            OutputPath = null;
            return;
        }

        LastRunTitle = result.Title;
        LastRunSummary = result.Summary;
        var how = result.Trigger switch
        {
            "manual" => "run by you",
            "tray" => "from the tray",
            _ => "scheduled",
        };
        LastRunWhen = RelativeTimeConverter.Format(result.Finished, DateTimeOffset.Now, CultureInfo.CurrentCulture)
            + " · " + how + " · took " + FormatDuration(result.Duration);
        LastRunStatus = !result.Success ? HealthStatus.Critical : result.NeedsAttention ? HealthStatus.Warning : HealthStatus.Good;
        OutputPath = result.OutputPath;
    }

    public void UpdateScheduleText()
    {
        var schedule = ToSchedule();
        ScheduleText = ScheduleRules.Validate(schedule) is null ? ScheduleRules.Describe(schedule) : "Check the time";
        NextRunText = ScheduleRules.NextRun(schedule, DateTime.Now) is { } next
            ? "Next run " + next.ToString("ddd d MMM, HH:mm", CultureInfo.CurrentCulture)
            : null;
    }

    private void Edited()
    {
        if (_loading)
        {
            return;
        }

        IsDirty = true;
        ValidationError = ScheduleRules.Validate(ToSchedule());
        UpdateScheduleText();
    }

    partial void OnFrequencyChanged(ScheduleFrequency value) => Edited();
    partial void OnDayChanged(DayOfWeek value) => Edited();
    partial void OnTimeChanged(string value) => Edited();
    partial void OnDryRunChanged(bool value) => Edited();
    partial void OnKeepReportsChanged(double value) => Edited();

    partial void OnIsScheduledChanged(bool value)
    {
        if (!_loading)
        {
            _ = _onScheduledChanged(this, value);
        }
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private Task SaveAsync() => _onSave(this);

    private bool CanSave() => IsDirty && !IsWorking;

    [RelayCommand(CanExecute = nameof(CanRunNow))]
    private Task RunNowAsync() => _onRunNow(this);

    private bool CanRunNow() => !IsRunning;

    [RelayCommand(CanExecute = nameof(HasOutput))]
    private void OpenOutput() => _onOpenOutput(this);

    private static string FormatDuration(TimeSpan span) => span.TotalSeconds < 60
        ? Math.Max(1, (int)Math.Round(span.TotalSeconds)).ToString(CultureInfo.InvariantCulture) + " s"
        : ((int)span.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " min " + span.Seconds.ToString(CultureInfo.InvariantCulture) + " s";
}
