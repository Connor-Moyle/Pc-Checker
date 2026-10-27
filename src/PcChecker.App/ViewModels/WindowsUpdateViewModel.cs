using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Converters;
using PcChecker.App.Localization;
using PcChecker.App.Services;
using PcChecker.Core.Common;
using PcChecker.Core.Settings;
using PcChecker.Core.WindowsUpdate;

namespace PcChecker.App.ViewModels;

/// <summary>
/// Windows Update page: status (last check and install, restart, pause, policy), pending software updates with
/// install through the change guard, the install history with failures explained, and the pause and active hours
/// controls. Driver updates stay on the Drivers page.
/// </summary>
public sealed partial class WindowsUpdateViewModel : ViewModelBase
{
    private const string DefaultSubtitle = "Windows and Microsoft updates for this PC, their history, and when they may install.";

    private readonly IWindowsUpdateService _updates;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly ILogger<WindowsUpdateViewModel> _logger;
    private readonly List<UpdateHistoryRow> _allHistory = [];
    private IReadOnlyList<string> _statusWarnings = [];
    private IReadOnlyList<string> _checkWarnings = [];
    private bool _installNeedsRestart;

    [ObservableProperty]
    private string _subtitle = DefaultSubtitle;

    [ObservableProperty]
    private string _pendingCountText = "—";

    [ObservableProperty]
    private string _pendingSubtitle = Loc.T("Not checked yet");

    [ObservableProperty]
    private string _lastCheckText = "—";

    [ObservableProperty]
    private string _lastInstallText = "—";

