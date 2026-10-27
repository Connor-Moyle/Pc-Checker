using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Localization;
using PcChecker.App.Services;
using PcChecker.Core.Common;
using PcChecker.Core.Drivers;
using PcChecker.Core.Settings;

namespace PcChecker.App.ViewModels;

/// <summary>
/// Old drivers page: scans for superseded and unused driver packages and for devices that are no longer connected,
/// groups them by how safe they are to remove, and removes the ticked ones after a confirmation. Every package is
/// exported to a backup folder first; the service runs the change through the change guard.
/// </summary>
public sealed partial class DriverCleanupViewModel : ViewModelBase
{
    private const string DefaultSubtitle = "Driver versions left behind by updates, and devices that are no longer connected.";

    private readonly IDriverCleanupService _cleanup;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly ILogger<DriverCleanupViewModel> _logger;
    private readonly List<DriverCleanupRow> _rows = [];
    private IReadOnlyList<DriverCleanupItem> _items = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFindings))]
    [NotifyPropertyChangedFor(nameof(HasNoFindings))]
    private bool _hasScanned;

    [ObservableProperty]
    private string _subtitle = DefaultSubtitle;

    [ObservableProperty]
    private int _packagesFound;

    [ObservableProperty]
    private int _safeCount;

    [ObservableProperty]
    private string _freeableText = "—";

    [ObservableProperty]
    private string _freeableSubtitle = "Safe and probably safe packages";

    [ObservableProperty]
    private int _disconnectedCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFindings))]
    [NotifyPropertyChangedFor(nameof(HasNoFindings))]
    private int _findingCount;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveSelectedCommand))]
    private int _selectedCount;

    [ObservableProperty]
    private string _selectedText = "Nothing selected";

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

    [ObservableProperty]
    private bool _rebootRequired;

    public DriverCleanupViewModel(ISettingsService settings, IDriverCleanupService cleanup, IDialogService dialogs,
        INotificationService notifications, ILogger<DriverCleanupViewModel> logger)
        : base(settings)
    {
        _cleanup = cleanup;
        _dialogs = dialogs;
        _notifications = notifications;
        _logger = logger;
        ResultLines.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasResultLines));
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsAdvancedMode))
            {
                foreach (var row in _rows)
                {
                    row.CanSelect = DriverCleanupRules.CanSelect(row.Item, IsAdvancedMode);
                }

                UpdateSelection();
            }
        };
    }

    public ObservableCollection<DriverCleanupGroup> Groups { get; } = [];

    /// <summary>One line per step of the last removal ("Removed …" / "Not removed …").</summary>
    public ObservableCollection<string> ResultLines { get; } = [];

    public bool HasFindings => HasScanned && FindingCount > 0;

    public bool HasNoFindings => HasScanned && FindingCount == 0;

    public bool HasWarnings => !string.IsNullOrWhiteSpace(Warnings);

    public bool HasResultLines => ResultLines.Count > 0;

    public bool ShowSuccessResult => ResultSuccess && !string.IsNullOrWhiteSpace(ResultMessage);

    public bool ShowFailureResult => !ResultSuccess && !string.IsNullOrWhiteSpace(ResultMessage);

    /// <summary>Waits for the user in normal mode (the page shows one call to action); demo mode scans so the page has content.</summary>
    public override Task InitializeAsync(CancellationToken cancellationToken) =>
        AppOptions.Current.IsDemo ? RunScanAsync(cancellationToken) : Task.CompletedTask;

    public override Task RefreshAsync(CancellationToken cancellationToken) => RunScanAsync(cancellationToken);

    protected override void OnBusyError(Exception exception) => _logger.LogError(exception, "Old drivers page operation failed.");

    [RelayCommand]
    private Task ScanAsync() => RunScanAsync(CancellationToken.None);

    private Task RunScanAsync(CancellationToken outer) => RunBusyAsync(async busyToken =>
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(busyToken, outer);
        await ScanCoreAsync(linked.Token);
    }, Loc.T("Looking for old and unused drivers…"));

    private async Task ScanCoreAsync(CancellationToken ct)
    {
        var progress = new Progress<ProgressUpdate>(p => BusyText = p.Stage + (p.Detail is null ? string.Empty : " — " + p.Detail));
        var result = await _cleanup.ScanAsync(progress, ct);
        Apply(result);
    }

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private async Task RemoveSelectedAsync()
    {
        var selected = _rows.Where(r => r.IsSelected).Select(r => r.Item).ToList();
        var plan = DriverCleanupRules.BuildRemovalPlan(selected, _items, IsAdvancedMode);
        if (plan.Count == 0)
        {
            return;
        }

        bool keepSelected = selected.Any(i => i.Status == DriverCleanupStatus.Keep);
        if (!await _dialogs.ConfirmAsync(Loc.T("Remove these drivers?"), ConfirmationText(plan, keepSelected), Loc.T("Remove"), isDanger: true))
        {
            return;
        }

        await RunBusyAsync(async ct =>
        {
            var progress = new Progress<ProgressUpdate>(p => BusyText = p.Stage + (p.Detail is null ? string.Empty : " — " + p.Detail));
            var result = await _cleanup.RemoveAsync(plan, progress, ct);
            RebootRequired |= result.RebootRequired;
            ShowResult(result);
            await ScanCoreAsync(ct);
        }, Loc.T("Removing old drivers…"), cancellable: false);
    }

    private bool CanRemove() => SelectedCount > 0;

    [RelayCommand]
    private void SelectRecommended()
    {
        foreach (var row in _rows)
        {
            row.IsSelected = row.CanSelect && DriverCleanupRules.IsSelectedByDefault(row.Item);
        }
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var row in _rows)
        {
            row.IsSelected = false;
        }
    }

    [RelayCommand]
    private void OpenBackupFolder()
    {
        try
        {
            Directory.CreateDirectory(_cleanup.BackupRoot);
        }
        catch (Exception ex)
        {
            ErrorMessage = "The backup folder could not be created: " + ex.Message;
            return;
        }

        Open(_cleanup.BackupRoot);
    }

    [RelayCommand]
    private void OpenDeviceManager() => Open(DriverRules.DeviceManagerCommand);

    [RelayCommand]
    private void DismissReboot() => RebootRequired = false;

    private void Apply(DriverCleanupScanResult result)
    {
        _items = result.Items;
        _rows.Clear();
        _rows.AddRange(result.Items.Select(i => new DriverCleanupRow(i, DriverCleanupRules.CanSelect(i, IsAdvancedMode), UpdateSelection)));
        Groups.Clear();
        foreach (var group in _rows.GroupBy(r => r.Item.Status).OrderBy(g => g.Key))
        {
            Groups.Add(new DriverCleanupGroup(group.Key, group));
        }

        var s = result.Summary;
        PackagesFound = s.PackagesFound;
        SafeCount = s.SafeToRemove;
        DisconnectedCount = s.DisconnectedDevices;
        FreeableText = ByteSize.Format(s.FreeableBytes);
        FreeableSubtitle = s.SizesIncomplete ? "Some sizes could not be measured" : "Safe and probably safe packages";
        FindingCount = result.Items.Count;
        Warnings = result.Warnings.Count == 0 ? null : string.Join("\n", result.Warnings);
        Subtitle = "Checked " + result.Captured.ToString("t", CultureInfo.CurrentCulture) + ". " + DefaultSubtitle;
        HasScanned = true;
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        var selected = _rows.Where(r => r.IsSelected).Select(r => r.Item).ToList();
        SelectedCount = selected.Count;
        if (selected.Count == 0)
        {
            SelectedText = "Nothing selected";
            return;
        }

        var plan = DriverCleanupRules.BuildRemovalPlan(selected, _items, IsAdvancedMode);
        long bytes = plan.Sum(p => p.SizeBytes ?? 0);
        SelectedText = bytes > 0 ? $"{selected.Count} selected · frees about {ByteSize.Format(bytes)}" : $"{selected.Count} selected";
    }

    private string ConfirmationText(IReadOnlyList<DriverCleanupStep> plan, bool keepSelected)
    {
        var packages = plan.Where(p => p.Kind == DriverCleanupStepKind.DeletePackage).ToList();
        var devices = plan.Where(p => p.Kind == DriverCleanupStepKind.RemoveDevice).ToList();
        var text = new StringBuilder(Loc.T("PC Checker will:") + "\n");
        if (devices.Count > 0)
        {
            text.Append("• ").Append(devices.Count == 1
                ? Loc.T("make Windows forget 1 device that is not connected")
                : Loc.F("make Windows forget {0} devices that are not connected", devices.Count)).Append('\n');
        }

        if (packages.Count > 0)
        {
            long bytes = packages.Sum(p => p.SizeBytes ?? 0);
            text.Append("• ").Append(packages.Count == 1 ? Loc.T("remove 1 driver package") : Loc.F("remove {0} driver packages", packages.Count));
            text.Append(bytes > 0 ? " " + Loc.F("(about {0})", ByteSize.Format(bytes)) + "\n" : "\n");
        }

        var added = plan.Where(p => p.AddedAutomatically).Select(p => p.Title).Distinct().ToList();
        if (added.Count > 0)
        {
            text.Append('\n').Append(Loc.F("Included because they belong together: {0}.", string.Join(", ", added))).Append('\n');
        }

        text.Append('\n').Append(Loc.F("Each package is copied to {0} first, so you can reinstall it from Drivers → More → Install .inf (Advanced mode).", _cleanup.BackupRoot))
            .Append(' ').Append(Loc.T("Windows refuses to remove a package that a device still uses; nothing is forced. A restore point is created first, per your settings."));
        if (keepSelected)
        {
            text.Append("\n\n").Append(Loc.T("You selected items marked Keep. Only remove them if you are sure the hardware is gone for good."));
        }

        return text.ToString();
    }

    private void ShowResult(OperationResult<DriverCleanupRemovalResult> result)
    {
        ResultLines.Clear();
        if (result.Value is { } value)
        {
            foreach (var r in value.Results)
            {
                var verb = r.Result.Success ? "Removed" : "Not removed";
                ResultLines.Add($"{verb}: {r.Step.Title} ({r.Step.Target}). {r.Result.Message}");
            }
        }

        bool allOk = result.Success && (result.Value?.Failed ?? 0) == 0;
        ResultSuccess = allOk;
        ResultMessage = result.Message;
        if (allOk)
        {
            _notifications.Success(Loc.T("Old drivers removed"), result.Message);
        }
        else if (result.Success)
        {
            _notifications.Warning(Loc.T("Some drivers were kept"), result.Message);
        }
        else
        {
            _notifications.Error(Loc.T("Removal failed"), result.Message.Split('\n')[0]);
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
