using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Localization;
using PcChecker.App.Services;
using PcChecker.Core.Common;
using PcChecker.Core.DiskSpace;
using PcChecker.Core.Settings;

namespace PcChecker.App.ViewModels;

/// <summary>
/// Disk space page: pick a drive or folder, see what uses the space (folders you can drill into, largest files, types,
/// old big files), find duplicates and move what you pick to the Recycle Bin. Scans only read; the service checks each
/// item again and journals the move. The drill-down, selection and removal live in the partial files next to this one.
/// </summary>
public sealed partial class DiskSpaceViewModel : ViewModelBase
{
    private readonly IDiskSpaceService _service;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly ILogger<DiskSpaceViewModel> _logger;

    [ObservableProperty]
    private string _subtitle = Loc.T("See what fills your drives, find duplicates, and clear out what you no longer need.");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    private bool _hasScanned;

    [ObservableProperty]
    private string _scannedText = "—";

    [ObservableProperty]
    private string _scannedSubtitle = string.Empty;

    [ObservableProperty]
    private string _usedText = "—";

    [ObservableProperty]
    private string _usedSubtitle = string.Empty;

    [ObservableProperty]
    private string _largestFolderText = "—";

    [ObservableProperty]
    private string _largestFolderSubtitle = string.Empty;

    [ObservableProperty]
    private string _filesText = "—";

