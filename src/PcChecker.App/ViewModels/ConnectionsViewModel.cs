using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Helpers;
using PcChecker.App.Localization;
using PcChecker.App.Services;
using PcChecker.Core.Connections;
using PcChecker.Core.Processes;
using PcChecker.Core.Settings;

namespace PcChecker.App.ViewModels;

/// <summary>
/// Connections page: every open TCP and UDP endpoint with the program behind it, like TCPView. Programs on the left
/// with their counts, the connections of the chosen one on the right, refreshed every two seconds while the page is
/// visible (with a pause). Programs that accept connections from other devices are highlighted in plain words.
/// </summary>
public sealed partial class ConnectionsViewModel : ViewModelBase
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    private readonly IConnectionService _connections;
    private readonly IProcessService _processes;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly ILogger<ConnectionsViewModel> _logger;
    private readonly HostNameCache _hostNames;
    private readonly ProcessIconCache _icons = new();
    private readonly Dictionary<string, ConnectionRow> _rowByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ConnectionProgramRow> _programByKey = new(StringComparer.Ordinal);
    private readonly HashSet<object> _viewers = [];
    private ConnectionSnapshot _last = ConnectionSnapshot.Empty(null);
    private DispatcherTimer? _timer;
    private bool _reading;
    private bool _rebuilding;
    private bool _hostRefreshQueued;

    public ConnectionsViewModel(IConnectionService connections, IProcessService processes, ISettingsService settings, IDialogService dialogs,
        INotificationService notifications, ILogger<ConnectionsViewModel> logger)
        : base(settings)
    {
        _connections = connections;
        _processes = processes;
        _dialogs = dialogs;
        _notifications = notifications;
        _logger = logger;
        _hostNames = new HostNameCache(connections.LookUpHostAsync);
        _hostNames.Resolved += OnHostResolved;
        AllPrograms = new ConnectionProgramRow(string.Empty, Loc.T("All programs"));
        Programs.Add(AllPrograms);
        _selectedProgram = AllPrograms;
    }

    public ObservableCollection<ConnectionRow> Rows { get; } = [];

    public ObservableCollection<ConnectionProgramRow> Programs { get; } = [];

    public ConnectionProgramRow AllPrograms { get; }

    /// <summary>The filter options, in the order of <see cref="ConnectionFilterKind"/>.</summary>
    public IReadOnlyList<string> FilterOptions { get; } = [Loc.T("All connections"), Loc.T("Listening"), Loc.T("Connected")];

    [ObservableProperty]
    private int _selectedFilterIndex;

    [ObservableProperty]
    private string? _searchText;

    [ObservableProperty]
    private bool _hideLoopback = true;

    [ObservableProperty]
    private bool _showHostNames;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PauseText), nameof(LiveText))]
    private bool _isPaused;

    [ObservableProperty]
    private ConnectionProgramRow? _selectedProgram;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(EndProcessNote))]
    [NotifyCanExecuteChangedFor(nameof(EndProcessCommand), nameof(CloseConnectionCommand), nameof(CopyCommand), nameof(SearchRemoteCommand),
        nameof(OpenFileLocationCommand), nameof(GoToProcessCommand))]
    private ConnectionRow? _selectedRow;

    [ObservableProperty]
    private string _programsText = "—";

    [ObservableProperty]
    private string _connectedText = "—";

    [ObservableProperty]
    private string _listeningText = "—";

    [ObservableProperty]
    private string _exposedText = "—";

    [ObservableProperty]
    private string? _exposedNames;

    [ObservableProperty]
    private bool _hasExposed;

    [ObservableProperty]
    private bool _hasNoRows;

    [ObservableProperty]
    private string? _warningText;

    public bool HasWarning => !string.IsNullOrWhiteSpace(WarningText);

    partial void OnWarningTextChanged(string? value) => OnPropertyChanged(nameof(HasWarning));

    public bool HasSelection => SelectedRow is not null;

    /// <summary>Why the selected program cannot be ended here (Windows' critical processes and the like), or null.</summary>
    public string? EndProcessNote => SelectedRow is { } row
        && ConnectionProcesses.EndRefusal(row.Process, row.Endpoint.Pid, _processes.OwnProcessId) is { } reason
            ? Loc.T(reason)
            : null;

    public string PauseText => IsPaused ? Loc.T("Resume") : Loc.T("Pause");

    public string LiveText => IsPaused ? Loc.T("Paused. The list is not updating.") : Loc.T("Updates every 2 seconds.");

    public override Task InitializeAsync(CancellationToken cancellationToken) =>
        RunBusyAsync(async ct => Apply(await _connections.ReadAsync(ct)), Loc.T("Reading connections…"));

    public override Task RefreshAsync(CancellationToken cancellationToken) => ReadNowAsync();

    /// <summary>The header's Refresh button and F5.</summary>
    [RelayCommand]
    private Task Refresh() => RefreshAsync(CancellationToken.None);

    /// <summary>Starts the two-second refresh for <paramref name="viewer"/> (the page while it is visible).</summary>
    public void StartLive(object viewer)
    {
        _viewers.Add(viewer);
        if (_timer is not null)
        {
            return;
        }

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = Interval };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    public void StopLive(object viewer)
    {
        _viewers.Remove(viewer);
        if (_viewers.Count > 0 || _timer is null)
        {
            return;
        }

        _timer.Stop();
        _timer.Tick -= OnTick;
        _timer = null;
    }

    private async void OnTick(object? sender, EventArgs e)
    {
        if (IsPaused || IsBusy || !IsInitialized)
        {
            return;
        }

        await ReadNowAsync();
    }

    private async Task ReadNowAsync()
    {
        if (_reading)
        {
            return;
        }

        _reading = true;
        try
        {
            Apply(await _connections.ReadAsync(CancellationToken.None));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Reading the connection list failed.");
        }
        finally
        {
            _reading = false;
        }
    }

    partial void OnSelectedFilterIndexChanged(int value) => Rebuild();

    partial void OnSearchTextChanged(string? value) => Rebuild();

    partial void OnHideLoopbackChanged(bool value) => Rebuild();

    partial void OnSelectedProgramChanged(ConnectionProgramRow? value)
    {
        if (_rebuilding)
        {
            return;
        }

        if (value is null)
        {
            // The list lost its selection: fall back to every program.
            SelectedProgram = AllPrograms;
            return;
        }

        Rebuild();
    }

    partial void OnShowHostNamesChanged(bool value)
    {
        UpdateHostNames();
        if (value)
        {
            _notifications.Info(Loc.T("Host names"), Loc.T("PC Checker asks your DNS server for the name of each remote address. Names appear as the answers come in."));
        }
    }

    [RelayCommand]
    private void TogglePause() => IsPaused = !IsPaused;

    private void Apply(ConnectionSnapshot snapshot)
    {
        _last = snapshot;
        WarningText = snapshot.Warning;
        Rebuild();
    }

    private ConnectionFilterKind Filter => (ConnectionFilterKind)Math.Clamp(SelectedFilterIndex, 0, FilterOptions.Count - 1);

    private void Rebuild()
    {
        if (_rebuilding)
        {
            return;
        }

        _rebuilding = true;
        try
        {
            RebuildCore();
        }
        finally
        {
            _rebuilding = false;
        }

        SelectedProgram ??= AllPrograms;
    }

    private void RebuildCore()
    {
        var processes = _last.Processes;
        var visible = ConnectionGrouping.Filter(_last.Endpoints, processes, Filter, HideLoopback, SearchText,
            e => ShowHostNames ? _hostNames.Get(e.RemoteAddress, startLookup: false) : null);
        var groups = ConnectionGrouping.Group(visible, processes);
        int iconBudget = 8;
        UpdatePrograms(groups, visible.Count, ref iconBudget);
        UpdateSummary(groups);

        string? key = SelectedProgram is { IsAll: false } program ? program.Key : null;
        var shown = key is null ? visible : visible.Where(e => ConnectionGrouping.KeyOf(processes.GetValueOrDefault(e.Pid), e.Pid) == key).ToList();
        var ordered = shown
            .OrderBy(e => ConnectionGrouping.Rank(e, processes.GetValueOrDefault(e.Pid)?.IsWindows ?? false))
            .ThenBy(e => ConnectionProcesses.NameOf(processes.GetValueOrDefault(e.Pid), e.Pid), StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.LocalPort)
            .ToList();

        var target = new List<ConnectionRow>(ordered.Count);
        foreach (var endpoint in ordered)
        {
            var process = processes.GetValueOrDefault(endpoint.Pid);
            if (_rowByKey.TryGetValue(endpoint.Key, out var row))
            {
                row.Endpoint = endpoint;
                row.Process = process;
            }
            else
            {
                row = new ConnectionRow(endpoint, process);
                _rowByKey[endpoint.Key] = row;
            }

            row.Icon ??= _icons.Get(process?.Path, ref iconBudget);
            row.HostName = ShowHostNames ? _hostNames.Get(endpoint.RemoteAddress, startLookup: true) : null;
            target.Add(row);
        }

        // Forget rows that are gone, so the dictionary does not grow forever.
        if (_rowByKey.Count > target.Count * 4 + 512)
        {
            var keep = target.Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var stale in _rowByKey.Keys.Where(k => !keep.Contains(k)).ToList())
            {
                _rowByKey.Remove(stale);
            }
        }

        Sync(Rows, target);
        HasNoRows = Rows.Count == 0;
        if (SelectedRow is not null && !Rows.Contains(SelectedRow))
        {
            SelectedRow = null;
        }

        NotifyCommands();
    }

    private void UpdatePrograms(IReadOnlyList<ConnectionGroup> groups, int total, ref int iconBudget)
    {
        AllPrograms.Count = total;
        AllPrograms.Detail = Loc.F("{0} programs", groups.Count);
        var target = new List<ConnectionProgramRow>(groups.Count + 1) { AllPrograms };
        foreach (var group in groups)
        {
            if (!_programByKey.TryGetValue(group.Key, out var row))
            {
                row = new ConnectionProgramRow(group.Key, group.Name);
                _programByKey[group.Key] = row;
            }

            row.Name = group.Name;
            row.Group = group;
            row.Count = group.Count;
            row.IsExposed = group.AcceptsFromOtherDevices;
            row.Detail = group.Pids.Count == 1
                ? Loc.F("PID {0}", group.Pids[0])
                : Loc.F("{0} processes", group.Pids.Count);
            row.Icon ??= _icons.Get(group.Path, ref iconBudget);
            target.Add(row);
        }

        // Keep the chosen program in the list while it has nothing to show, so the selection does not jump.
        if (SelectedProgram is { IsAll: false } selected && !target.Contains(selected))
        {
            selected.Count = 0;
            target.Add(selected);
        }

        Sync(Programs, target);
    }

    private void UpdateSummary(IReadOnlyList<ConnectionGroup> groups)
    {
        var all = _last.Endpoints;
        var processes = _last.Processes;
        ProgramsText = groups.Count.ToString(CultureInfo.CurrentCulture);
        ConnectedText = all.Count(e => e.Protocol == ConnectionProtocol.Tcp && e.State == TcpState.Established && !ConnectionRules.IsLocalOnly(e))
            .ToString(CultureInfo.CurrentCulture);
        ListeningText = all.Count(ConnectionRules.IsListening).ToString(CultureInfo.CurrentCulture);

        var exposed = all.Where(e => ConnectionRules.AcceptsFromOtherDevices(e, processes.GetValueOrDefault(e.Pid)?.IsWindows ?? false))
            .GroupBy(e => ConnectionProcesses.NameOf(processes.GetValueOrDefault(e.Pid), e.Pid), StringComparer.OrdinalIgnoreCase)
            .Select(g => $"{g.Key} ({string.Join(", ", g.Select(e => e.LocalPort).Distinct().Order())})")
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
        ExposedText = exposed.Count.ToString(CultureInfo.CurrentCulture);
        HasExposed = exposed.Count > 0;
        ExposedNames = exposed.Count == 0 ? null : Loc.F(
            "{0}. Other PCs and phones on your network can connect to these programs. That is normal for game servers, media sharing and remote tools; if you do not recognise one, end it or check its settings.",
            string.Join(", ", exposed));
    }

    private static void Sync<T>(ObservableCollection<T> rows, IReadOnlyList<T> target)
        where T : class
    {
        var keep = new HashSet<T>(target);
        for (int i = rows.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(rows[i]))
            {
                rows.RemoveAt(i);
            }
        }

        for (int i = 0; i < target.Count; i++)
        {
            if (i < rows.Count && ReferenceEquals(rows[i], target[i]))
            {
                continue;
            }

            int current = rows.IndexOf(target[i]);
            if (current >= 0)
            {
                rows.Move(current, i);
            }
            else
            {
                rows.Insert(i, target[i]);
            }
        }
    }

    private void OnHostResolved(object? sender, EventArgs e)
    {
        // Answers arrive on worker threads, often several at once: update the rows once on the UI thread.
        if (_hostRefreshQueued || Application.Current?.Dispatcher is not { } dispatcher)
        {
            return;
        }

        _hostRefreshQueued = true;
        dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _hostRefreshQueued = false;
            UpdateHostNames();
        });
    }

    private void UpdateHostNames()
    {
        foreach (var row in Rows)
        {
            row.HostName = ShowHostNames ? _hostNames.Get(row.Endpoint.RemoteAddress, startLookup: true) : null;
        }
    }

    private bool HasSelectionForCommand() => SelectedRow is not null;

    private bool CanEndProcess() => SelectedRow is { Process: not null } row
        && ConnectionProcesses.EndRefusal(row.Process, row.Endpoint.Pid, _processes.OwnProcessId) is null;

    private bool CanCloseConnection() => SelectedRow is { } row && _connections.CanCloseConnections && ConnectionRules.CloseRefusal(row.Endpoint) is null;

    private bool CanSearchRemote() => SelectedRow is { } row && !string.IsNullOrEmpty(row.RemoteText) && !ConnectionRules.IsLoopback(row.Endpoint.RemoteAddress);

    private bool CanOpenFileLocation() => SelectedRow?.Process?.Path is { Length: > 0 };

    [RelayCommand(CanExecute = nameof(CanEndProcess))]
    private async Task EndProcess()
    {
        if (SelectedRow is not { Process: { } process } row)
        {
            return;
        }

        string message = Loc.F("End {0} (PID {1})? All its connections close and unsaved work in it is lost.", row.Program, process.Pid);
        if (process.IsWindows)
        {
            message += "\n\n" + Loc.T("This is part of Windows. Ending it can stop networking, sound or other features until you restart.");
        }

        if (!await _dialogs.ConfirmAsync(Loc.T("End process?"), message, Loc.T("End process"), isDanger: true))
        {
            return;
        }

        var result = await _processes.EndProcessAsync(ConnectionProcesses.ToProcessInfo(process), includeChildren: false, CancellationToken.None);
        Report(result);
        await ReadNowAsync();
    }

    [RelayCommand(CanExecute = nameof(CanCloseConnection))]
    private async Task CloseConnection()
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        string remote = string.IsNullOrEmpty(row.HostName) ? row.RemoteText : $"{row.HostName} ({row.RemoteText})";
        if (!await _dialogs.ConfirmAsync(Loc.T("Close connection?"),
                Loc.F("Close the connection of {0} to {1}? The program keeps running and may connect again; something it was sending or downloading may stop half-way.", row.Program, remote),
                Loc.T("Close connection"), isDanger: true))
        {
            return;
        }

        Report(await _connections.CloseConnectionAsync(row.Endpoint, row.Process, CancellationToken.None));
        await ReadNowAsync();
    }

    [RelayCommand(CanExecute = nameof(HasSelectionForCommand))]
    private void Copy()
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        try
        {
            Clipboard.SetText(row.CopyText());
            _notifications.Success(Loc.T("Copied"), Loc.T("The connection is on the clipboard."));
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Clipboard copy failed.");
        }
    }

    [RelayCommand(CanExecute = nameof(CanSearchRemote))]
    private void SearchRemote()
    {
        if (SelectedRow?.Endpoint.RemoteAddress is { } address)
        {
            Report(_connections.SearchAddressOnline(address), quietSuccess: true);
        }
    }

    [RelayCommand(CanExecute = nameof(CanOpenFileLocation))]
    private void OpenFileLocation()
    {
        if (SelectedRow?.Process is { } process)
        {
            Report(_connections.OpenFileLocation(process), quietSuccess: true);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelectionForCommand))]
    private void GoToProcess()
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        try
        {
            // Selects the row (clearing any search) and then shows the Processes tab.
            (App.Services.GetService(typeof(ProcessesViewModel)) as ProcessesViewModel)?.SelectProcess(row.Endpoint.Pid);
            (App.Services.GetService(typeof(Wpf.Ui.INavigationService)) as Wpf.Ui.INavigationService)?.Navigate(typeof(Views.Pages.ProcessesPage));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not go to the process of a connection.");
        }
    }

    [RelayCommand]
    private void ShowEverything()
    {
        SearchText = null;
        SelectedFilterIndex = 0;
        HideLoopback = false;
        SelectedProgram = AllPrograms;
    }

    private void Report(Core.Common.OperationResult result, bool quietSuccess = false)
    {
        if (!result.Success)
        {
            _notifications.Error(Loc.T("Could not complete"), result.Message);
        }
        else if (!quietSuccess || result.Message.StartsWith("Demo mode", StringComparison.Ordinal))
        {
            _notifications.Success(Loc.T("Done"), result.Message);
        }
    }

    private void NotifyCommands()
    {
        EndProcessCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(EndProcessNote));
        CloseConnectionCommand.NotifyCanExecuteChanged();
        SearchRemoteCommand.NotifyCanExecuteChanged();
        OpenFileLocationCommand.NotifyCanExecuteChanged();
    }

    protected override void OnBusyError(Exception exception) => _logger.LogError(exception, "Connections page operation failed.");
}
