using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcChecker.App.Localization;
using PcChecker.Core.Common;
using PcChecker.Core.WindowsUpdate;

namespace PcChecker.App.ViewModels;

/// <summary>
/// The controls half of the Windows Update page: pausing and resuming updates, active hours, and the links to the
/// matching pages in the Settings app. Settings managed by policy are shown read-only.
/// </summary>
public sealed partial class WindowsUpdateViewModel
{
    public const string SettingsUri = "ms-settings:windowsupdate";
    public const string HistorySettingsUri = "ms-settings:windowsupdate-history";
    public const string AdvancedOptionsUri = "ms-settings:windowsupdate-options";
    public const string ActiveHoursUri = "ms-settings:windowsupdate-activehours";

    private UpdatePauseState _pause = UpdatePauseState.NotPaused;
    private int _maxPauseDays = UpdatePauseRules.DefaultMaxPauseDays;
    private int _activeHoursMaxSpan = ActiveHoursRules.MaxSpanHours;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PauseButtonText))]
    [NotifyCanExecuteChangedFor(nameof(ResumeCommand))]
    private bool _isPaused;

    [ObservableProperty]
    private string _pauseText = "—";

    [ObservableProperty]
    private string _pauseSubtitle = Loc.T("Updates install as usual");

    [ObservableProperty]
    private string _pauseStatusText = Loc.T("Updates are not paused.");

    [ObservableProperty]
    private PauseOption _selectedWeeks;

    [ObservableProperty]
    private IReadOnlyList<PauseOption> _availableWeeks = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PauseCommand))]
    private bool _canPause;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PauseControlsEnabled))]
    [NotifyCanExecuteChangedFor(nameof(PauseCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResumeCommand))]
    private bool _pauseManaged;

    [ObservableProperty]
    private HourOption? _activeStart;

    [ObservableProperty]
    private HourOption? _activeEnd;

    [ObservableProperty]
    private string _activeHoursText = "—";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveHoursProblem))]
    [NotifyCanExecuteChangedFor(nameof(SaveActiveHoursCommand))]
    private string? _activeHoursProblem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveHoursEnabled))]
    [NotifyCanExecuteChangedFor(nameof(SaveActiveHoursCommand))]
    private bool _activeHoursManaged;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveActiveHoursCommand))]
    private bool _activeHoursChanged;

    /// <summary>Windows picks active hours from usage; they are then changed in Settings, not here.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveHoursEnabled))]
    [NotifyCanExecuteChangedFor(nameof(SaveActiveHoursCommand))]
    private bool _activeHoursAutomatic;

    [ObservableProperty]
    private string _activeHoursHelp = Loc.F("Up to {0} hours, and they may run past midnight.", ActiveHoursRules.MaxSpanHours);

    public IReadOnlyList<PauseOption> WeekOptions { get; }

    public IReadOnlyList<HourOption> HourOptions { get; }

    public string PauseButtonText => IsPaused ? Loc.T("Extend pause") : Loc.T("Pause updates");

    public bool PauseControlsEnabled => !PauseManaged;

    public bool ActiveHoursEnabled => !ActiveHoursManaged && !ActiveHoursAutomatic;

    public bool HasActiveHoursProblem => !string.IsNullOrWhiteSpace(ActiveHoursProblem);

    [RelayCommand(CanExecute = nameof(CanRunPause))]
    private async Task PauseAsync()
    {
        int weeks = SelectedWeeks.Weeks;
        var plan = UpdatePauseRules.Plan(_pause, DateTimeOffset.UtcNow, weeks, _maxPauseDays);
        if (!plan.Allowed)
        {
            await _dialogs.ShowMessageAsync("Updates cannot be paused longer", plan.Message);
            return;
        }

        var until = plan.Expiry.ToLocalTime().ToString("D", CultureInfo.CurrentCulture);
        var message = (plan.IsExtension ? $"Updates will stay paused until {until}." : $"Windows will not install updates until {until}.")
            + (plan.WasShortened ? " That is as long as Windows allows a pause to last." : string.Empty)
            + "\n\nSecurity fixes wait too, so resume as soon as you can. When the pause ends, Windows installs the latest updates before it can be paused again."
            + " PC Checker sets the same pause as Settings → Windows Update.";
        if (!await _dialogs.ConfirmAsync(plan.IsExtension ? "Extend the pause?" : "Pause updates?", message, plan.IsExtension ? "Extend" : "Pause"))
        {
            return;
        }

        await RunSettingChangeAsync(ct => _updates.PauseAsync(weeks, ct), "Pausing updates…", SettingsUri);
    }

    private bool CanRunPause() => CanPause && !PauseManaged;

    [RelayCommand(CanExecute = nameof(CanResume))]
    private async Task ResumeAsync()
    {
        if (!await _dialogs.ConfirmAsync("Resume updates?", "Windows will look for updates again and install them as usual. A restart may follow outside your active hours.", "Resume"))
        {
            return;
        }

        await RunSettingChangeAsync(ct => _updates.ResumeAsync(ct), "Resuming updates…", SettingsUri);
    }

    private bool CanResume() => IsPaused && !PauseManaged;

    [RelayCommand(CanExecute = nameof(CanSaveActiveHours))]
    private async Task SaveActiveHoursAsync()
    {
        if (ActiveStart is not { } start || ActiveEnd is not { } end)
        {
            return;
        }

        var hours = new ActiveHours(start.Hour, end.Hour, false);
        if (!await _dialogs.ConfirmAsync("Change active hours?",
                $"Windows will not restart the PC for updates between {ActiveHoursRules.Describe(hours)}. PC Checker sets the same values as Settings → Windows Update → Advanced options.",
                "Save"))
        {
            return;
        }

        await RunSettingChangeAsync(ct => _updates.SetActiveHoursAsync(start.Hour, end.Hour, ct), "Saving active hours…", AdvancedOptionsUri);
    }

    private bool CanSaveActiveHours() => ActiveHoursChanged && !ActiveHoursManaged && !ActiveHoursAutomatic && !HasActiveHoursProblem;

    [RelayCommand]
    private void OpenSettings() => Open(SettingsUri);

    [RelayCommand]
    private void OpenHistorySettings() => Open(HistorySettingsUri);

    [RelayCommand]
    private void OpenAdvancedOptions() => Open(AdvancedOptionsUri);

    [RelayCommand]
    private void OpenActiveHoursSettings() => Open(ActiveHoursUri);

    partial void OnActiveStartChanged(HourOption? value) => ValidateActiveHours();

    partial void OnActiveEndChanged(HourOption? value) => ValidateActiveHours();

    private void ApplyControls(WindowsUpdateStatus status)
    {
        _pause = status.Pause;
        _maxPauseDays = status.MaxPauseDays;
        IsPaused = status.Pause.IsPaused;
        PauseManaged = status.Policy.PauseManaged;
        if (status.Pause is { IsPaused: true, PausedUntil: { } until })
        {
            var local = until.ToLocalTime();
            PauseText = Loc.F("Until {0}", local.ToString("m", CultureInfo.CurrentCulture));
            PauseSubtitle = Loc.T("Updates are paused");
            PauseStatusText = Loc.F("Updates are paused until {0}.", local.ToString("D", CultureInfo.CurrentCulture));
        }
        else
        {
            PauseText = Loc.T("Not paused");
            PauseSubtitle = Loc.T("Updates install as usual");
            PauseStatusText = Loc.T("Updates are not paused.");
        }

        int weeks = UpdatePauseRules.WeeksAvailable(status.Pause, DateTimeOffset.UtcNow, status.MaxPauseDays);
        AvailableWeeks = WeekOptions.Take(weeks).ToList();
        CanPause = weeks > 0;
        if (weeks > 0 && SelectedWeeks.Weeks > weeks)
        {
            SelectedWeeks = AvailableWeeks[^1];
        }

        if (UpdatePauseRules.HasUnknownStart(status.Pause, DateTimeOffset.UtcNow))
        {
            PauseStatusText += " " + Loc.T("To extend this pause, use Settings → Windows Update.");
        }
        else if (weeks == 0 && IsPaused)
        {
            PauseStatusText += " " + Loc.T("That is as long as Windows allows.");
        }

        ActiveHoursManaged = status.Policy.ActiveHoursManaged;
        _activeHoursMaxSpan = ActiveHoursRules.EffectiveMaxSpan(status.Policy.ActiveHoursMaxSpan);
        ActiveHoursHelp = _activeHoursMaxSpan < ActiveHoursRules.MaxSpanHours
            ? Loc.F("Up to {0} hours (set by a policy on this PC), and they may run past midnight.", _activeHoursMaxSpan)
            : Loc.F("Up to {0} hours, and they may run past midnight.", _activeHoursMaxSpan);
        ActiveHoursAutomatic = status.ActiveHours?.IsAutomatic == true;
        if (status.ActiveHours is { } hours)
        {
            ActiveHoursText = ActiveHoursRules.Describe(hours);
            ActiveStart = HourOptions[hours.StartHour];
            ActiveEnd = HourOptions[hours.EndHour];
        }
        else
        {
            ActiveHoursText = Loc.T("Unknown");
        }

        ActiveHoursChanged = false;
    }

    private void ValidateActiveHours()
    {
        if (ActiveStart is not { } start || ActiveEnd is not { } end)
        {
            ActiveHoursProblem = null;
            return;
        }

        ActiveHoursProblem = ActiveHoursRules.Validate(start.Hour, end.Hour, _activeHoursMaxSpan);
        ActiveHoursChanged = true;
    }

    /// <summary>
    /// Runs one setting change, shows its result and reloads the status. When it failed, offers to open
    /// <paramref name="settingsUri"/> (the matching page in Settings) to make the change there instead.
    /// </summary>
    private async Task RunSettingChangeAsync(Func<CancellationToken, Task<OperationResult>> change, string busyText, string settingsUri)
    {
        OperationResult? result = null;
        await RunBusyAsync(async ct =>
        {
            result = await change(ct);
            ResultLines.Clear();
            ResultSuccess = result.Success;
            ResultMessage = result.Message;
            await LoadStatusAndHistoryAsync(CancellationToken.None);
        }, busyText, cancellable: false);

        if (result is { Success: false } failed
            && await _dialogs.ConfirmAsync("Open Windows Update settings?", failed.Message + "\n\nYou can make this change in Settings → Windows Update.", "Open Settings"))
        {
            Open(settingsUri);
        }
    }

    private void Open(string target)
    {
        var result = ShellLauncher.Open(target);
        if (!result.Success)
        {
            ErrorMessage = result.Message;
        }
    }
}
