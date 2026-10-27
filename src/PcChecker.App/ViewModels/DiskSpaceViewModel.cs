using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
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
    private const string DefaultSubtitle = "See what fills your drives, find duplicates, and clear out what you no longer need.";

    private readonly IDiskSpaceService _service;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly ILogger<DiskSpaceViewModel> _logger;

    [ObservableProperty]
    private string _subtitle = DefaultSubtitle;

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
        var folder = await _dialogs.PickFolderAsync(Localization.Loc.T("Choose a folder to measure"));
        if (!string.IsNullOrWhiteSpace(folder))
        {
            await RunScanAsync(folder, CancellationToken.None);
        }
    }

    [RelayCommand]
    private Task RefreshDrivesAsync() => RunBusyAsync(LoadDrivesAsync, "Reading drives…");

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
    }, "Reading " + target + "…");

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
        Subtitle = $"Measured {result.RootPath} at {result.Captured:t} in {DiskSpaceFormat.Duration(result.Stats.Duration)}.";
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
        ScannedSubtitle = stale ? "Totals updated after removal" : scan.RootPath;
        UsedText = ByteSize.Format(root.TotalBytes);
        UsedSubtitle = scan.Drive is { } d
            ? $"{DiskSpaceFormat.PercentText(d.UsedBytes, d.TotalBytes)} of {ByteSize.Format(d.TotalBytes)} is in use · {ByteSize.Format(d.FreeBytes)} free"
            : "In this folder and below";
        var largest = DiskSpaceAggregation.LargestChild(root);
        LargestFolderText = largest is null ? "—" : ByteSize.Format(largest.TotalBytes);
        LargestFolderSubtitle = largest is null ? "No sub-folders" : largest.Name;
        FilesText = root.TotalFiles.ToString("#,0", System.Globalization.CultureInfo.CurrentCulture);
        FilesSubtitle = $"in {DiskSpaceFormat.Count(root.TotalFolders, "folder", "folders")}";

        var notes = new List<string>();
        if (root.InaccessibleFolders > 0)
        {
            notes.Add($"{DiskSpaceFormat.Count(root.InaccessibleFolders, "folder", "folders")} could not be read (Windows keeps some folders private), so the totals may be a little low.");
        }

        if (scan.Drive is { } dr && dr.UsedBytes > root.TotalBytes + ByteSize.Gigabyte)
        {
            notes.Add($"Windows reports {ByteSize.Format(dr.UsedBytes - root.TotalBytes)} more in use than the files add up to: restore points, the file system's own records and unreadable folders.");
        }

        if (scan.Stats.SkippedLinks > 0)
        {
            notes.Add($"{DiskSpaceFormat.Count(scan.Stats.SkippedLinks, "link was", "links were")} not followed, so nothing is counted twice.");
        }

        if (scan.Stats.CloudOnlyFiles > 0)
        {
            notes.Add($"{DiskSpaceFormat.Count(scan.Stats.CloudOnlyFiles, "online-only file", "online-only files")} (OneDrive and similar) take no space here and are not counted.");
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
