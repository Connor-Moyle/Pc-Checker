using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Helpers;
using PcChecker.App.Localization;
using PcChecker.App.Services;
using PcChecker.App.Views.Pages;
using PcChecker.Core.AppHistory;
using PcChecker.Core.Common;
using PcChecker.Core.History;
using PcChecker.Core.Settings;
using Wpf.Ui;

namespace PcChecker.App.ViewModels;

/// <summary>
/// App history page (Task manager): processor time, network data and disk traffic per app over a date range, from
/// Windows' own records or PC Checker's own recording. Loading happens in <see cref="IAppHistoryService"/> off the UI
/// thread; changing the range or the search only re-adds the figures already loaded.
/// </summary>
public sealed partial class AppHistoryViewModel : ViewModelBase
{
    private const string ActivitySettingsUri = "ms-settings:privacy-activityhistory";
    private const int IconsPerPass = 80;

    private readonly IAppHistoryService _appHistory;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly INavigationService _navigation;
    private readonly ILogger<AppHistoryViewModel> _logger;
    private readonly ProcessIconCache _icons = new();
    private AppHistoryResult? _result;

    [ObservableProperty]
    private string _subtitle = Loc.T("How much each app used the processor, the network and the disk.");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSinceDate))]
    private int _selectedRangeIndex = 2;

    [ObservableProperty]
    private DateTime? _sinceDate = DateTime.Today.AddDays(-14);

    [ObservableProperty]
    private int _selectedSourceIndex;

    [ObservableProperty]
    private string? _searchText;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenFileLocationCommand))]
    [NotifyCanExecuteChangedFor(nameof(GoToProcessesCommand))]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private AppHistoryRow? _selectedRow;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyRange))]
    [NotifyPropertyChangedFor(nameof(ShowList))]
    private bool _hasLoaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyRange))]
    [NotifyPropertyChangedFor(nameof(ShowList))]
    private int _rangeAppCount;

    [ObservableProperty]
    private string _topCpuName = "—";

    [ObservableProperty]
    private string _topCpuDetail = Loc.T("Processor time");

    [ObservableProperty]
    private string _topNetworkName = "—";

    [ObservableProperty]
    private string _topNetworkDetail = Loc.T("Sent and received");

    [ObservableProperty]
    private string _totalDataText = "—";

    [ObservableProperty]
    private string _totalDataDetail = Loc.T("All apps, sent and received");

    [ObservableProperty]
    private string _appCountText = "—";

    [ObservableProperty]
    private string? _sourceMessage;

    [ObservableProperty]
    private string? _fallbackMessage;

    [ObservableProperty]
    private string _emptyTitle = Loc.T("No app use in this range yet");

    [ObservableProperty]
    private string _emptyMessage = string.Empty;

    [ObservableProperty]
    private bool _canTurnOnRecording;

    [ObservableProperty]
    private string _matchText = string.Empty;

    public AppHistoryViewModel(ISettingsService settings, IAppHistoryService appHistory, IDialogService dialogs,
        INotificationService notifications, INavigationService navigation, ILogger<AppHistoryViewModel> logger)
        : base(settings)
    {
        _appHistory = appHistory;
        _dialogs = dialogs;
        _notifications = notifications;
        _navigation = navigation;
        _logger = logger;
    }

    public IReadOnlyList<string> RangeOptions { get; } =
        [Loc.T("Today"), Loc.T("Last 7 days"), Loc.T("Last 30 days"), Loc.T("Since a date")];

    public IReadOnlyList<string> SourceOptions { get; } =
        [Loc.T("Best available"), Loc.T("Windows' records"), Loc.T("PC Checker's recording")];

    public ObservableCollection<AppHistoryRow> Rows { get; } = [];

    public bool IsSinceDate => SelectedRangeIndex == (int)AppHistoryRangeKind.SinceDate;

    public bool HasSelection => SelectedRow is not null;

    public bool HasSourceMessage => !string.IsNullOrWhiteSpace(SourceMessage);

    public bool HasFallbackMessage => !string.IsNullOrWhiteSpace(FallbackMessage);

    public bool ShowList => HasLoaded && RangeAppCount > 0;

    public bool ShowEmptyRange => HasLoaded && RangeAppCount == 0;

    public bool HasNoMatches => ShowList && Rows.Count == 0;

    public override Task InitializeAsync(CancellationToken cancellationToken) => LoadAsync(false, cancellationToken);

    public override Task RefreshAsync(CancellationToken cancellationToken) => LoadAsync(true, cancellationToken);

    protected override void OnBusyError(Exception exception) => _logger.LogError(exception, "App history page failed.");

    [RelayCommand]
    private Task RefreshNowAsync() => LoadAsync(true, CancellationToken.None);

    partial void OnSelectedRangeIndexChanged(int value) => ApplyRange();

    partial void OnSinceDateChanged(DateTime? value) => ApplyRange();

    partial void OnSearchTextChanged(string? value) => ApplyRange();

    partial void OnSelectedSourceIndexChanged(int value)
    {
        if (IsInitialized)
        {
            _ = LoadAsync(false, CancellationToken.None);
        }
    }

    partial void OnSourceMessageChanged(string? value) => OnPropertyChanged(nameof(HasSourceMessage));

    partial void OnFallbackMessageChanged(string? value) => OnPropertyChanged(nameof(HasFallbackMessage));

    private AppUsageSourceChoice SourceChoice => SelectedSourceIndex switch
    {
        1 => AppUsageSourceChoice.WindowsRecords,
        2 => AppUsageSourceChoice.OwnRecording,
        _ => AppUsageSourceChoice.Automatic,
    };

    private Task LoadAsync(bool force, CancellationToken outer) => RunBusyAsync(async busyToken =>
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(busyToken, outer);
        var progress = new Progress<ProgressUpdate>(p => BusyText = Loc.T(p.Stage) + (p.Detail is null ? string.Empty : "\n" + Loc.T(p.Detail)));
        _result = await _appHistory.LoadAsync(SourceChoice, force, progress, linked.Token);
        ShowSource(_result);
        HasLoaded = true;
        ApplyRange();
    }, Loc.T("Reading app history…"));

    /// <summary>Re-adds the loaded records for the chosen range and search, and fills the tiles.</summary>
    private void ApplyRange()
    {
        if (_result is not { } result)
        {
            return;
        }

        var kind = (AppHistoryRangeKind)Math.Clamp(SelectedRangeIndex, 0, 3);
        DateOnly? since = SinceDate is { } date ? DateOnly.FromDateTime(date) : null;
        var range = AppUsageAggregator.Resolve(kind, since, DateTimeOffset.Now, TimeZoneInfo.Local);
        var summaries = AppUsageAggregator.Summarize(result.Records, result.Apps, range);
        var totals = AppUsageAggregator.Totals(summaries);
        RangeAppCount = summaries.Count;
        FillTiles(totals);

        var selectedKey = SelectedRow?.Summary.AppKey;
        Rows.Clear();
        int budget = IconsPerPass;
        foreach (var summary in AppUsageAggregator.Filter(summaries, SearchText))
        {
            var publisher = result.Apps.TryGetValue(summary.AppKey, out var identity) ? identity.Publisher : null;
            var row = new AppHistoryRow(summary, publisher) { Icon = _icons.Get(summary.Path, ref budget) };
            Rows.Add(row);
        }

        SelectedRow = Rows.FirstOrDefault(r => string.Equals(r.Summary.AppKey, selectedKey, StringComparison.OrdinalIgnoreCase));
        OnPropertyChanged(nameof(HasNoMatches));
        MatchText = string.IsNullOrWhiteSpace(SearchText) ? string.Empty : Loc.F("{0} of {1} apps", Rows.Count, summaries.Count);
        Subtitle = AppHistoryText.RangeLabel(kind, since) + " · " + SourceName(result.Source);
        UpdateEmptyState(result);
    }

    private void FillTiles(AppUsageTotals totals)
    {
        TopCpuName = totals.TopCpu?.Name ?? "—";
        TopCpuDetail = totals.TopCpu is { } cpu ? Loc.F("{0} of processor time", AppHistoryText.CpuTime(cpu.CpuTime)) : Loc.T("Processor time");
        TopNetworkName = totals.TopNetwork?.Name ?? "—";
        TopNetworkDetail = totals.TopNetwork is { } net
            ? Loc.F("{0} sent and received", AppHistoryText.Bytes(net.NetworkBytes))
            : (totals.TotalNetworkBytes is null ? Loc.T("Only Windows' records have this") : Loc.T("Sent and received"));
        TotalDataText = AppHistoryText.Bytes(totals.TotalNetworkBytes);
        TotalDataDetail = totals.TotalMeteredBytes is > 0
            ? Loc.F("{0} of it on a metered connection", AppHistoryText.Bytes(totals.TotalMeteredBytes))
            : Loc.T("All apps, sent and received");
        AppCountText = totals.AppCount.ToString(CultureInfo.CurrentCulture);
    }

    private void ShowSource(AppHistoryResult result)
    {
        string since = result.OldestRecord is { } oldest
            ? " " + Loc.F("Data from {0} on.", oldest.ToLocalTime().ToString("d", CultureInfo.CurrentCulture))
            : string.Empty;
        SourceMessage = result.Source == AppUsageSource.WindowsRecords
            ? Loc.T("Showing Windows' own records (the System Resource Usage Monitor), the same data Task Manager's App history uses. Windows writes them about once an hour.") + since
            : Loc.T("Showing PC Checker's own recording, made once a minute while PC Checker runs. It has no network figures.") + since;

        FallbackMessage = result.WindowsRecordsFailure is { } failure
            ? Loc.T(failure.Message) + " " + (result.Source == AppUsageSource.OwnRecording
                ? Loc.T("PC Checker's own recording is shown instead.")
                : Loc.T("Choose PC Checker's recording to see what PC Checker added up itself."))
            : null;
        if (result.Warnings.Count > 0)
        {
            FallbackMessage = string.Join(" ", new[] { FallbackMessage }.Concat(result.Warnings.Select(Loc.T)).Where(t => !string.IsNullOrWhiteSpace(t)));
        }
    }

    private void UpdateEmptyState(AppHistoryResult result)
    {
        CanTurnOnRecording = result.Source == AppUsageSource.OwnRecording && !result.OwnRecordingOn;
        if (result.Source == AppUsageSource.WindowsRecords && result.WindowsRecordsFailure is not null)
        {
            EmptyTitle = Loc.T("Windows' records could not be read");
            EmptyMessage = Loc.T("Choose \"Best available\" or \"PC Checker's recording\" above to see PC Checker's own figures, or press Refresh to try again.");
        }
        else if (CanTurnOnRecording)
        {
            EmptyTitle = Loc.T("Recording is off");
            EmptyMessage = Loc.T("PC Checker only adds up app use while History recording is on. Turn it on, leave PC Checker open or in the tray, and come back later.");
        }
        else if (result.Records.Count == 0)
        {
            EmptyTitle = Loc.T("No app history yet");
            EmptyMessage = Loc.T("PC Checker adds up app use once a minute while it runs. Leave it open or in the tray and come back in a little while.");
        }
        else
        {
            EmptyTitle = Loc.T("No app use in this range");
            EmptyMessage = Loc.T("Nothing was recorded for this period. Pick a longer range to see more.");
        }
    }

    private static string SourceName(AppUsageSource source) => source == AppUsageSource.WindowsRecords
        ? Loc.T("Windows' records")
        : Loc.T("PC Checker's recording");

    [RelayCommand]
    private void ShowLast30Days() => SelectedRangeIndex = (int)AppHistoryRangeKind.Last30Days;

    [RelayCommand(CanExecute = nameof(CanOpenFileLocation))]
    private void OpenFileLocation()
    {
        if (SelectedRow?.Path is not { } path)
        {
            return;
        }

        var result = _appHistory.OpenFileLocation(path);
        if (!result.Success)
        {
            _notifications.Warning(Loc.T("Could not open the file location"), result.Message);
        }
    }

    private bool CanOpenFileLocation() => SelectedRow?.HasPath == true;

    [RelayCommand(CanExecute = nameof(CanGoToProcesses))]
    private void GoToProcesses()
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        try
        {
            // Searches for the program on the Processes tab, so its running copies (if any) are listed.
            if (App.Services.GetService(typeof(ProcessesViewModel)) is ProcessesViewModel processes)
            {
                processes.SearchText = row.ProcessSearchText;
            }

            _navigation.Navigate(typeof(ProcessesPage));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not go to the Processes page from App history.");
        }
    }

    private bool CanGoToProcesses() => SelectedRow is not null;

    [RelayCommand]
    private async Task DeleteUsageHistoryAsync()
    {
        var message = Loc.T("This deletes the app usage PC Checker added up itself, for every day. It cannot be undone.")
            + "\n\n" + Loc.T("Windows' own records are not touched: only Windows can clear them. To limit what Windows keeps, open Settings → Privacy & security → Activity history.");
        if (!await _dialogs.ConfirmAsync(Loc.T("Delete usage history?"), message, Loc.T("Delete"), isDanger: true))
        {
            return;
        }

        await RunBusyAsync(async ct =>
        {
            var result = await _appHistory.DeleteOwnRecordingAsync(ct);
            if (result.Success)
            {
                _notifications.Success(Loc.T("Usage history deleted"), result.Message);
            }
            else
            {
                _notifications.Error(Loc.T("Usage history not deleted"), result.Message);
            }
        }, Loc.T("Deleting usage history…"), cancellable: false);

        if (_result?.Source == AppUsageSource.OwnRecording)
        {
            await LoadAsync(true, CancellationToken.None);
        }
    }

    [RelayCommand]
    private void OpenActivitySettings()
    {
        var result = ShellLauncher.Open(ActivitySettingsUri);
        if (!result.Success)
        {
            ErrorMessage = result.Message;
        }
    }

    [RelayCommand]
    private void OpenHistorySettings() => _navigation.Navigate(typeof(SettingsPage));

    [RelayCommand]
    private async Task TurnOnRecordingAsync()
    {
        Settings.Current.History ??= new HistorySettings();
        Settings.Current.History.Enabled = true;
        try
        {
            await Settings.SaveAsync();
            _appHistory.Start();
            _notifications.Success(Loc.T("Recording app use"), Loc.T("PC Checker now adds up app use while it is open or in the tray."));
            await LoadAsync(true, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Turning on history recording from App history failed.");
            ErrorMessage = ex.Message;
        }
    }
}
