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
    private HostsSnapshot? _snapshot;
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

    public ObservableCollection<HostsEntryRow> Rows { get; } = [];

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
    [ObservableProperty] private int _selectedViewIndex;
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

    public bool HasSelection => SelectedRow is not null;

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

    partial void OnSearchTextChanged(string? value) => RebuildRows();

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
        RefreshFromDocument();
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

    /// <summary>Updates the table, the text view and the tiles from the current document.</summary>
    private void RefreshFromDocument()
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

        var entries = _document.Entries.ToList();
        ActiveText = entries.Count(e => e.Enabled).ToString(CultureInfo.CurrentCulture);
        OffText = entries.Count(e => !e.Enabled).ToString(CultureInfo.CurrentCulture);
        BlockedText = entries.Count(e => e.Enabled && HostsSuspicionRules.IsBlockAddress(e.Address)).ToString(CultureInfo.CurrentCulture);
        UnwantedCount = entries.Count(e => HostsSuspicionRules.Evaluate(e) is not null);
        UnwantedText = UnwantedCount.ToString(CultureInfo.CurrentCulture);
        RebuildRows();
    }

    private void RebuildRows()
    {
        int? selectedLine = SelectedRow?.LineIndex;
        string term = SearchText?.Trim() ?? string.Empty;
        Rows.Clear();
        for (int i = 0; i < _document.Lines.Count; i++)
        {
            var line = _document.Lines[i];
            if (line.Kind != HostsLineKind.Entry)
            {
                continue;
            }

            var row = new HostsEntryRow(i, line, OnRowEnabledChanged);
            bool keep = SelectedFilterIndex switch
            {
                1 => line.Enabled,
                2 => !line.Enabled,
                3 => line.Enabled && HostsSuspicionRules.IsBlockAddress(line.Address),
                4 => row.HasFlag,
                _ => true,
            };
            if (keep && (term.Length == 0
                || row.NamesText.Contains(term, StringComparison.OrdinalIgnoreCase)
                || row.Address.Contains(term, StringComparison.OrdinalIgnoreCase)
                || row.Comment.Contains(term, StringComparison.CurrentCultureIgnoreCase)))
            {
                Rows.Add(row);
            }
        }

        HasNoRows = Rows.Count == 0;
        SelectedRow = selectedLine is null ? null : Rows.FirstOrDefault(r => r.LineIndex == selectedLine);
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
        RefreshFromDocument();
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