    [ObservableProperty]
    private string _lastInstallSubtitle = Loc.T("Last successful install");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdates))]
    [NotifyPropertyChangedFor(nameof(ShowNoUpdates))]
    [NotifyPropertyChangedFor(nameof(ShowNotChecked))]
    private bool _updatesChecked;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdates))]
    [NotifyPropertyChangedFor(nameof(ShowNoUpdates))]
    [NotifyPropertyChangedFor(nameof(UpdatesTabHeader))]
    private int _updateCount;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallSelectedCommand))]
    private int _selectedCount;

    [ObservableProperty]
    private string _selectedText = Loc.T("Nothing selected");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HistoryTabHeader))]
    private int _failedCount;

    [ObservableProperty]
    private bool _historyLoaded;

    [ObservableProperty]
    private bool _failedOnly;

    [ObservableProperty]
    private bool _restartPending;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPolicyNotice))]
    private string? _policyNotice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarnings))]
    private string? _warnings;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSuccessResult))]
    [NotifyPropertyChangedFor(nameof(ShowFailureResult))]
    private string? _resultMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSuccessResult))]
    [NotifyPropertyChangedFor(nameof(ShowFailureResult))]
    private bool _resultSuccess;

    public WindowsUpdateViewModel(ISettingsService settings, IWindowsUpdateService updates, IDialogService dialogs,
        INotificationService notifications, ILogger<WindowsUpdateViewModel> logger)
        : base(settings)
    {
        _updates = updates;
        _dialogs = dialogs;
        _notifications = notifications;
        _logger = logger;
        ResultLines.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasResultLines));
        WeekOptions = Enumerable.Range(1, UpdatePauseRules.MaxWeeks).Select(w => new PauseOption(w, w == 1 ? Loc.T("1 week") : Loc.F("{0} weeks", w))).ToList();
        HourOptions = Enumerable.Range(0, 24).Select(h => new HourOption(h, ActiveHoursRules.Hour(h))).ToList();
        _selectedWeeks = WeekOptions[0];
    }

    public ObservableCollection<PendingUpdateRow> Updates { get; } = [];

    public ObservableCollection<UpdateHistoryRow> History { get; } = [];

    /// <summary>One line per update of the last install run.</summary>
    public ObservableCollection<string> ResultLines { get; } = [];

    public bool HasUpdates => UpdatesChecked && UpdateCount > 0;

    public bool ShowNoUpdates => UpdatesChecked && UpdateCount == 0;

    public bool ShowNotChecked => !UpdatesChecked;

    public bool HasHistory => History.Count > 0;

    public bool HasPolicyNotice => !string.IsNullOrWhiteSpace(PolicyNotice);

    public bool HasWarnings => !string.IsNullOrWhiteSpace(Warnings);

    public bool HasResultLines => ResultLines.Count > 0;

    public bool ShowSuccessResult => ResultSuccess && !string.IsNullOrWhiteSpace(ResultMessage);

    public bool ShowFailureResult => !ResultSuccess && !string.IsNullOrWhiteSpace(ResultMessage);

    public string UpdatesTabHeader => UpdateCount == 0 ? Loc.T("Updates") : Loc.F("Updates ({0})", UpdateCount);

    public string HistoryTabHeader => FailedCount == 0 ? Loc.T("History") : Loc.F("History ({0} failed)", FailedCount);

    /// <summary>
    /// Reads the status and history (both quick and read-only). The check for updates waits for the button in normal
    /// mode because it can take a minute; demo mode checks so the page has content.
    /// </summary>
    public override Task InitializeAsync(CancellationToken cancellationToken) => RunBusyAsync(async busyToken =>
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(busyToken, cancellationToken);
        await LoadStatusAndHistoryAsync(linked.Token);
        if (AppOptions.Current.IsDemo)
        {
            await CheckCoreAsync(linked.Token);
        }
    }, "Reading Windows Update status…");

    public override Task RefreshAsync(CancellationToken cancellationToken) => RunBusyAsync(async busyToken =>
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(busyToken, cancellationToken);
        await LoadStatusAndHistoryAsync(linked.Token);
    }, "Reading Windows Update status…");

    protected override void OnBusyError(Exception exception) => _logger.LogError(exception, "Windows Update page operation failed.");

    [RelayCommand]
    private Task CheckForUpdatesAsync() => RunBusyAsync(CheckCoreAsync, "Checking Windows Update…");

    [RelayCommand]
    private Task RefreshStatusAsync() => RefreshAsync(CancellationToken.None);

    [RelayCommand(CanExecute = nameof(CanInstallSelected))]
    private Task InstallSelectedAsync() =>
        InstallAsync(Updates.Where(r => r.IsSelected && r.CanSelect).Select(r => r.Update).ToList());

    private bool CanInstallSelected() => SelectedCount > 0;

    [RelayCommand]
    private Task InstallAllAsync() => InstallAsync(UpdateInstallSummary.InstallAllSelection(Updates.Select(r => r.Update)));

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var row in Updates.Where(r => r.CanSelect && !r.Update.IsOptional))
        {
            row.IsSelected = true;
        }
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var row in Updates)
        {
            row.IsSelected = false;
        }
    }

    [RelayCommand]
    private void DismissRestart()
    {
        _installNeedsRestart = false;
        RestartPending = false;
    }

    partial void OnFailedOnlyChanged(bool value) => ApplyHistoryFilter();

    private async Task CheckCoreAsync(CancellationToken ct)
    {
        var progress = new Progress<ProgressUpdate>(p => BusyText = p.Stage + (p.Detail is null ? string.Empty : " — " + p.Detail));
        var scan = await _updates.CheckForUpdatesAsync(progress, ct);
        Updates.Clear();
        foreach (var update in scan.Updates)
        {
            Updates.Add(new PendingUpdateRow(update, UpdateSelection) { IsSelected = !update.IsOptional && !update.NeedsUserInput });
        }

        UpdateCount = Updates.Count;
        UpdatesChecked = true;
        long bytes = scan.Updates.Where(u => !u.IsDownloaded).Sum(u => u.SizeBytes);
        PendingCountText = UpdateCount.ToString(CultureInfo.CurrentCulture);
        PendingSubtitle = UpdateCount == 0 ? Loc.T("Up to date") : bytes > 0 ? Loc.F("{0} to download", ByteSize.Format(bytes)) : Loc.T("Already downloaded");
        LastCheckText = RelativeTimeConverter.Format(scan.Captured, DateTimeOffset.Now);
        Subtitle = Loc.F("Checked {0}.", scan.Captured.ToString("t", CultureInfo.CurrentCulture)) + " " + Loc.T(DefaultSubtitle);
        _checkWarnings = scan.Warnings;
        ShowWarnings();
        UpdateSelection();
    }

    private async Task LoadStatusAndHistoryAsync(CancellationToken ct)
    {
        var status = await _updates.GetStatusAsync(ct);
        var history = await _updates.GetHistoryAsync(UpdateHistoryMapper.DefaultLimit, ct);
        ApplyHistory(history);
        ApplyStatus(status, history);
        _statusWarnings = status.Warnings.Concat(history.Warnings).ToList();
        ShowWarnings();
    }

    private void ApplyStatus(WindowsUpdateStatus status, UpdateHistory history)
    {
        if (!UpdatesChecked)
        {
            LastCheckText = status.LastCheck is { } check ? RelativeTimeConverter.Format(check.ToLocalTime(), DateTimeOffset.Now) : Loc.T("Unknown");
        }

        // Windows does not always record the last install time; the history knows it.
        var lastInstall = status.LastInstall ?? UpdateHistoryMapper.LastSuccessfulInstall(history.Entries);
        LastInstallText = lastInstall is { } installed ? RelativeTimeConverter.Format(installed.ToLocalTime(), DateTimeOffset.Now) : Loc.T("Unknown");
        RestartPending = status.RestartPending || _installNeedsRestart;
        PolicyNotice = status.Policy.IsManaged ? string.Join("\n", status.Policy.Notes) : null;
        ApplyControls(status);
    }

    private void ApplyHistory(UpdateHistory history)
    {
        _allHistory.Clear();
        _allHistory.AddRange(history.Entries.Select(e => new UpdateHistoryRow(e)));
        FailedCount = _allHistory.Count(r => r.IsFailure);
        HistoryLoaded = true;
        ApplyHistoryFilter();
    }

    private void ApplyHistoryFilter()
    {
        History.Clear();
        foreach (var row in FailedOnly ? _allHistory.Where(r => r.IsFailure) : _allHistory)
        {
            History.Add(row);
        }

        OnPropertyChanged(nameof(HasHistory));
    }

    private async Task InstallAsync(IReadOnlyList<PendingUpdate> selected)
    {
        if (selected.Count == 0)
        {
            _notifications.Warning("Nothing to install", "Tick the updates you want to install first.");
            return;
        }

        if (!await _dialogs.ConfirmAsync("Install Windows updates?", ConfirmationText(selected), "Install"))
        {
            return;
        }

        await RunBusyAsync(async ct =>
        {
            var progress = new Progress<ProgressUpdate>(p => BusyText = p.Stage + (p.Detail is null ? string.Empty : " — " + p.Detail));
            var result = await _updates.InstallAsync(selected, progress, ct);
            ShowResult(result);
            if (result.Value is { } value)
            {
                foreach (var item in value.Items)
                {
                    Updates.FirstOrDefault(r => r.Update.Id == item.Update.Id)?.MarkResult(item);
                }

                foreach (var row in Updates.Where(r => value.Items.Any(i => i.Success && i.Update.Id == r.Update.Id)).ToList())
                {
                    Updates.Remove(row);
                }

                UpdateCount = Updates.Count;
                PendingCountText = UpdateCount.ToString(CultureInfo.CurrentCulture);
                _installNeedsRestart |= value.RebootRequired;
                RestartPending |= value.RebootRequired;
                UpdateSelection();
            }

            await LoadStatusAndHistoryAsync(CancellationToken.None);
        }, "Installing Windows updates… Cancel stops after the current update.");
    }

    private static string ConfirmationText(IReadOnlyList<PendingUpdate> selected)
    {
        var text = new StringBuilder($"PC Checker will download (if needed) and install {(selected.Count == 1 ? "this update" : $"these {selected.Count} updates")} from Windows Update, one at a time:\n\n");
        foreach (var u in selected.Take(8))
        {
            text.Append("• ").Append(u.Title).Append('\n');
        }

        if (selected.Count > 8)
        {
            text.Append($"• and {selected.Count - 8} more\n");
        }

        long bytes = selected.Where(u => !u.IsDownloaded).Sum(u => u.SizeBytes);
        if (bytes > 0)
        {
            text.Append($"\nUp to {ByteSize.Format(bytes)} will be downloaded.");
        }

        text.Append("\nA restore point is created first (per your settings). Installing accepts Microsoft's licence terms for these updates, as Windows Update does.");
        if (selected.Any(u => u.Restart == UpdateRestartNeed.Yes))
        {
            text.Append("\n\nWindows will need a restart to finish. PC Checker does not restart on its own.");
        }

        if (selected.Any(u => u.Classification == UpdateClassification.Feature))
        {
            text.Append("\n\nA new Windows version takes a long time to install and several restarts to finish.");
        }

        return text.ToString();
    }

    private void ShowResult(OperationResult<UpdateInstallResult> result)
    {
        ResultLines.Clear();
        if (result.Value is { } value)
        {
            foreach (var line in UpdateInstallSummary.Lines(value))
            {
                ResultLines.Add(line);
            }
        }

        bool allOk = result.Success && (result.Value?.Failed ?? 0) == 0;
        ResultSuccess = allOk;
        ResultMessage = result.Message;
        if (allOk)
        {
            _notifications.Success("Updates installed", result.Message);
        }
        else if (result.Success)
        {
            _notifications.Warning("Some updates did not install", result.Message);
        }
        else
        {
            _notifications.Error("Updates not installed", result.Message.Split('\n')[0]);
        }
    }

    private void UpdateSelection()
    {
        var selected = Updates.Where(r => r.IsSelected && r.CanSelect).ToList();
        SelectedCount = selected.Count;
        long bytes = selected.Where(r => !r.Update.IsDownloaded).Sum(r => r.Update.SizeBytes);
        SelectedText = selected.Count == 0
            ? Loc.T("Nothing selected")
            : bytes > 0 ? Loc.F("{0} selected · {1} to download", selected.Count, ByteSize.Format(bytes)) : Loc.F("{0} selected", selected.Count);
    }

    private void ShowWarnings()
    {
        var list = _statusWarnings.Concat(_checkWarnings).Where(w => !string.IsNullOrWhiteSpace(w)).Distinct().ToList();
        Warnings = list.Count == 0 ? null : string.Join("\n", list);
    }
}
