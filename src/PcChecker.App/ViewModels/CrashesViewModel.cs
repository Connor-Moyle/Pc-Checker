using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Localization;
using PcChecker.App.Services;
using PcChecker.App.Views.Pages;
using PcChecker.Core.Assistant;
using PcChecker.Core.Common;
using PcChecker.Core.Crashes;
using PcChecker.Core.Drivers;
using PcChecker.Core.Drivers.Sources;
using PcChecker.Core.GameRuntimes;
using PcChecker.Core.Games;
using PcChecker.Core.Logging;
using PcChecker.Core.Optimization;
using PcChecker.Core.Overclocking;
using PcChecker.Core.Settings;
using PcChecker.Core.Storage;
using Wpf.Ui;

namespace PcChecker.App.ViewModels;

/// <summary>The services the Crashes page's fix buttons use (grouped to keep the constructor readable).</summary>
public sealed record CrashFixServices(
    IDialogService Dialogs,
    IOverclockService Overclock,
    IOptimizationService Optimization,
    IStorageToolsService Storage,
    IDriverService Drivers,
    IGameLibraryService Games,
    IActionJournal Journal,
    GameFolderScanHandOff GameScans,
    RuntimeFamilyHandOff Runtimes,
    DriverFocusHandOff DriverFocus,
    DriverSearchCache DriverSearch);