    [ObservableProperty]
    private string _filesSubtitle = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScanNote))]
    private string? _scanNote;

    [ObservableProperty]
    private int _selectedTabIndex;

    /// <summary>Options: the smallest file the duplicate search looks at, in MB.</summary>
    [ObservableProperty]
    private double _duplicateMinMegabytes = 1;

    /// <summary>Options: the smallest file listed under Old files, in MB.</summary>
    [ObservableProperty]
    private double _oldFileMinMegabytes = 50;

    /// <summary>Options: how long a file must have been unused to count as old, in months.</summary>
    [ObservableProperty]
    private double _oldFileMonths = 12;

    private DiskScanResult? _scan;

    public DiskSpaceViewModel(ISettingsService settings, IDiskSpaceService service, IDialogService dialogs, INotificationService notifications,
        ILogger<DiskSpaceViewModel> logger)
        : base(settings)
    {
        _service = service;
        _dialogs = dialogs;
        _notifications = notifications;
        _logger = logger;
        ResultLines.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasResultLines));
    }

    public ObservableCollection<DiskDriveRow> Drives { get; } = [];

    public ObservableCollection<DiskTypeRow> Types { get; } = [];

    public bool HasDrives => Drives.Count > 0;

    public bool ShowEmptyState => !HasScanned;

    public bool HasScanNote => !string.IsNullOrWhiteSpace(ScanNote);

    /// <summary>Lists the drives; in demo mode also scans the Windows drive so the page has content.</summary>
    public override async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await LoadDrivesAsync(cancellationToken);
        if (AppOptions.Current.IsDemo && Drives.FirstOrDefault() is { } first)
        {
            await RunScanAsync(first.Drive.RootPath, cancellationToken);
        }
    }

    public override async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await LoadDrivesAsync(cancellationToken);
        if (_scan is not null)
        {
            await RunScanAsync(_scan.RootPath, cancellationToken);
        }
    }

    protected override void OnBusyError(Exception exception) => _logger.LogError(exception, "Disk space page operation failed.");

    /// <summary>Header "Scan": scans the last target again, or the Windows drive the first time.</summary>
    [RelayCommand]
    private Task ScanAsync()
    {
        var target = _scan?.RootPath ?? Drives.FirstOrDefault(d => d.Drive.IsSystemDrive)?.Drive.RootPath ?? Drives.FirstOrDefault()?.Drive.RootPath;
        return target is null ? Task.CompletedTask : RunScanAsync(target, CancellationToken.None);
    }

    [RelayCommand]
    private Task ScanDriveAsync(DiskDriveRow? drive) =>
        drive is null ? Task.CompletedTask : RunScanAsync(drive.Drive.RootPath, CancellationToken.None);

    [RelayCommand]
    private async Task ChooseFolderAsync()
    {
        var folder = await _dialogs.PickFolderAsync(Loc.T("Choose a folder to measure"));
        if (!string.IsNullOrWhiteSpace(folder))
        {
            await RunScanAsync(folder, CancellationToken.None);
        }
    }

    [RelayCommand]
    private Task RefreshDrivesAsync() => RunBusyAsync(LoadDrivesAsync, Loc.T("Reading drives…"));

    [RelayCommand]
    private void OpenRecycleBin() => Open("shell:RecycleBinFolder");

    [RelayCommand]
    private void OpenStorageSettings() => Open("ms-settings:storagesense");

    private async Task LoadDrivesAsync(CancellationToken cancellationToken)
    {
        var drives = await _service.GetDrivesAsync(cancellationToken);
        Drives.Clear();
        foreach (var d in drives)
        {
            Drives.Add(new DiskDriveRow(d) { IsCurrent = _scan?.Drive?.RootPath == d.RootPath });
        }

        OnPropertyChanged(nameof(HasDrives));
    }

    private Task RunScanAsync(string target, CancellationToken outer) => RunBusyAsync(async busyToken =>
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(busyToken, outer);
        var progress = new Progress<ProgressUpdate>(p => BusyText = p.Stage + (p.Detail is null ? string.Empty : "\n" + p.Detail));
        var result = await _service.ScanAsync(target, ScanOptions(), progress, linked.Token);
        ClearResult();
        ApplyScan(result);
    }, Loc.F("Reading {0}…", target));

    private DiskScanOptions ScanOptions() => new()
    {
        DuplicateMinBytes = (long)(Math.Clamp(DuplicateMinMegabytes, 0.01, 100_000) * ByteSize.Megabyte),
        OldFileMinBytes = (long)(Math.Clamp(OldFileMinMegabytes, 1, 1_000_000) * ByteSize.Megabyte),
        OldFileDays = (int)Math.Round(Math.Clamp(OldFileMonths, 1, 120) * 30.44),
    };

    /// <summary>Shows a scan: tiles, notes, folder list, file lists and types. Duplicates from an older scan are cleared.</summary>
    private void ApplyScan(DiskScanResult result)
    {
        _scan = result;
        ResetRows();
        ClearDuplicates();
        foreach (var d in Drives)
        {
            d.IsCurrent = result.Drive is not null && d.Drive.RootPath.Equals(result.Drive.RootPath, StringComparison.OrdinalIgnoreCase);
        }

        UpdateSummary(stale: false);
        NavigateTo(result.Root);
        FillFileLists();
        Subtitle = Loc.F("Measured {0} at {1:t} in {2}.", result.RootPath, result.Captured, DiskSpaceText.Duration(result.Stats.Duration));
        HasScanned = true;
    }

    private void UpdateSummary(bool stale)
    {
        if (_scan is not { } scan)
        {
            return;
        }

        var root = scan.Root;
        ScannedText = scan.Drive is { } drive ? drive.Letter : root.Name;
        ScannedSubtitle = stale ? Loc.T("Totals updated after removal") : scan.RootPath;
        UsedText = ByteSize.Format(root.TotalBytes);
        UsedSubtitle = scan.Drive is { } d
            ? Loc.F("{0} of {1} is in use · {2} free", DiskSpaceFormat.PercentText(d.UsedBytes, d.TotalBytes), ByteSize.Format(d.TotalBytes), ByteSize.Format(d.FreeBytes))
            : Loc.T("In this folder and below");
        var largest = DiskSpaceAggregation.LargestChild(root);
        LargestFolderText = largest is null ? "—" : ByteSize.Format(largest.TotalBytes);
        LargestFolderSubtitle = largest is null ? Loc.T("No sub-folders") : largest.Name;
        FilesText = root.TotalFiles.ToString("#,0", System.Globalization.CultureInfo.CurrentCulture);
        FilesSubtitle = DiskSpaceText.Count(root.TotalFolders, Loc.T("in {0} folder"), Loc.T("in {0} folders"));

        var notes = new List<string>();
        if (root.InaccessibleFolders > 0)
        {
            notes.Add(DiskSpaceText.Count(root.InaccessibleFolders,
                Loc.T("{0} folder could not be read (Windows keeps some folders private), so the totals may be a little low."),
                Loc.T("{0} folders could not be read (Windows keeps some folders private), so the totals may be a little low.")));
        }

        if (scan.Drive is { } dr && dr.UsedBytes > root.TotalBytes + ByteSize.Gigabyte)
        {
            notes.Add(Loc.F("Windows reports {0} more in use than the files add up to: restore points, the file system's own records and unreadable folders.",
                ByteSize.Format(dr.UsedBytes - root.TotalBytes)));
        }
        else if (scan.Drive is { } over && root.TotalBytes > over.UsedBytes + ByteSize.Gigabyte)
        {
            // Sizes are file lengths: hard-linked Windows files are counted once per path and compressed or sparse
            // files (CompactOS, WSL disks) at their full size, so the total can exceed what the drive really uses.
            notes.Add(Loc.F("The files add up to {0} more than Windows reports in use: files that share their data (hard links in the Windows folder) are counted once per path, and compressed or sparse files at their full size.",
                ByteSize.Format(root.TotalBytes - over.UsedBytes)));
        }

        if (scan.Stats.SkippedLinks > 0)
        {
            notes.Add(DiskSpaceText.Count(scan.Stats.SkippedLinks,
                Loc.T("{0} link was not followed, so nothing is counted twice."), Loc.T("{0} links were not followed, so nothing is counted twice.")));
        }

        if (scan.Stats.CloudOnlyFiles > 0)
        {
            notes.Add(DiskSpaceText.Count(scan.Stats.CloudOnlyFiles,
                Loc.T("{0} online-only file (OneDrive and similar) takes no space here and is not counted."),
                Loc.T("{0} online-only files (OneDrive and similar) take no space here and are not counted.")));
        }

        ScanNote = notes.Count == 0 ? null : string.Join("\n", notes);
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
