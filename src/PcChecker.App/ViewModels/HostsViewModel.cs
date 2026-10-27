using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Localization;
using PcChecker.App.Services;
using PcChecker.Core.Common;
using PcChecker.Core.Hosts;
using PcChecker.Core.Network;
using PcChecker.Core.Settings;

namespace PcChecker.App.ViewModels;

/// <summary>
/// Hosts file page: the entries as a table and the whole file as text. Edits stay on the page until Save, which asks
/// first, keeps a timestamped backup, goes through the change guard and clears the DNS cache. Entries that send
/// well-known sites elsewhere or block security updates are flagged. Actions are in HostsViewModel.Actions.cs.
/// </summary>
public sealed partial class HostsViewModel : ViewModelBase
{
    private readonly IHostsFileService _hosts;
    private readonly INetworkService _network;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly ILogger<HostsViewModel> _logger;

    /// <summary>The table shows at most this many rows; search or filter finds the rest (block lists can be huge).</summary>
    private const int MaxShownRows = 1000;

    /// <summary>Above this many entries, search waits for a pause in typing before filtering.</summary>
    private const int SearchDebounceThreshold = 2000;

    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(250);

    private HostsSnapshot? _snapshot;
    private List<HostsEntryRow> _allRows = [];
    private CancellationTokenSource? _searchDebounce;
    private HostsDocument _document = HostsDocument.Empty;
    private HostsBackup? _lastBackup;
    private bool _rawDirty;
    private bool _settingRaw;

    public HostsViewModel(ISettingsService settings, IHostsFileService hosts, INetworkService network, IDialogService dialogs,
        INotificationService notifications, ILogger<HostsViewModel> logger)
        : base(settings)
    {
        _hosts = hosts;
        _network = network;
        _dialogs = dialogs;
        _notifications = notifications;
        _logger = logger;
    }

    /// <summary>The rows shown, replaced in one go so a big file does not send one notification per row.</summary>
    [ObservableProperty] private ObservableCollection<HostsEntryRow> _rows = [];

    public ObservableCollection<HostsBackupRow> Backups { get; } = [];

    /// <summary>The filter drop-down, in order.</summary>
    public IReadOnlyList<string> FilterOptions { get; } =
    [
        Loc.T("All entries"),
        Loc.T("Turned on"),
        Loc.T("Turned off"),
        Loc.T("Blocked sites"),
        Loc.T("Possibly unwanted"),
    ];