/// <summary>
/// Crashes page: blue screens, sudden shutdowns, graphics driver resets and app crashes from the last 90 days as a
/// timeline grouped by day, a crash summary card, and a details pane that explains each crash (what happened, what the
/// stop-code details say, what probably caused it, patterns across crashes, what happened before it) with ranked fix
/// buttons. Reading is automatic; every fix that changes the PC asks first and is journaled.
/// </summary>
public sealed partial class CrashesViewModel : ViewModelBase
{
    private readonly ICrashService _crashes;
    private readonly IPcAssistantService _assistant;
    private readonly INavigationService _navigation;
    private readonly INotificationService _notifications;
    private readonly CrashFixServices _fix;
    private readonly ILogger<CrashesViewModel> _logger;
    private readonly List<CrashRow> _allRows = [];
    private CrashReport? _report;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoCrashes))]
    [NotifyPropertyChangedFor(nameof(HasCrashes))]
    private bool _hasScanned;

    [ObservableProperty]
    private string _subtitle = Loc.T("Blue screens, sudden shutdowns, driver resets and app crashes from the last 90 days.");

    [ObservableProperty] private string _blueScreensText = "—";
    [ObservableProperty] private string _suddenShutdownsText = "—";
    [ObservableProperty] private string _driverResetsText = "—";
    [ObservableProperty] private string _appCrashesText = "—";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoCrashes))]
    [NotifyPropertyChangedFor(nameof(HasCrashes))]
    private int _totalCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoMatches))]
    private int _visibleCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(HasNoSelection))]
    [NotifyCanExecuteChangedFor(nameof(CopyDetailsCommand))]
    [NotifyCanExecuteChangedFor(nameof(AskClaudeCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenEventViewerCommand))]
    [NotifyCanExecuteChangedFor(nameof(SearchOnlineCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleSolvedCommand))]
    private CrashRow? _selectedRow;

    [ObservableProperty]
    private CrashDetails? _details;

    [ObservableProperty] private CrashFilterOption _selectedKind;
    [ObservableProperty] private CrashFilterOption _selectedRange;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAppFilter))]
    [NotifyPropertyChangedFor(nameof(AppFilterText))]
    private string? _appFilter;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarnings))]
    private string? _warnings;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWhea))]
    private string? _wheaText;

    public CrashesViewModel(ISettingsService settings, ICrashService crashes, IPcAssistantService assistant, INavigationService navigation,
        INotificationService notifications, CrashFixServices fix, ILogger<CrashesViewModel> logger)
        : base(settings)
    {
        _crashes = crashes;
        _assistant = assistant;
        _navigation = navigation;
        _notifications = notifications;
        _fix = fix;
        _logger = logger;
        KindOptions =
        [
            new(Loc.T("All types"), 0, []),
            new(Loc.T("Blue screens"), 0, [CrashKind.BlueScreen]),
            new(Loc.T("Sudden shutdowns"), 0, [CrashKind.SuddenShutdown]),
            new(Loc.T("Driver resets"), 0, [CrashKind.DriverReset]),
            new(Loc.T("App crashes and hangs"), 0, [CrashKind.AppCrash, CrashKind.AppHang]),
        ];
        RangeOptions =
        [
            new(Loc.T("Last 7 days"), 7, []),
            new(Loc.T("Last 30 days"), 30, []),
            new(Loc.T("Last 90 days"), CrashRules.WindowDays, []),
        ];
        _selectedKind = KindOptions[0];
        _selectedRange = RangeOptions[^1];
        RowsView = CollectionViewSource.GetDefaultView(Rows);
        RowsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(CrashRow.DayText)));
        _fix.DriverSearch.Changed += (_, _) => DispatcherHelper.PostToUi(RefreshDriverOffers);
    }

    public ObservableCollection<CrashRow> Rows { get; } = [];

    /// <summary>The timeline, grouped by day.</summary>
    public ICollectionView RowsView { get; }

    public ObservableCollection<AppCrashRow> TopApps { get; } = [];

    public IReadOnlyList<CrashFilterOption> KindOptions { get; }

    public IReadOnlyList<CrashFilterOption> RangeOptions { get; }

    public bool HasCrashes => HasScanned && TotalCount > 0;

    public bool HasNoCrashes => HasScanned && TotalCount == 0;

    public bool HasNoMatches => HasCrashes && VisibleCount == 0;

    public bool HasTopApps => TopApps.Count > 0;

    public bool HasSelection => SelectedRow is not null;

    public bool HasNoSelection => SelectedRow is null;

    public bool HasWarnings => !string.IsNullOrWhiteSpace(Warnings);

    public bool HasWhea => !string.IsNullOrWhiteSpace(WheaText);

    public bool HasAppFilter => !string.IsNullOrWhiteSpace(AppFilter);

    public string AppFilterText => HasAppFilter ? Loc.F("Showing only {0}", AppFilter!) : string.Empty;

    /// <summary>Scans straight away in both modes: it only reads the event logs and dump headers.</summary>
    public override Task InitializeAsync(CancellationToken cancellationToken) => RunScanAsync(cancellationToken);

    public override Task RefreshAsync(CancellationToken cancellationToken) => RunScanAsync(cancellationToken);

    protected override void OnBusyError(Exception exception) => _logger.LogError(exception, "Crashes page operation failed.");

    [RelayCommand]
    private Task RefreshNowAsync() => RunScanAsync(CancellationToken.None);

    private Task RunScanAsync(CancellationToken outer) => RunBusyAsync(async busyToken =>
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(busyToken, outer);
        var progress = new Progress<ProgressUpdate>(p => BusyText = Loc.T(p.Stage));
        var report = await _crashes.ScanAsync(progress, linked.Token);
        var tr = Loc.Current;
        var built = await Task.Run(() => report.Events
            .Select(e => (Crash: e, Advice: CrashAdvice.For(e, report.OverclockApplied, tr), Insight: CrashInsightBuilder.Build(e, report.OverclockApplied, allEvents: report.Events)))
            .ToList(), linked.Token);
        Apply(report, built);
        _ = LoadInsightsAsync(report);
    }, Loc.T("Reading crash reports…"));

    private void Apply(CrashReport report, IReadOnlyList<(CrashEvent Crash, CrashAdviceText Advice, CrashInsight Insight)> built)
    {
        _report = report;
        var selectedId = SelectedRow?.Crash.Id;
        var solved = SolvedIds();
        _allRows.Clear();
        _driverDetails.Clear();
        foreach (var (e, advice, insight) in built)
        {
            _allRows.Add(new CrashRow(e, advice, insight) { IsSolved = solved.Contains(e.Id) });
        }

        var summary = CrashRules.Summarize(report.Events, DateTimeOffset.Now);
        BlueScreensText = summary.BlueScreens.ToString(CultureInfo.CurrentCulture);
        SuddenShutdownsText = summary.SuddenShutdowns.ToString(CultureInfo.CurrentCulture);
        DriverResetsText = summary.DriverResets.ToString(CultureInfo.CurrentCulture);
        AppCrashesText = summary.AppCrashes.ToString(CultureInfo.CurrentCulture);

        TopApps.Clear();
        foreach (var group in CrashRules.AppGroups(report.Events, 6))
        {
            TopApps.Add(new AppCrashRow(group));
        }

        OnPropertyChanged(nameof(HasTopApps));
        WheaText = report.WheaErrors is > 0 and var n
            ? Loc.F("Windows logged {0} hardware error reports (WHEA) in the last 30 days. The Health page explains what they point at.", n)
            : null;
        Warnings = report.Warnings.Count == 0 ? null : string.Join(" ", report.Warnings.Select(w => w.Text(Loc.Current)));
        TotalCount = _allRows.Count;
        HasScanned = true;
        Subtitle = TotalCount == 0
            ? Loc.T("No crashes in the last 90 days.")
            : Loc.F("Checked {0}. Newest first; click one to see what it means.", report.Captured.ToLocalTime().ToString("t", CultureInfo.CurrentCulture));
        UpdateSummaryCard();
        ApplyFilter(selectedId);
    }

    partial void OnSelectedKindChanged(CrashFilterOption value) => ApplyFilter(SelectedRow?.Crash.Id);

    partial void OnSelectedRangeChanged(CrashFilterOption value) => ApplyFilter(SelectedRow?.Crash.Id);

    partial void OnAppFilterChanged(string? value) => ApplyFilter(SelectedRow?.Crash.Id);

    partial void OnSelectedRowChanged(CrashRow? value)
    {
        RefreshDetails();
        if (value is not null)
        {
            _ = InspectDriverAsync(value);
        }
    }

    private void ApplyFilter(string? keepSelectedId)
    {
        var from = DateTimeOffset.Now.AddDays(-Math.Max(1, SelectedRange?.Days ?? CrashRules.WindowDays));
        var visible = CrashRules.Filter(_allRows.Select(r => r.Crash), SelectedKind?.Kinds ?? [], from, AppFilter)
            .Select(e => e.Id)
            .ToHashSet(StringComparer.Ordinal);
        Rows.Clear();
        foreach (var row in _allRows.Where(r => visible.Contains(r.Crash.Id)))
        {
            Rows.Add(row);
        }

        VisibleCount = Rows.Count;
        SelectedRow = Rows.FirstOrDefault(r => r.Crash.Id == keepSelectedId) ?? Rows.FirstOrDefault();
    }

    /// <summary>Rebuilds the details pane for the selected crash (after its insight changed).</summary>
    private void RefreshDetails()
    {
        if (SelectedRow is not { } row)
        {
            Details = null;
            return;
        }

        var fixes = row.Insight.Fixes.Select((f, i) => new CrashFixRow(f, row, i + 1)).ToList();
        Details = new CrashDetails(row, fixes);
    }

    [RelayCommand]
    private void ClearFilters()
    {
        AppFilter = null;
        SelectedKind = KindOptions[0];
        SelectedRange = RangeOptions[^1];
    }

    [RelayCommand]
    private void ShowApp(AppCrashRow? app)
    {
        if (app is null)
        {
            return;
        }

        SelectedKind = KindOptions[^1];
        AppFilter = app.AppName;
    }

    [RelayCommand]
    private void OpenPage(CrashLink? link)
    {
        if (link is not null && PageHelpLocator.PageTypeOf(link.PageKey) is { } page)
        {
            _navigation.Navigate(page);
        }
    }

    [RelayCommand]
    private void OpenHealth() => _navigation.Navigate(typeof(HealthPage));

    [RelayCommand]
    private void OpenReliabilityMonitor()
    {
        var psi = new ProcessStartInfo("perfmon.exe") { UseShellExecute = false };
        psi.ArgumentList.Add("/rel");
        Start(psi, Loc.T("Reliability Monitor"));
    }

    [RelayCommand(CanExecute = nameof(HasEventSource))]
    private void OpenEventViewer()
    {
        var crash = SelectedRow?.Crash;
        string log = crash?.LogName ?? "System";
        var psi = new ProcessStartInfo("eventvwr.exe") { UseShellExecute = false };
        psi.ArgumentList.Add("/c:" + log);
        if (crash?.EventId is { } id)
        {
            psi.ArgumentList.Add(string.Create(CultureInfo.InvariantCulture, $"/f:*[System[EventID={id}]]"));
        }

        Start(psi, Loc.T("Event Viewer"));
    }

    private bool HasEventSource() => SelectedRow?.Crash.LogName is not null;

    [RelayCommand]
    private void OpenDumpFolder()
    {
        var dump = SelectedRow?.Crash.DumpPath;
        if (dump is not null && File.Exists(dump))
        {
            var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            psi.ArgumentList.Add("/select," + dump);
            Start(psi, Loc.T("the dump folder"));
            return;
        }

        var folder = _crashes.MinidumpFolder;
        if (!Directory.Exists(folder))
        {
            _notifications.Info(Loc.T("No dump folder yet"), Loc.T("Windows creates it the first time it saves a small memory dump after a blue screen."));
            return;
        }

        var result = ShellLauncher.Open(folder);
        if (!result.Success)
        {
            _notifications.Warning(Loc.T("Could not open"), result.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void CopyDetails()
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        try
        {
            Clipboard.SetText(CrashReportText.Build(row.Insight));
            _notifications.Success(Loc.T("Copied"), Loc.T("The full crash report was copied. Paste it into a support request or forum post."));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Copying crash details failed.");
            _notifications.Warning(Loc.T("Could not copy"), ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void AskClaude()
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        AssistantPrompt.Set(CrashReportText.AskPrompt(row.Insight));
        bool configured;
        try
        {
            configured = _assistant.IsConfigured;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Assistant configuration could not be read.");
            configured = false;
        }

        if (!configured)
        {
            _notifications.Warning(Loc.T("Ask Claude is not set up yet"), Loc.T("Add an Anthropic API key on the Ask Claude page; your question is ready to send once it is."));
        }

        _navigation.Navigate(typeof(AssistantPage));
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void SearchOnline()
    {
        if (SelectedRow?.Crash is not { } crash)
        {
            return;
        }

        string query = crash.Kind switch
        {
            CrashKind.BlueScreen when crash.BugCheckCode is { } code => $"{StopCodeCatalog.NameOf(code)} {BugCheckFormat.Code(code)}"
                + (crash.Blame is { IsWindowsModule: false } b ? " " + b.Module : string.Empty),
            CrashKind.DriverReset => $"{crash.Driver ?? "display driver"} stopped responding and has successfully recovered 4101",
            CrashKind.AppCrash or CrashKind.AppHang => $"{crash.AppName} crash {(crash.ExceptionCode is { } ex ? BugCheckFormat.Exception(ex) : string.Empty)} {crash.ModuleName}".Trim(),
            _ => "Kernel-Power 41 unexpected shutdown",
        };
        OpenUrl("https://www.bing.com/search?q=" + Uri.EscapeDataString(query));
    }

    [RelayCommand]
    private void OpenDocs()
    {
        if (Details?.DocsUrl is { } url)
        {
            OpenUrl(url);
        }
    }

    [RelayCommand]
    private void OpenMemTest() => OpenUrl("https://www.memtest86.com/");

    private void OpenUrl(string url)
    {
        var result = ShellLauncher.Open(url);
        if (!result.Success)
        {
            _notifications.Warning(Loc.T("Could not open"), result.Message);
        }
    }

    private void Start(ProcessStartInfo psi, string what)
    {
        if (!PlatformGuard.IsWindows)
        {
            return;
        }

        try
        {
            using var _ = Process.Start(psi);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open {What}.", what);
            _notifications.Warning(Loc.T("Could not open"), Loc.F("{0} could not be opened: {1}", what, ex.Message));
        }
    }
}
