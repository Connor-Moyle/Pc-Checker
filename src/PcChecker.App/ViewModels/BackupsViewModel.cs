using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Converters;
using PcChecker.App.Localization;
using PcChecker.App.Services;
using PcChecker.Core.Backups;
using PcChecker.Core.Common;
using PcChecker.Core.Restore;
using PcChecker.Core.Settings;

namespace PcChecker.App.ViewModels;

/// <summary>
/// Backups page (Security): System Restore points with create and delete, System Protection and its space per drive,
/// the state of Windows' own backups (read only, with links), and PC Checker's own backup folders with "delete old
/// ones". Every change asks first and runs through the backups service, which guards and journals it.
/// </summary>
public sealed partial class BackupsViewModel : ViewModelBase, IRefreshablePage
{
    public const string DefaultDescription = "Manual restore point (PC Checker)";
    private const string DefaultSubtitle = "Restore points, System Protection and every backup on this PC in one place.";

    private readonly IBackupsService _backups;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly ILogger<BackupsViewModel> _logger;
    private RestorePointOverview? _overview;
    private IReadOnlyList<WindowsBackupItem> _windowsBackups = [];
    private IReadOnlyList<AppBackupSummary> _appBackups = [];

    [ObservableProperty]
    private bool _hasLoaded;

    [ObservableProperty]
    private string _subtitle = DefaultSubtitle;

    [ObservableProperty]
    private string _pointCountText = "—";

    [ObservableProperty]
    private string _pointCountSubtitle = string.Empty;

    [ObservableProperty]
    private string _newestText = "—";

    [ObservableProperty]
    private string _newestSubtitle = string.Empty;

    [ObservableProperty]
    private string _protectionText = "—";

    [ObservableProperty]
    private string _protectionSubtitle = string.Empty;

    [ObservableProperty]
    private string _spaceText = "—";

