using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Localization;
using PcChecker.App.Services;
using PcChecker.Core.Common;
using PcChecker.Core.DiskSpace;
using PcChecker.Core.Leftovers;
using PcChecker.Core.Settings;

namespace PcChecker.App.ViewModels;

/// <summary>
/// Leftovers page: finds folders, registry keys, services, scheduled tasks and Start menu shortcuts that programs left
/// behind, groups them by kind with a confidence for each, and removes the ticked ones after a confirmation (Recycle
/// Bin for files, .reg and XML backups for keys and tasks, which the restore list can put back). Scans focused on a
/// just-uninstalled program arrive from the Apps page through <see cref="LeftoverScanQueue"/>.
/// </summary>
public sealed partial class LeftoversViewModel : ViewModelBase
{
    private readonly ILeftoverService _service;
    private readonly LeftoverScanQueue _queue;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly ILogger<LeftoversViewModel> _logger;
    private readonly List<LeftoverRow> _rows = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFindings))]
    [NotifyPropertyChangedFor(nameof(HasNoFindings))]
    private bool _hasScanned;

    [ObservableProperty]
    private string _subtitle = Loc.T("Folders, settings, services, tasks and shortcuts that removed programs left behind.");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFindings))]
    [NotifyPropertyChangedFor(nameof(HasNoFindings))]
    private int _findingCount;

    [ObservableProperty]
    private int _highCount;

    [ObservableProperty]
    private string _spaceText = "—";

    [ObservableProperty]
    private int _systemEntryCount;

    [ObservableProperty]
    private string _comparedText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveSelectedCommand))]
    private int _selectedCount;

    [ObservableProperty]
    private string _selectedText = Loc.T("Nothing selected");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarnings))]
    private string? _warnings;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFocus))]
    private string? _focusText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSuccessResult))]
    [NotifyPropertyChangedFor(nameof(ShowFailureResult))]
    private string? _resultMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSuccessResult))]
    [NotifyPropertyChangedFor(nameof(ShowFailureResult))]
    private bool _resultSuccess;

    public LeftoversViewModel(ISettingsService settings, ILeftoverService service, LeftoverScanQueue queue, IDialogService dialogs,
        INotificationService notifications, ILogger<LeftoversViewModel> logger)
        : base(settings)
    {
        _service = service;
        _queue = queue;
        _dialogs = dialogs;
        _notifications = notifications;
        _logger = logger;
        ResultLines.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasResultLines));
        RestoreEntries.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasRestoreEntries));
        _queue.Queued += (_, _) => DispatcherHelper.PostToUi(OnScanQueued);
    }

    public ObservableCollection<LeftoverGroup> Groups { get; } = [];

    public ObservableCollection<string> ResultLines { get; } = [];

    public ObservableCollection<LeftoverRestoreRow> RestoreEntries { get; } = [];

    public bool HasFindings => HasScanned && FindingCount > 0;

    public bool HasNoFindings => HasScanned && FindingCount == 0;

    public bool HasWarnings => !string.IsNullOrWhiteSpace(Warnings);

    public bool HasFocus => !string.IsNullOrWhiteSpace(FocusText);

    public bool HasResultLines => ResultLines.Count > 0;

    public bool HasRestoreEntries => RestoreEntries.Count > 0;

    public bool ShowSuccessResult => ResultSuccess && !string.IsNullOrWhiteSpace(ResultMessage);

    public bool ShowFailureResult => !ResultSuccess && !string.IsNullOrWhiteSpace(ResultMessage);

    public string BackupFolder => _service.BackupDirectory;

    /// <summary>
    /// A scan queued by the Apps page runs straight away; demo mode scans so the page has content; otherwise the page
    /// waits for the user (a real scan reads every program folder) and only loads the restore list.
    /// </summary>
    public override async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await LoadRestoreEntriesAsync(cancellationToken);
        var focus = _queue.Take();
        if (focus is not null || AppOptions.Current.IsDemo)
        {
            await RunScanAsync(focus, cancellationToken);
        }
    }

    public override Task RefreshAsync(CancellationToken cancellationToken) => RunScanAsync(null, cancellationToken);

    protected override void OnBusyError(Exception exception) => _logger.LogError(exception, "Leftovers page operation failed.");

    /// <summary>The Apps page queued a scan while this page was already loaded: run it now (or after the current work).</summary>
    private async void OnScanQueued()
    {
        if (!IsInitialized || IsBusy)
        {
            // InitializeAsync (or the next scan) takes the request.
            return;
        }

        // The request may already have been taken (by InitializeAsync or a scan that started meanwhile).
        if (_queue.Take() is not { } focus)
        {
            return;
        }

        try
        {
            await RunScanAsync(focus, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Queued leftover scan failed.");
        }
    }

    [RelayCommand]
    private Task ScanAsync() => RunScanAsync(_queue.Take(), CancellationToken.None);

    private Task RunScanAsync(LeftoverFocus? focus, CancellationToken outer) => RunBusyAsync(async busyToken =>
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(busyToken, outer);
        await ScanCoreAsync(focus, linked.Token);
    }, focus is null ? Loc.T("Looking for leftovers…") : Loc.F("Looking for what {0} left behind…", focus.ProgramName));

    private async Task ScanCoreAsync(LeftoverFocus? focus, CancellationToken ct)
    {
        var progress = new Progress<ProgressUpdate>(p => BusyText = p.Stage + (p.Detail is null ? string.Empty : " — " + p.Detail));
        var result = await _service.ScanAsync(focus, progress, ct);
        Apply(result);
    }

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private async Task RemoveSelectedAsync()
    {
        var selected = _rows.Where(r => r.IsSelected && r.CanSelect).Select(r => r.Item).ToList();
        if (selected.Count == 0)
        {
            return;
        }

        if (!await _dialogs.ConfirmAsync(Loc.T("Remove these leftovers?"), ConfirmationText(selected), Loc.T("Remove"), isDanger: true))
        {
            return;
        }

        var focus = Last?.Focus;
        await RunBusyAsync(async ct =>
        {
            var progress = new Progress<ProgressUpdate>(p => BusyText = p.Stage + (p.Detail is null ? string.Empty : " — " + p.Detail));
            var result = await _service.RemoveAsync(selected, OwnerWindow(), progress, ct);
            ShowResult(result);
            await LoadRestoreEntriesAsync(ct);
            BusyText = Loc.T("Checking again…");
            await ScanCoreAsync(focus, ct);
        }, Loc.T("Removing leftovers…"), cancellable: false);
    }

    private bool CanRemove() => SelectedCount > 0;

    private LeftoverScanResult? Last => _service.Last;

    [RelayCommand]
    private void SelectRecommended()
    {
        foreach (var row in _rows)
        {
            row.IsSelected = row.Item.IsPreselected;
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
    private void OpenLocation(LeftoverRow? row)
    {
        if (row is null || !row.CanOpen)
        {
            return;
        }

        var path = row.Item.Kind == LeftoverKind.Shortcut ? DiskSpacePaths.ParentOf(row.Item.Location) : row.Item.Location;
        if (path is null || !Directory.Exists(path))
        {
            _notifications.Warning(Loc.T("Folder not opened"), Loc.T("It no longer exists."));
            return;
        }

        var result = ShellLauncher.Open(path);
        if (!result.Success)
        {
            _notifications.Warning(Loc.T("Folder not opened"), result.Message);
        }
    }

    [RelayCommand]
    private void OpenBackupFolder()
    {
        try
        {
            Directory.CreateDirectory(_service.BackupDirectory);
        }
        catch (Exception ex)
        {
            ErrorMessage = Loc.F("The backup folder could not be created: {0}", ex.Message);
            return;
        }

        var result = ShellLauncher.Open(_service.BackupDirectory);
        if (!result.Success)
        {
            ErrorMessage = result.Message;
        }
    }

    [RelayCommand]
    private Task RefreshRestoreListAsync() => RunBusyAsync(LoadRestoreEntriesAsync, Loc.T("Loading the restore list…"));

    [RelayCommand]
    private async Task RestoreAsync(LeftoverRestoreRow? row)
    {
        if (row is null || !row.CanRestore)
        {
            return;
        }

        var message = row.Entry.Kind == LeftoverRestoreKind.RegistryKeys
            ? Loc.F("The registry keys removed on {0} are put back from their .reg backup:\n\n{1}\n\nA restore point is created first.", row.CreatedText, row.Location)
            : Loc.F("The scheduled task {0} is registered again from the XML saved on {1}. It still points at a program that is gone, so it will not do anything useful until that program is reinstalled.", row.Location, row.CreatedText);
        if (!await _dialogs.ConfirmAsync(Loc.T("Restore this?"), message, Loc.T("Restore")))
        {
            return;
        }

        await RunBusyAsync(async ct =>
        {
            var result = await _service.RestoreAsync(row.Entry.Id, ct);
            if (!result.Success)
            {
                ErrorMessage = result.Message;
                return;
            }

            _notifications.Success(Loc.T("Restored"), result.Message);
            await LoadRestoreEntriesAsync(ct);
        }, Loc.T("Restoring…"), cancellable: false);
    }

    private async Task LoadRestoreEntriesAsync(CancellationToken ct)
    {
        var entries = await _service.ListRestoreEntriesAsync(ct);
        DispatcherHelper.RunOnUi(() =>
        {
            RestoreEntries.Clear();
            foreach (var entry in entries)
            {
                RestoreEntries.Add(new LeftoverRestoreRow(entry));
            }
        });
    }

    private void Apply(LeftoverScanResult result)
    {
        _rows.Clear();
        _rows.AddRange(result.Items.Select(i => new LeftoverRow(i, UpdateSelection)));
        Groups.Clear();
        foreach (var group in _rows.GroupBy(r => LeftoverGroup.KeyOf(r.Item.Kind)).OrderBy(g => LeftoverGroup.OrderOf(g.Key)))
        {
            Groups.Add(new LeftoverGroup(group.Key, group));
        }

        FindingCount = result.Items.Count;
        HighCount = result.Items.Count(i => i.Confidence == LeftoverConfidence.High);
        long bytes = result.Items.Where(i => i.Kind is LeftoverKind.Folder or LeftoverKind.EmptyFolder or LeftoverKind.Shortcut).Sum(i => i.SizeBytes ?? 0);
        SpaceText = ByteSize.Format(bytes);
        SystemEntryCount = result.Items.Count(i => i.Kind is LeftoverKind.RegistryKey or LeftoverKind.Service or LeftoverKind.ScheduledTask or LeftoverKind.Shortcut);
        ComparedText = result.ProgramsCompared > 0 ? Loc.F("Compared with {0} installed programs", result.ProgramsCompared) : Loc.T("Nothing to compare with");
        Warnings = result.Warnings.Count == 0 ? null : string.Join("\n", result.Warnings);
        int focusCount = result.Items.Count(i => i.MatchesFocus);
        FocusText = result.Focus is null
            ? null
            : focusCount == 0
                ? Loc.F("Nothing that belongs to {0} was found: its uninstaller cleaned up after itself. Other leftovers are listed below.", result.Focus.ProgramName)
                : Loc.F("{0} of these belong to {1}, which you just uninstalled. They are marked and ticked.", focusCount, result.Focus.ProgramName);
        Subtitle = Loc.F("Checked {0}. Traces that removed programs left behind.", result.Captured.ToString("t", CultureInfo.CurrentCulture));
        HasScanned = true;
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        var selected = _rows.Where(r => r.IsSelected && r.CanSelect).Select(r => r.Item).ToList();
        SelectedCount = selected.Count;
        if (selected.Count == 0)
        {
            SelectedText = Loc.T("Nothing selected");
            return;
        }

        long bytes = selected.Where(i => i.Kind is LeftoverKind.Folder or LeftoverKind.EmptyFolder or LeftoverKind.Shortcut).Sum(i => i.SizeBytes ?? 0);
        SelectedText = bytes > 0
            ? Loc.F("{0} selected · frees about {1}", selected.Count, ByteSize.Format(bytes))
            : Loc.F("{0} selected", selected.Count);
    }

    private static string ConfirmationText(IReadOnlyList<LeftoverItem> selected)
    {
        var text = new StringBuilder(Loc.T("PC Checker will:")).Append('\n');
        void Line(IEnumerable<LeftoverItem> items, string format)
        {
            var list = items.ToList();
            if (list.Count > 0)
            {
                text.Append("• ").Append(string.Format(CultureInfo.CurrentCulture, format, LeftoverRules.Summarize(list))).Append('\n');
            }
        }

        Line(selected.Where(i => i.Kind is LeftoverKind.Folder or LeftoverKind.EmptyFolder or LeftoverKind.Shortcut),
            Loc.T("move {0} to the Recycle Bin"));
        Line(selected.Where(i => i.Kind == LeftoverKind.RegistryKey), Loc.T("back up {0} to a .reg file, then remove them"));
        Line(selected.Where(i => i.Kind == LeftoverKind.ScheduledTask), Loc.T("export {0} to XML, then delete them"));
        Line(selected.Where(i => i.Kind == LeftoverKind.Service), Loc.T("delete {0} (only if still stopped and their program is still missing)"));

        text.Append('\n').Append(Loc.T("A restore point is created first. Each item is checked again right before it is removed, and anything that changed is kept. Registry keys and tasks can be put back from the restore list."));
        int password = selected.Count(i => i.Kind == LeftoverKind.ScheduledTask && i.NeedsPasswordToRestore);
        if (password > 0)
        {
            text.Append("\n\n").Append(Loc.F("{0} of the tasks ran with a saved password. The restore list cannot put them back; you would import the exported XML in Task Scheduler and type the password.", password));
        }

        int notHigh = selected.Count(i => i.Confidence != LeftoverConfidence.High);
        if (notHigh > 0)
        {
            text.Append("\n\n").Append(Loc.F("{0} of the selected items are not marked High. Only remove them if you recognise them.", notHigh));
        }

        return text.ToString();
    }

    private void ShowResult(OperationResult<LeftoverRemovalResult> result)
    {
        DispatcherHelper.RunOnUi(() =>
        {
            ResultLines.Clear();
            if (result.Value is { } value)
            {
                foreach (var outcome in value.Outcomes)
                {
                    ResultLines.Add($"{(outcome.Success ? "✓" : "–")} {outcome.Item.Name} ({outcome.Item.Location}): {outcome.Message}");
                }
            }
        });

        bool allOk = result.Success && (result.Value?.NotRemoved ?? 0) == 0;
        ResultSuccess = allOk;
        ResultMessage = result.Message;
        if (allOk)
        {
            _notifications.Success(Loc.T("Leftovers removed"), result.Message);
        }
        else if (result.Success)
        {
            _notifications.Warning(Loc.T("Some leftovers were kept"), result.Message);
        }
        else
        {
            _notifications.Error(Loc.T("Nothing was removed"), result.Message.Split('\n')[0]);
        }
    }

    /// <summary>The main window's handle, so a prompt from Windows (item too big for the Recycle Bin) opens in front of it.</summary>
    private static nint OwnerWindow()
    {
        try
        {
            return Application.Current?.MainWindow is { } window ? new WindowInteropHelper(window).Handle : 0;
        }
        catch (InvalidOperationException)
        {
            return 0;
        }
    }
}