    [ObservableProperty] private int _selectedFilterIndex;
    [ObservableProperty] private string? _searchText;
    [ObservableProperty] private bool _hasLoaded;
    [ObservableProperty] private bool _hasNoRows;
    [ObservableProperty] private string _subtitle = Loc.T("Which names Windows looks up here first, before asking the internet.");
    [ObservableProperty] private string _activeText = "—";
    [ObservableProperty] private string _offText = "—";
    [ObservableProperty] private string _blockedText = "—";
    [ObservableProperty] private string _unwantedText = "—";
    [ObservableProperty] private string _fileInfoText = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(EditEntryCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveEntryCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleEntryCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyLineCommand))]
    private int _selectedViewIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRowLimit))]
    private string? _rowLimitText;
    [ObservableProperty] private bool _hasNoBackups = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnwanted))]
    [NotifyPropertyChangedFor(nameof(UnwantedMessage))]
    private int _unwantedCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFileWarnings))]
    private string? _fileWarnings;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSaveError))]
    private string? _saveError;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(DiscardCommand))]
    private bool _isDirty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoLastSaveCommand))]
    private bool _canUndoSave;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(EditEntryCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveEntryCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleEntryCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyLineCommand))]
    private HostsEntryRow? _selectedRow;

    private string _rawText = string.Empty;

    /// <summary>The whole file as text. Typing here marks the page changed; the table follows when you switch back.</summary>
    public string RawText
    {
        get => _rawText;
        set
        {
            if (SetProperty(ref _rawText, value) && !_settingRaw)
            {
                _rawDirty = true;
                IsDirty = true;
            }
        }
    }

    /// <summary>Only in the table view: the entry actions work on the table, not on text typed in the Text view.</summary>
    public bool HasSelection => SelectedRow is not null && SelectedViewIndex == 0;

    public bool HasRowLimit => !string.IsNullOrWhiteSpace(RowLimitText);

    public bool HasFileWarnings => !string.IsNullOrWhiteSpace(FileWarnings);

    public bool HasSaveError => !string.IsNullOrWhiteSpace(SaveError);

    public bool HasUnwanted => UnwantedCount > 0;

    public string UnwantedMessage => UnwantedCount == 1
        ? Loc.T("1 entry looks like something malware adds. Select it to see why, then turn it off or remove it and Save.")
        : Loc.F("{0} entries look like something malware adds. Select them to see why, then turn them off or remove them and Save.", UnwantedCount);

    public string BackupFolder => _hosts.BackupFolder;

    /// <summary>Reading the hosts file changes nothing, so the page fills itself in both modes.</summary>
    public override Task InitializeAsync(CancellationToken cancellationToken) => LoadCoreAsync(cancellationToken);

    public override Task RefreshAsync(CancellationToken cancellationToken) => LoadCoreAsync(cancellationToken);

    protected override void OnBusyError(Exception exception) => _logger.LogError(exception, "Hosts file page operation failed.");

    partial void OnSearchTextChanged(string? value)
    {
        _searchDebounce?.Cancel();
        _searchDebounce?.Dispose();
        _searchDebounce = null;
        if (_allRows.Count <= SearchDebounceThreshold)
        {
            RebuildRows();
            return;
        }

        var debounce = new CancellationTokenSource();
        _searchDebounce = debounce;
        _ = RebuildAfterPauseAsync(debounce.Token);
    }

    private async Task RebuildAfterPauseAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(SearchDelay, cancellationToken);
            RebuildRows();
        }
        catch (OperationCanceledException)
        {
            // Superseded by newer typing; that search rebuilds the table.
        }
    }

    partial void OnSelectedFilterIndexChanged(int value) => RebuildRows();

    partial void OnSelectedViewIndexChanged(int value)
    {
        // Leaving the text view: read what was typed so the table shows it.
        if (value == 0)
        {
            ApplyRawText();
        }
    }

    private Task LoadCoreAsync(CancellationToken outer) => RunBusyAsync(async busyToken =>
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(busyToken, outer);
        await LoadAsync(linked.Token);
    }, Loc.T("Reading the hosts file…"));

    private async Task LoadAsync(CancellationToken ct)
    {
        var snapshot = await _hosts.LoadAsync(ct);
        _snapshot = snapshot;
        _document = snapshot.Document;
        _rawDirty = false;
        IsDirty = false;
        SaveError = null;
        FileWarnings = snapshot.Warnings.Count == 0 ? null : string.Join("\n", snapshot.Warnings);
        FileInfoText = snapshot.Facts.Exists
            ? Loc.F("{0} · {1} · {2}", snapshot.Path, HostsEncodingDetector.Describe(snapshot.Encoding), ByteSize.Format(snapshot.Facts.Size))
            : snapshot.Path;
        Subtitle = Loc.F("Read at {0}. Edits are kept here until you Save.", snapshot.Loaded.ToString("t", CultureInfo.CurrentCulture));
        // Building a row per entry (with its checks) is the slow part for big block lists, so it runs off the UI thread.
        var document = snapshot.Document;
        var rows = await Task.Run(() => CreateRows(document, OnRowEnabledChanged), ct);
        RefreshFromDocument(rows);
        await LoadBackupsAsync(ct);
        HasLoaded = true;
    }

    private async Task LoadBackupsAsync(CancellationToken ct)
    {
        var backups = await _hosts.GetBackupsAsync(ct);
        Backups.Clear();
        foreach (var backup in backups)
        {
            Backups.Add(new HostsBackupRow(backup));
        }

        HasNoBackups = Backups.Count == 0;
    }

    /// <summary>
    /// Updates the table, the text view and the tiles from the current document. <paramref name="rows"/> are rows
    /// already built for it; <paramref name="changedIndex"/> is a single entry line that was replaced in place, so only
    /// that row is rebuilt.
    /// </summary>
    private void RefreshFromDocument(List<HostsEntryRow>? rows = null, int? changedIndex = null)
    {
        _settingRaw = true;
        try
        {
            RawText = HostsParser.Serialize(_document);
        }
        finally
        {
            _settingRaw = false;
        }

        if (rows is not null)
        {
            _allRows = rows;
        }
        else if (changedIndex is not { } index || !TryReplaceRow(index))
        {
            _allRows = CreateRows(_document, OnRowEnabledChanged);
        }

        int on = 0, off = 0, blocked = 0, unwanted = 0;
        foreach (var row in _allRows)
        {
            if (row.Line.Enabled)
            {
                on++;
                blocked += row.IsBlock ? 1 : 0;
            }
            else
            {
                off++;
            }

            unwanted += row.HasFlag ? 1 : 0;
        }

        ActiveText = on.ToString(CultureInfo.CurrentCulture);
        OffText = off.ToString(CultureInfo.CurrentCulture);
        BlockedText = blocked.ToString(CultureInfo.CurrentCulture);
        UnwantedCount = unwanted;
        UnwantedText = unwanted.ToString(CultureInfo.CurrentCulture);
        RebuildRows();
    }

    /// <summary>One row per entry line of <paramref name="document"/>, in file order. Safe to run off the UI thread.</summary>
    private static List<HostsEntryRow> CreateRows(HostsDocument document, Action<HostsEntryRow, bool> enabledChanged)
    {
        var rows = new List<HostsEntryRow>();
        for (int i = 0; i < document.Lines.Count; i++)
        {
            if (document.Lines[i].Kind == HostsLineKind.Entry)
            {
                rows.Add(new HostsEntryRow(i, document.Lines[i], enabledChanged));
            }
        }

        return rows;
    }

    /// <summary>Rebuilds the row of one entry line that was replaced by another entry line; false when that does not apply.</summary>
    private bool TryReplaceRow(int lineIndex)
    {
        if (lineIndex < 0 || lineIndex >= _document.Lines.Count || _document.Lines[lineIndex].Kind != HostsLineKind.Entry)
        {
            return false;
        }

        int lo = 0, hi = _allRows.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            int at = _allRows[mid].LineIndex;
            if (at == lineIndex)
            {
                _allRows[mid] = new HostsEntryRow(lineIndex, _document.Lines[lineIndex], OnRowEnabledChanged);
                return true;
            }

            if (at < lineIndex)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return false;
    }

    private void RebuildRows()
    {
        int? selectedLine = SelectedRow?.LineIndex;
        string term = SearchText?.Trim() ?? string.Empty;
        var shown = new List<HostsEntryRow>(Math.Min(_allRows.Count, MaxShownRows));
        int matching = 0;
        foreach (var row in _allRows)
        {
            bool keep = SelectedFilterIndex switch
            {
                1 => row.Line.Enabled,
                2 => !row.Line.Enabled,
                3 => row.Line.Enabled && row.IsBlock,
                4 => row.HasFlag,
                _ => true,
            };
            if (keep && (term.Length == 0
                || row.NamesText.Contains(term, StringComparison.OrdinalIgnoreCase)
                || row.Address.Contains(term, StringComparison.OrdinalIgnoreCase)
                || row.Comment.Contains(term, StringComparison.CurrentCultureIgnoreCase)))
            {
                matching++;
                if (shown.Count < MaxShownRows)
                {
                    shown.Add(row);
                }
            }
        }

        Rows = new ObservableCollection<HostsEntryRow>(shown);
        HasNoRows = shown.Count == 0;
        RowLimitText = matching > shown.Count
            ? Loc.F("Showing the first {0} of {1} entries. Search or filter to find the others.",
                shown.Count.ToString("N0", CultureInfo.CurrentCulture), matching.ToString("N0", CultureInfo.CurrentCulture))
            : null;
        SelectedRow = selectedLine is null ? null : shown.FirstOrDefault(r => r.LineIndex == selectedLine);
    }

    private void OnRowEnabledChanged(HostsEntryRow row, bool enabled)
    {
        if (row.Line.Enabled == enabled || row.LineIndex >= _document.Lines.Count)
        {
            return;
        }

        // Rebuild the table after the tick box has finished its own update.
        int index = row.LineIndex;
        var line = row.Line with { Raw = null, Enabled = enabled };
        DispatcherHelper.PostToUi(() => ReplaceLine(index, line));
    }

    private void ReplaceLine(int index, HostsLine? line)
    {
        if (index < 0 || index > _document.Lines.Count || (line is null && index == _document.Lines.Count))
        {
            return;
        }

        // An entry replaced by an entry keeps every line number, so only its own row needs rebuilding.
        bool inPlace = line is { Kind: HostsLineKind.Entry } && index < _document.Lines.Count
            && _document.Lines[index].Kind == HostsLineKind.Entry;
        var lines = _document.Lines.ToList();
        if (line is null)
        {
            lines.RemoveAt(index);
        }
        else if (index == lines.Count)
        {
            lines.Add(line);
        }
        else
        {
            lines[index] = line;
        }

        _document = _document with { Lines = lines };
        IsDirty = true;
        RefreshFromDocument(changedIndex: inPlace ? index : null);
    }

    /// <summary>Reads the text view into the document when it was typed in.</summary>
    private void ApplyRawText()
    {
        if (!_rawDirty)
        {
            return;
        }

        _rawDirty = false;
        _document = HostsParser.Parse(RawText);
        RefreshFromDocument();
    }
}