    [ObservableProperty]
    private string _spaceSubtitle = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedPointCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyPointCommand))]
    private RestorePointRow? _selectedPoint;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateRestorePointCommand))]
    private string _newDescription = DefaultDescription;

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
    private string _systemImageText = "Not checked yet. Checking can take a minute.";

    [ObservableProperty]
    private double _keepNewest = AppBackupCatalog.DefaultKept;

    [ObservableProperty]
    private bool _allowMoreThanOnePerDay;

    public BackupsViewModel(ISettingsService settings, IBackupsService backups, IDialogService dialogs, INotificationService notifications,
        ILogger<BackupsViewModel> logger)
        : base(settings)
    {
        _backups = backups;
        _dialogs = dialogs;
        _notifications = notifications;
        _logger = logger;
        _allowMoreThanOnePerDay = settings.Current.RestorePoints.BypassFrequencyLimit;
        Points.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasPoints));
        Hints.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasHints));
        Drives.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasDrives));
    }

    public ObservableCollection<BackupHintRow> Hints { get; } = [];

    public ObservableCollection<RestorePointRow> Points { get; } = [];

    public ObservableCollection<DriveProtectionRow> Drives { get; } = [];

    public ObservableCollection<WindowsBackupRow> WindowsBackups { get; } = [];

    public ObservableCollection<AppBackupRow> AppBackups { get; } = [];

    public bool HasHints => Hints.Count > 0;

    public bool HasPoints => Points.Count > 0;

    public bool HasDrives => Drives.Count > 0;

    public bool HasWarnings => !string.IsNullOrWhiteSpace(Warnings);

    public bool ShowSuccessResult => ResultSuccess && !string.IsNullOrWhiteSpace(ResultMessage);

    public bool ShowFailureResult => !ResultSuccess && !string.IsNullOrWhiteSpace(ResultMessage);

    /// <summary>What the empty restore point list says, depending on whether System Protection is on.</summary>
    public string PointsEmptyText => _overview?.SystemDrive?.IsProtected == false
        ? Loc.T("No restore points: System Protection is off for the Windows drive. Turn it on below, then create one.")
        : Loc.T("No restore points yet. Press Create restore point to make the first one.");

    ICommand IRefreshablePage.RefreshKeyCommand => RefreshNowCommand;

    /// <summary>The page only reads, so it fills itself in every mode.</summary>
    public override Task InitializeAsync(CancellationToken cancellationToken) => LoadWithBusyAsync(cancellationToken);

    public override Task RefreshAsync(CancellationToken cancellationToken) => LoadWithBusyAsync(cancellationToken);

    protected override void OnBusyError(Exception exception) => _logger.LogError(exception, "Backups page operation failed.");

    [RelayCommand]
    private Task RefreshNowAsync() => LoadWithBusyAsync(CancellationToken.None);

    private Task LoadWithBusyAsync(CancellationToken outer) => RunBusyAsync(async busyToken =>
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(busyToken, outer);
        await LoadAsync(linked.Token);
    }, Loc.T("Reading restore points and backups…"));

    private async Task LoadAsync(CancellationToken ct)
    {
        var overviewTask = _backups.GetRestorePointsAsync(ct);
        var windowsTask = _backups.GetWindowsBackupsAsync(ct);
        var appTask = _backups.GetAppBackupsAsync(ct);
        _overview = await overviewTask;
        _windowsBackups = await windowsTask;
        _appBackups = await appTask;
        Apply();
    }

    private void Apply()
    {
        if (_overview is not { } overview)
        {
            return;
        }

        var now = DateTimeOffset.Now;
        var previous = SelectedPoint?.SequenceNumber;
        Points.Clear();
        var newest = overview.Newest;
        foreach (var point in overview.Points.OrderByDescending(p => p.SequenceNumber))
        {
            Points.Add(new RestorePointRow(point, newest?.SequenceNumber == point.SequenceNumber, now));
        }

        SelectedPoint = Points.FirstOrDefault(p => p.SequenceNumber == previous);
        OnPropertyChanged(nameof(PointsEmptyText));

        Drives.Clear();
        foreach (var drive in overview.Drives)
        {
            Drives.Add(new DriveProtectionRow(drive, TurnOnAsync, TurnOffAsync, ApplySpaceAsync));
        }

        WindowsBackups.Clear();
        foreach (var item in _windowsBackups)
        {
            WindowsBackups.Add(new WindowsBackupRow(item, OpenWindowsBackup));
        }

        RebuildAppBackups();
        ApplyTiles(overview, now);
        RebuildHints(overview, now);

        Warnings = overview.Warnings.Count == 0 ? null : string.Join("\n", overview.Warnings.Select(Loc.T));
        Subtitle = Loc.F("Checked {0}. {1}", overview.Captured.ToString("t", CultureInfo.CurrentCulture), Loc.T(DefaultSubtitle));
        HasLoaded = true;
    }

    private void ApplyTiles(RestorePointOverview overview, DateTimeOffset now)
    {
        PointCountText = overview.PointsKnown ? overview.Points.Count.ToString(CultureInfo.CurrentCulture) : "—";
        PointCountSubtitle = overview.PointsKnown ? Loc.T("System Restore points") : Loc.T("Windows did not list them");

        if (overview.Newest is { } newest && newest.Created != DateTimeOffset.MinValue)
        {
            NewestText = RelativeTimeConverter.Format(newest.Created, now);
            NewestSubtitle = newest.Description;
        }
        else
        {
            NewestText = Loc.T("None");
            NewestSubtitle = Loc.T("No restore point yet");
        }

        var system = overview.SystemDrive;
        ProtectionText = system?.IsProtected switch
        {
            true => Loc.T("On"),
            false => Loc.T("Off"),
            _ => Loc.T("Unknown"),
        };
        ProtectionSubtitle = system is null
            ? Loc.T("The Windows drive was not found")
            : overview.TurnedOffByPolicy ? Loc.T("Turned off by a policy") : Loc.F("System Protection on {0}", system.Drive);

        if (system?.Storage is { } storage)
        {
            SpaceText = ByteSize.Format(storage.UsedBytes);
            SpaceSubtitle = storage.IsUnbounded
                ? Loc.T("used, no limit")
                : Loc.F("used of up to {0}", ByteSize.Format(storage.MaxBytes));
        }
        else
        {
            SpaceText = "—";
            SpaceSubtitle = Loc.T("Restore point space on the Windows drive");
        }
    }

    private void RebuildHints(RestorePointOverview overview, DateTimeOffset now)
    {
        var system = overview.SystemDrive;
        var facts = new BackupHealthFacts
        {
            Now = now,
            SystemDrive = system?.Drive ?? "C:",
            SystemDriveProtected = overview.TurnedOffByPolicy ? false : system?.IsProtected,
            PointsKnown = overview.PointsKnown,
            NewestPoint = overview.Newest?.Created,
            SystemDriveStorage = system?.Storage,
            FileHistory = _windowsBackups.FirstOrDefault(w => w.Id == "file-history")?.State ?? BackupFeatureState.Unknown,
            OneDriveFolders = _windowsBackups.FirstOrDefault(w => w.Id == "onedrive")?.State ?? BackupFeatureState.Unknown,
        };

        Hints.Clear();
        foreach (var hint in BackupHealthRules.Evaluate(facts))
        {
            Hints.Add(new BackupHintRow(
                hint.Id,
                hint.Level switch
                {
                    BackupHintLevel.Critical => Severity.Critical,
                    BackupHintLevel.Warning => Severity.Medium,
                    _ => Severity.Info,
                },
                Loc.F(hint.Title, hint.Args.ToArray()),
                Loc.T(hint.Detail),
                Loc.T(hint.ActionText),
                new AsyncRelayCommand(() => RunHintAsync(hint))));
        }
    }

    private void RebuildAppBackups()
    {
        int keep = KeepCount();
        AppBackups.Clear();
        foreach (var summary in _appBackups)
        {
            AppBackups.Add(new AppBackupRow(summary, keep, OpenBackupFolder, DeleteOldBackupsAsync));
        }
    }

    partial void OnKeepNewestChanged(double value) => RebuildAppBackups();

    partial void OnAllowMoreThanOnePerDayChanged(bool value)
    {
        if (Settings.Current.RestorePoints.BypassFrequencyLimit == value)
        {
            return;
        }

        Settings.Current.RestorePoints.BypassFrequencyLimit = value;
        _ = SaveSettingsAsync();
    }

    private async Task SaveSettingsAsync()
    {
        try
        {
            await Settings.SaveAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save the restore point setting.");
            ErrorMessage = Loc.T("The setting could not be saved.");
        }
    }

    private int KeepCount() => Math.Max(AppBackupCatalog.MinimumKept, (int)Math.Round(double.IsNaN(KeepNewest) ? AppBackupCatalog.DefaultKept : KeepNewest));

    private async Task RunHintAsync(BackupHint hint)
    {
        var system = Drives.FirstOrDefault(d => d.Drive.IsSystemDrive);
        switch (hint.Action)
        {
            case BackupHintAction.TurnOnProtection when system is not null:
                await TurnOnAsync(system);
                break;
            case BackupHintAction.CreateRestorePoint:
                await CreateRestorePointAsync();
                break;
            case BackupHintAction.SetMaxSpace when system is not null:
                if (system.MaxPercent < SystemProtectionRules.SuggestedPercent)
                {
                    system.MaxPercent = SystemProtectionRules.SuggestedPercent;
                }

                await ApplySpaceAsync(system);
                break;
            case BackupHintAction.OpenWindowsBackup:
                Open(WindowsBackupRules.WindowsBackupUri);
                break;
        }
    }

    private void ShowResult(OperationResult result)
    {
        ResultSuccess = result.Success;
        ResultMessage = Sentence(result.Message);
    }

    private static string Sentence(string message) =>
        string.IsNullOrWhiteSpace(message) || message.EndsWith('.') || message.EndsWith('!') || message.EndsWith(')') ? message : message + ".";

    // ---- Restore points -----------------------------------------------------------------------------------------------

    private bool CanCreate() => !string.IsNullOrWhiteSpace(NewDescription);

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateRestorePointAsync()
    {
        var description = NewDescription.Trim();
        var message = Loc.F("Windows saves a snapshot of system files, drivers, programs and settings named \"{0}\". Personal files are not included. It takes about a minute.", description);
        if (_overview?.Newest is { } newest && DateTimeOffset.Now - newest.Created < TimeSpan.FromHours(24) && !AllowMoreThanOnePerDay)
        {
            message += "\n\n" + Loc.T("A restore point was made in the last 24 hours, and Windows normally allows only one a day. Turn on \"Allow more than one restore point a day\" under Options if Windows skips it.");
        }

        if (!await _dialogs.ConfirmAsync(Loc.T("Create a restore point?"), message, Loc.T("Create")))
        {
            return;
        }

        await RunBusyAsync(async ct =>
        {
            var result = await _backups.CreateRestorePointAsync(description, ct);
            ShowResult(result.AsUntyped());
            if (result.Success)
            {
                _notifications.Success(Loc.T("Restore point created"), result.Value?.Description ?? description);
                NewDescription = DefaultDescription;
            }

            await LoadAsync(ct);
        }, Loc.T("Creating a restore point… this can take a minute"), cancellable: false);
    }

    private bool HasSelectedPoint() => SelectedPoint is not null;

    [RelayCommand(CanExecute = nameof(HasSelectedPoint))]
    private async Task DeleteSelectedPointAsync()
    {
        if (SelectedPoint is not { } row)
        {
            return;
        }

        if (!await _dialogs.ConfirmAsync(Loc.T("Delete this restore point?"),
                Loc.F("\"{0}\" from {1} is deleted for good. You can no longer go back to it with System Restore.", row.Description, row.CreatedText),
                Loc.T("Delete"), isDanger: true))
        {
            return;
        }

        if (row.IsNewest)
        {
            bool onlyOne = Points.Count == 1;
            var warning = onlyOne
                ? Loc.T("This is your only restore point. Without it, System Restore has nothing to go back to until Windows or you make a new one.")
                : Loc.T("This is your newest restore point, the one closest to how the PC is now. Older points take you further back.");
            if (!await _dialogs.ConfirmAsync(Loc.T("Delete the newest restore point?"), warning, Loc.T("Delete anyway"), isDanger: true))
            {
                return;
            }
        }

        await RunBusyAsync(async ct =>
        {
            var result = await _backups.DeleteRestorePointAsync(row.Point, ct);
            ShowResult(result);
            if (result.Success)
            {
                _notifications.Success(Loc.T("Restore point deleted"), row.Description);
            }

            await LoadAsync(ct);
        }, Loc.T("Deleting the restore point…"), cancellable: false);
    }

    [RelayCommand]
    private async Task DeleteAllButNewestAsync()
    {
        if (Points.Count <= 1)
        {
            await _dialogs.ShowMessageAsync(Loc.T("Nothing to delete"), Loc.T("There is at most one restore point, and the newest one always stays."));
            return;
        }

        var newest = Points.First(p => p.IsNewest);
        if (!await _dialogs.ConfirmAsync(Loc.T("Delete older restore points?"),
                Loc.F("{0} older restore points are deleted for good. Only the newest, \"{1}\" from {2}, stays, so System Restore can only go back to that one.",
                    Points.Count - 1, newest.Description, newest.CreatedText),
                Loc.T("Delete older points"), isDanger: true))
        {
            return;
        }

        await RunBusyAsync(async ct =>
        {
            var progress = new Progress<ProgressUpdate>(p => BusyText = p.Stage + (p.Detail is null ? string.Empty : " — " + p.Detail));
            var result = await _backups.DeleteAllButNewestAsync(progress, ct);
            ShowResult(result);
            if (result.Success)
            {
                _notifications.Success(Loc.T("Older restore points deleted"), result.Message);
            }

            await LoadAsync(ct);
        }, Loc.T("Deleting older restore points…"), cancellable: false);
    }

    [RelayCommand(CanExecute = nameof(HasSelectedPoint))]
    private void CopyPoint()
    {
        if (SelectedPoint is not { } row)
        {
            return;
        }

        try
        {
            Clipboard.SetText(row.CopyText());
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not copy the restore point.");
        }
    }

    [RelayCommand]
    private void OpenSystemRestore() => Open("rstrui.exe");

    [RelayCommand]
    private void OpenSystemProtection() => Open("SystemPropertiesProtection.exe");

    // ---- System Protection per drive ----------------------------------------------------------------------------------

    private async Task TurnOnAsync(DriveProtectionRow row)
    {
        if (!await _dialogs.ConfirmAsync(Loc.F("Turn on System Protection for {0}?", row.Letter),
                Loc.F("Windows will keep restore points for {0}. They use a few percent of the drive (you can change how much below) and let you undo driver, program and settings changes.", row.Letter),
                Loc.T("Turn on")))
        {
            return;
        }

        await RunBusyAsync(async ct =>
        {
            var letter = row.Letter;
            var result = await UndoOffers.RunAsync(
                () => _backups.TurnOnProtectionAsync(letter, ct),
                undoCt => _backups.TurnOffProtectionAsync(letter, undoCt),
                Loc.F("Turn on System Protection for {0}", letter));
            ShowResult(result);
            if (result.Success)
            {
                _notifications.Success(Loc.T("System Protection is on"), Sentence(result.Message));
            }

            await LoadAsync(ct);
        }, Loc.T("Turning on System Protection…"), cancellable: false);
    }

    private async Task TurnOffAsync(DriveProtectionRow row)
    {
        var extra = row.Drive.IsSystemDrive
            ? "\n\n" + Loc.T("This is the Windows drive: PC Checker and Windows can no longer make restore points before changes.")
            : string.Empty;
        if (!await _dialogs.ConfirmAsync(Loc.F("Turn off System Protection for {0}?", row.Letter),
                Loc.F("Windows deletes every restore point on {0} and stops making new ones. This cannot be undone.", row.Letter) + extra,
                Loc.T("Turn off"), isDanger: true))
        {
            return;
        }

        await RunBusyAsync(async ct =>
        {
            var result = await _backups.TurnOffProtectionAsync(row.Letter, ct);
            ShowResult(result);
            if (result.Success)
            {
                _notifications.Success(Loc.T("System Protection is off"), Sentence(result.Message));
            }

            await LoadAsync(ct);
        }, Loc.T("Turning off System Protection…"), cancellable: false);
    }

    private async Task ApplySpaceAsync(DriveProtectionRow row)
    {
        int percent = (int)Math.Clamp(Math.Round(row.MaxPercent), SystemProtectionRules.MinPercent, SystemProtectionRules.MaxPercent);
        var size = ShadowStorageParser.BytesForPercent(row.Drive.CapacityBytes, percent);
        var message = size is { } bytes
            ? Loc.F("Restore points on {0} may use up to {1} % of the drive (about {2}).", row.Letter, percent, ByteSize.Format(bytes))
            : Loc.F("Restore points on {0} may use up to {1} % of the drive.", row.Letter, percent);
        if (row.Drive.Storage is { } current && size is { } newSize && newSize < current.UsedBytes)
        {
            message += "\n\n" + Loc.T("That is less than they use now, so Windows deletes the oldest restore points until they fit.");
        }

        if (!await _dialogs.ConfirmAsync(Loc.F("Change the space for restore points on {0}?", row.Letter), message, Loc.T("Change")))
        {
            return;
        }

        var previous = row.Drive.Storage;
        await RunBusyAsync(async ct =>
        {
            var letter = row.Letter;
            var result = previous is null
                ? await _backups.SetMaxSpaceAsync(letter, percent, ct)
                : await UndoOffers.RunAsync(
                    () => _backups.SetMaxSpaceAsync(letter, percent, ct),
                    undoCt => _backups.RestoreMaxSpaceAsync(letter, previous, undoCt),
                    Loc.F("Change the space for restore points on {0}", letter));
            ShowResult(result);
            if (result.Success)
            {
                _notifications.Success(Loc.T("Restore point space changed"), Sentence(result.Message));
            }

            await LoadAsync(ct);
        }, Loc.T("Changing the space for restore points…"), cancellable: false);
    }

    // ---- Windows backups ----------------------------------------------------------------------------------------------

    private void OpenWindowsBackup(WindowsBackupRow row) => Open(row.Item.OpenTarget, row.Item.OpenArguments);

    [RelayCommand]
    private void OpenFileHistory() => Open(WindowsBackupRules.FileHistoryCommand, WindowsBackupRules.FileHistoryArguments);

    [RelayCommand]
    private void OpenWindowsBackupApp() => Open(WindowsBackupRules.WindowsBackupUri);

    [RelayCommand]
    private void OpenBackupAndRestore() => Open(WindowsBackupRules.FileHistoryCommand, WindowsBackupRules.BackupAndRestoreArguments);

    [RelayCommand]
    private Task CheckSystemImagesAsync() => RunBusyAsync(async ct =>
    {
        var status = await _backups.CheckSystemImagesAsync(ct);
        SystemImageText = Loc.T(status.Message);
    }, Loc.T("Looking for system image backups… this can take a minute"));

    // ---- PC Checker's own backups -------------------------------------------------------------------------------------

    private void OpenBackupFolder(AppBackupRow row)
    {
        if (AppOptions.Current.IsDemo)
        {
            _notifications.Info(Loc.T("Demo mode"), Loc.F("In normal mode this opens {0}.", row.Folder));
            return;
        }

        var location = row.Location;
        var folder = location.Kind == BackupItemKind.SingleFile ? Path.GetDirectoryName(location.Folder) : location.Folder;
        if (string.IsNullOrWhiteSpace(folder) || (PlatformGuard.IsWindows && !Directory.Exists(folder)))
        {
            _notifications.Info(Loc.T("No backups yet"), Loc.F("{0} has not saved anything yet, so its folder does not exist.", row.Name));
            return;
        }

        Open(folder);
    }

    private async Task DeleteOldBackupsAsync(AppBackupRow row)
    {
        int keep = KeepCount();
        var toDelete = AppBackupCatalog.SelectToDelete(row.Summary.Items, keep);
        if (toDelete.Count == 0)
        {
            return;
        }

        var oldest = toDelete.Max(i => i.Created);
        if (!await _dialogs.ConfirmAsync(Loc.F("Delete old {0}?", row.Name.ToLower(CultureInfo.CurrentCulture)),
                Loc.F("{0} backups ({1}) from {2} and older go to the Recycle Bin. The newest {3} stay.",
                    toDelete.Count, ByteSize.Format(toDelete.Sum(i => i.SizeBytes)), oldest.LocalDateTime.ToString("d", CultureInfo.CurrentCulture), keep),
                Loc.T("Delete"), isDanger: true))
        {
            return;
        }

        await RunBusyAsync(async ct =>
        {
            var result = await _backups.DeleteOldAppBackupsAsync(row.Location.Id, keep, ct);
            ShowResult(result.AsUntyped());
            if (result.Success && result.Value > 0)
            {
                _notifications.Success(Loc.T("Old backups deleted"), Sentence(result.Message));
            }

            await LoadAsync(ct);
        }, Loc.T("Moving old backups to the Recycle Bin…"), cancellable: false);
    }

    private void Open(string target, string? arguments = null)
    {
        var result = ShellLauncher.Open(target, arguments);
        if (!result.Success)
        {
            ErrorMessage = result.Message;
        }
    }
}
