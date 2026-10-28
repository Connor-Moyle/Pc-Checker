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
using PcChecker.Core.Settings;
using Wpf.Ui;

namespace PcChecker.App.ViewModels;

/// <summary>
/// Crashes page: blue screens, sudden shutdowns, graphics driver resets and app crashes from the last 90 days as a
/// timeline grouped by day, with a details pane (what it means, probably caused by, first steps, related pages) and the
/// most crashing apps. Read-only: it opens Windows tools and other pages but never changes the PC.
/// </summary>
public sealed partial class CrashesViewModel : ViewModelBase
{
    private readonly ICrashService _crashes;
    private readonly IPcAssistantService _assistant;
    private readonly INavigationService _navigation;
    private readonly INotificationService _notifications;
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
    [NotifyPropertyChangedFor(nameof(Details))]
    [NotifyCanExecuteChangedFor(nameof(CopyDetailsCommand))]
    [NotifyCanExecuteChangedFor(nameof(AskClaudeCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenEventViewerCommand))]
    [NotifyCanExecuteChangedFor(nameof(SearchOnlineCommand))]
    private CrashRow? _selectedRow;

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
        INotificationService notifications, ILogger<CrashesViewModel> logger)
        : base(settings)
    {
        _crashes = crashes;
        _assistant = assistant;
        _navigation = navigation;
        _notifications = notifications;
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

    /// <summary>The details pane for the selected crash.</summary>
    public CrashDetails? Details => SelectedRow is { } row ? new CrashDetails(row) : null;

    /// <summary>Scans straight away in both modes: it only reads the event logs and dump headers.</summary>
    public override Task InitializeAsync(CancellationToken cancellationToken) => RunScanAsync(cancellationToken);

    public override Task RefreshAsync(CancellationToken cancellationToken) => RunScanAsync(cancellationToken);

    protected override void OnBusyError(Exception exception) => _logger.LogError(exception, "Crashes page operation failed.");

    [RelayCommand]
    private Task RefreshNowAsync() => RunScanAsync(CancellationToken.None);

    private Task RunScanAsync(CancellationToken outer) => RunBusyAsync(async busyToken =>
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(busyToken, outer);
        var progress = new Progress<ProgressUpdate>(p => BusyText = p.Stage);
        var report = await _crashes.ScanAsync(progress, linked.Token);
        Apply(report);
    }, Loc.T("Reading crash reports…"));

    private void Apply(CrashReport report)
    {
        _report = report;
        var selectedId = SelectedRow?.Crash.Id;
        _allRows.Clear();
        _allRows.AddRange(report.Events.Select(e => new CrashRow(e, CrashAdvice.For(e, report.OverclockApplied))));

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
        Warnings = report.Warnings.Count == 0 ? null : string.Join(" ", report.Warnings);
        TotalCount = _allRows.Count;
        HasScanned = true;
        Subtitle = TotalCount == 0
            ? Loc.T("No crashes in the last 90 days.")
            : Loc.F("Checked {0}. Newest first; click one to see what it means.", report.Captured.ToLocalTime().ToString("t", CultureInfo.CurrentCulture));
        ApplyFilter(selectedId);
    }

    partial void OnSelectedKindChanged(CrashFilterOption value) => ApplyFilter(SelectedRow?.Crash.Id);

    partial void OnSelectedRangeChanged(CrashFilterOption value) => ApplyFilter(SelectedRow?.Crash.Id);

    partial void OnAppFilterChanged(string? value) => ApplyFilter(SelectedRow?.Crash.Id);

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
            Clipboard.SetText(CrashAdvice.DetailsText(row.Crash, row.Advice));
            _notifications.Success(Loc.T("Copied"), Loc.T("The crash details were copied. Paste them into a support request or forum post."));
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

        AssistantPrompt.Set(CrashAdvice.AskPrompt(row.Crash, row.Advice));
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
        var result = ShellLauncher.Open("https://www.bing.com/search?q=" + Uri.EscapeDataString(query));
        if (!result.Success)
        {
            _notifications.Warning(Loc.T("Could not open"), result.Message);
        }
    }

    [RelayCommand]
    private void OpenDocs()
    {
        if (Details?.DocsUrl is not { } url)
        {
            return;
        }

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

/// <summary>What the details pane shows for one crash.</summary>
public sealed class CrashDetails
{
    public CrashDetails(CrashRow row)
    {
        var crash = row.Crash;
        var advice = row.Advice;
        Title = advice.Title;
        WhenText = crash.Time.ToLocalTime().ToString("F", CultureInfo.CurrentCulture);
        Source = crash.Source;
        Explanation = advice.Explanation;
        LikelyCause = advice.LikelyCause;
        Steps = advice.Steps;
        Pages = advice.Pages;
        DocsUrl = advice.DocsUrl;
        HasDump = crash.DumpPath is not null;
        IsBestGuess = crash.Blame is not null;

        var facts = new List<string>();
        if (crash.BugCheckCode is { } code)
        {
            facts.Add(Loc.F("Stop code: {0}", BugCheckFormat.WithParameters(code, crash.Parameters)));
        }

        if (crash.Blame is { } blame)
        {
            facts.Add(Loc.F("Probably caused by: {0}", blame.Module));
        }

        if (crash.Driver is { Length: > 0 } driver)
        {
            facts.Add(Loc.F("Driver: {0}", driver));
        }

        if (crash.AppName is { Length: > 0 })
        {
            facts.Add(Loc.F("Program: {0} {1}", crash.AppName, crash.AppVersion ?? string.Empty).TrimEnd());
        }

        if (crash.AppPath is { Length: > 0 } path)
        {
            facts.Add(Loc.F("Path: {0}", path));
        }

        if (crash.ModuleName is { Length: > 0 } module)
        {
            facts.Add(Loc.F("Faulting file: {0} {1}", module, crash.ModuleVersion ?? string.Empty).TrimEnd());
        }

        if (crash.ExceptionCode is { } ex)
        {
            facts.Add(Loc.F("Exception: {0}", ExceptionCodeCatalog.Describe(ex)));
        }

        if (crash.OsBuild is { } build)
        {
            facts.Add(Loc.F("Windows build: {0}", build));
        }

        if (crash.DumpPath is { Length: > 0 } dump)
        {
            facts.Add(Loc.F("Dump file: {0}", dump));
        }

        if (crash.Modules.Count > 0)
        {
            facts.Add(Loc.F("Drivers loaded at the time: {0}", crash.Modules.Count));
        }

        Facts = facts;
    }

    public string Title { get; }

    public string WhenText { get; }

    public string Source { get; }

    public string Explanation { get; }

    public string? LikelyCause { get; }

    public bool HasLikelyCause => !string.IsNullOrWhiteSpace(LikelyCause);

    public bool IsBestGuess { get; }

    public IReadOnlyList<string> Steps { get; }

    public IReadOnlyList<CrashLink> Pages { get; }

    public bool HasPages => Pages.Count > 0;

    public string? DocsUrl { get; }

    public bool HasDocs => DocsUrl is not null;

    public bool HasDump { get; }

    public IReadOnlyList<string> Facts { get; }
}
