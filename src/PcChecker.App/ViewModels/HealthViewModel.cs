using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Services;
using PcChecker.App.Views.Pages;
using PcChecker.Core.Assistant;
using PcChecker.Core.Common;
using PcChecker.Core.Health;
using PcChecker.Core.History;
using PcChecker.Core.Settings;
using Wpf.Ui;

namespace PcChecker.App.ViewModels;

/// <summary>
/// Health page: runs the analysis, shows the score with severity counts, and lays the report out in sections
/// (issues, drives, battery, device problems, event-log errors, reliability history, crash dumps, security).
/// </summary>
public sealed partial class HealthViewModel : ViewModelBase
{
    private readonly IHealthService _health;
    private readonly IHistoryService _history;
    private readonly INavigationService _navigation;
    private readonly IPcAssistantService _assistant;
    private readonly INotificationService _notifications;
    private readonly ILogger<HealthViewModel> _logger;
    private readonly List<SystemErrorView> _allErrors = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReport))]
    private HealthReport? _report;

    [ObservableProperty]
    private int _score;

    [ObservableProperty]
    private string _scoreLabel = "Not analysed yet";

    [ObservableProperty]
    private HealthStatus _overallStatus = HealthStatus.Unknown;

    [ObservableProperty]
    private string _summaryText = "Run an analysis to see how your PC is doing.";

    [ObservableProperty]
    private string? _capturedText;

    [ObservableProperty]
    private int _criticalCount;

    [ObservableProperty]
    private int _warningCount;

    [ObservableProperty]
    private int _goodCount;

    [ObservableProperty]
    private string? _progressText;

    [ObservableProperty]
    private string? _errorFilter;

    [ObservableProperty]
    private string? _warningsText;

    [ObservableProperty]
    private string _crashDumpFolder = string.Empty;

    public HealthViewModel(IHealthService health, ISettingsService settings, INavigationService navigation,
        IPcAssistantService assistant, INotificationService notifications, IHistoryService history, ILogger<HealthViewModel> logger)
        : base(settings)
    {
        _health = health;
        _history = history;
        _navigation = navigation;
        _assistant = assistant;
        _notifications = notifications;
        _logger = logger;
        settings.Changed += OnAssistantSettingsChanged;
        try
        {
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            _crashDumpFolder = string.IsNullOrEmpty(windows) ? @"C:\Windows\Minidump" : Path.Combine(windows, "Minidump");
        }
        catch (Exception)
        {
            _crashDumpFolder = @"C:\Windows\Minidump";
        }
    }

    public ObservableCollection<HealthItemView> Issues { get; } = [];
    public ObservableCollection<HealthItemView> GoodItems { get; } = [];
    public ObservableCollection<HealthItemView> SecurityItems { get; } = [];
    public ObservableCollection<DiskHealthView> Disks { get; } = [];
    public ObservableCollection<BatteryHealthView> Batteries { get; } = [];
    public ObservableCollection<DeviceProblem> DeviceProblems { get; } = [];
    public ObservableCollection<SystemErrorView> SystemErrors { get; } = [];
    public ObservableCollection<SourceCountView> ErrorSources { get; } = [];
    public ObservableCollection<ReliabilityEventView> Reliability { get; } = [];
    public ObservableCollection<CrashDumpView> CrashDumps { get; } = [];

    public bool HasReport => Report is not null;
    public bool HasIssues => Issues.Count > 0;
    public bool HasGoodItems => GoodItems.Count > 0;
    public bool HasSecurityItems => SecurityItems.Count > 0;
    public bool HasDisks => Disks.Count > 0;
    public bool HasBatteries => Batteries.Count > 0;
    public bool HasDeviceProblems => DeviceProblems.Count > 0;
    public bool HasSystemErrors => _allErrors.Count > 0;
    public bool HasFilteredErrors => SystemErrors.Count > 0;
    public bool HasReliability => Reliability.Count > 0;
    public bool HasCrashDumps => CrashDumps.Count > 0;
    public bool HasWarnings => !string.IsNullOrWhiteSpace(WarningsText);
    public int SystemErrorCount => _allErrors.Count;
    public int ReliabilityFailureCount => Reliability.Count(r => r.IsFailure);
    public string IssuesHeader => HasIssues ? $"Issues found ({Issues.Count})" : "Issues found";
    public string DrivesHeader => HasDisks ? $"Drives ({Disks.Count})" : "Drives";
    public string DeviceProblemsHeader => HasDeviceProblems ? $"Device problems ({DeviceProblems.Count})" : "Device problems";
    public string SystemErrorsHeader => HasSystemErrors ? $"Recent system errors ({SystemErrorCount} in {WindowsHealthService.ErrorWindowDays} days)" : "Recent system errors";
    public string ReliabilityHeader => HasReliability ? $"Reliability history ({Reliability.Count} events, {ReliabilityFailureCount} failures)" : "Reliability history";
    public string CrashDumpsHeader => HasCrashDumps ? $"Crash dumps ({CrashDumps.Count})" : "Crash dumps";

    /// <summary>True when the Ask Claude feature has an API key; the Explain buttons still navigate otherwise.</summary>
    public bool IsAssistantConfigured => SafeIsConfigured();

    public string ExplainButtonText => IsAssistantConfigured ? "Explain with Claude" : "Set up Ask Claude";

    public string ExplainButtonToolTip => IsAssistantConfigured
        ? "Opens Ask Claude with this finding filled in so you can ask what it means and what to do."
        : "Ask Claude needs an Anthropic API key. This opens the Ask Claude page with the finding filled in, ready to send once the key is added.";

    /// <summary>
    /// The score gauge colours "value above threshold" (temperature semantics). A health score is the other way
    /// round, so the thresholds are set to 0 (always exceeded) or 101 (never exceeded) by the score band.
    /// </summary>
    public double GaugeWarnAt => HealthScoring.StatusFromScore(Score) is HealthStatus.Warning or HealthStatus.Critical ? 0 : 101;

    public double GaugeCritAt => HealthScoring.StatusFromScore(Score) == HealthStatus.Critical ? 0 : 101;

    public override async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_health.Last is { } last)
        {
            Apply(last);
            return;
        }

        await AnalyzeCoreAsync(cancellationToken);
    }

    public override Task RefreshAsync(CancellationToken cancellationToken) => AnalyzeCoreAsync(cancellationToken);

    [RelayCommand]
    private Task AnalyzeAsync() => AnalyzeCoreAsync(CancellationToken.None);

    /// <summary>Runs the analysis under the busy overlay; <paramref name="external"/> (navigation, screenshot runner) can cancel it too.</summary>
    private Task AnalyzeCoreAsync(CancellationToken external) => RunBusyAsync(async busyToken =>
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(busyToken, external);
        var progress = new Progress<ProgressUpdate>(p => ProgressText = p.Percent is { } pct ? $"{p.Stage} ({pct:0}%)" : p.Stage);
        var report = await _health.AnalyzeAsync(progress, linked.Token);
        Apply(report);
        if (report.Items.Count > 0)
        {
            // The History page charts the score of every check. Writing it touches the disk, so not on the UI thread.
            var captured = report.Captured;
            int score = report.Score;
            _ = Task.Run(() =>
            {
                try
                {
                    _history.RecordHealthScore(captured, score);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "The health score could not be added to the history.");
                }
            });
        }
        ProgressText = null;
    }, "Checking drives, devices, logs and security…");

    [RelayCommand]
    private void ExportToClipboard()
    {
        if (Report is null)
        {
            _notifications.Info("Nothing to copy", "Run an analysis first.");
            return;
        }

        try
        {
            Clipboard.SetText(HealthReportFormatter.ToText(Report));
            _notifications.Success("Copied", "The health report was copied as text. Paste it into an email or support ticket.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Clipboard export failed.");
            ErrorMessage = "The report could not be copied to the clipboard: " + ex.Message;
        }
    }

    [RelayCommand]
    private void ExplainWithClaude(HealthItemView? item)
    {
        if (item is null)
        {
            return;
        }

        var prompt = BuildExplainPrompt(item.Item);
        AssistantPrompt.PendingQuestion = prompt;
        if (!IsAssistantConfigured)
        {
            _notifications.Warning("Ask Claude is not set up yet", "Add an Anthropic API key on the Ask Claude page; your question is ready to send once it is.");
        }

        _navigation.Navigate(typeof(AssistantPage));
    }

    [RelayCommand]
    private void OpenDeviceManager() => Launch("devmgmt.msc");

    [RelayCommand]
    private void OpenEventViewer() => Launch("eventvwr.msc");

    [RelayCommand]
    private void OpenReliabilityMonitor() => Launch("perfmon.exe", "/rel");

    [RelayCommand]
    private void OpenWindowsUpdate() => Launch("ms-settings:windowsupdate");

    [RelayCommand]
    private void OpenWindowsSecurity() => Launch("windowsdefender:");

    [RelayCommand]
    private void OpenCrashDumpFolder()
    {
        if (!Directory.Exists(CrashDumpFolder))
        {
            _notifications.Info("No crash dump folder", $"{CrashDumpFolder} does not exist, which means Windows has never written a minidump on this PC.");
            return;
        }

        Launch(CrashDumpFolder);
    }

    [RelayCommand]
    private void OpenStoragePage() => _navigation.Navigate(typeof(StoragePage));

    [RelayCommand]
    private void OpenMemoryPage() => _navigation.Navigate(typeof(MemoryPage));

    [RelayCommand]
    private void OpenDriversPage() => _navigation.Navigate(typeof(DriversPage));

    [RelayCommand]
    private void ClearErrorFilter() => ErrorFilter = null;

    partial void OnErrorFilterChanged(string? value) => ApplyErrorFilter();

    partial void OnScoreChanged(int value)
    {
        OnPropertyChanged(nameof(GaugeWarnAt));
        OnPropertyChanged(nameof(GaugeCritAt));
    }

    /// <summary>Builds the question handed to the assistant for one finding.</summary>
    public static string BuildExplainPrompt(HealthItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var lines = new List<string>
        {
            "PC Checker found this on my PC. Explain in plain English what it means, how serious it is, and what I should do step by step.",
            string.Empty,
            $"Component: {item.Component}",
            $"Status: {item.Status}",
            $"Finding: {item.Title}",
            $"Details: {item.Detail}",
        };
        if (!string.IsNullOrWhiteSpace(item.Recommendation))
        {
            lines.Add($"Suggested fix: {item.Recommendation}");
        }

        if (item.Metrics.Count > 0)
        {
            lines.Add("Measurements: " + string.Join("; ", item.Metrics.Select(m => $"{m.Key} = {m.Value}")));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private void Apply(HealthReport report)
    {
        Report = report;
        Score = report.Score;
        OverallStatus = report.OverallStatus;
        ScoreLabel = HealthScoring.Describe(report.Score);
        CapturedText = "Checked " + HealthItemRules.DescribeAge(DateTimeOffset.Now - report.Captured) + $" ({report.Captured:HH:mm})";
        CriticalCount = report.Items.Count(i => i.Status == HealthStatus.Critical);
        WarningCount = report.Items.Count(i => i.Status == HealthStatus.Warning);
        GoodCount = report.Items.Count(i => i.Status == HealthStatus.Good);
        SummaryText = BuildSummary(report);
        WarningsText = report.Warnings.Count == 0 ? null : string.Join(Environment.NewLine, report.Warnings.Select(w => "• " + w));

        Replace(Issues, report.Items
            .Where(i => i.Status is HealthStatus.Critical or HealthStatus.Warning or HealthStatus.Unknown)
            .OrderByDescending(i => Rank(i.Status)).ThenBy(i => i.Component, StringComparer.OrdinalIgnoreCase)
            .Select(i => new HealthItemView(i)));
        Replace(GoodItems, report.Items.Where(i => i.Status == HealthStatus.Good && i.Component != "Security").Select(i => new HealthItemView(i)));
        Replace(SecurityItems, report.Items.Where(i => i.Component == "Security").OrderByDescending(i => Rank(i.Status)).Select(i => new HealthItemView(i)));
        Replace(Disks, report.Disks.Select(d => new DiskHealthView(d, Settings.Current.Monitoring)));
        Replace(Batteries, report.Batteries.Select(b => new BatteryHealthView(b)));
        Replace(DeviceProblems, report.DeviceProblems);
        Replace(Reliability, report.Reliability.Select(r => new ReliabilityEventView(r)));
        Replace(CrashDumps, report.CrashDumps.OrderByDescending(c => c.Time).Select(c => new CrashDumpView(c)));

        _allErrors.Clear();
        _allErrors.AddRange(report.SystemErrors.Select(e => new SystemErrorView(e)));
        Replace(ErrorSources, HealthItemRules.TopSources(report.SystemErrors, 8).Select(s => new SourceCountView(s.Source, s.Count)));
        ApplyErrorFilter();
        RaiseSectionFlags();
    }

    private void ApplyErrorFilter()
    {
        var filter = ErrorFilter?.Trim();
        IEnumerable<SystemErrorView> rows = _allErrors;
        if (!string.IsNullOrEmpty(filter))
        {
            rows = rows.Where(e => e.Source.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                   || e.Message.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                   || e.Log.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                   || e.EventId.ToString().Equals(filter, StringComparison.Ordinal));
        }

        Replace(SystemErrors, rows);
        OnPropertyChanged(nameof(HasFilteredErrors));
    }

    [RelayCommand]
    private void FilterBySource(SourceCountView? source) => ErrorFilter = source is null || ErrorFilter == source.Source ? null : source.Source;

    /// <summary>Re-evaluates the Explain buttons when the API key is added or removed on the Ask Claude page.</summary>
    private void OnAssistantSettingsChanged(object? sender, AppSettings settings) =>
        DispatcherHelper.PostToUi(RaiseAssistantFlags);

    private void RaiseAssistantFlags()
    {
        OnPropertyChanged(nameof(IsAssistantConfigured));
        OnPropertyChanged(nameof(ExplainButtonText));
        OnPropertyChanged(nameof(ExplainButtonToolTip));
    }

    private void RaiseSectionFlags()
    {
        foreach (var name in new[]
                 {
                     nameof(HasIssues), nameof(HasGoodItems), nameof(HasSecurityItems), nameof(HasDisks), nameof(HasBatteries),
                     nameof(HasDeviceProblems), nameof(HasSystemErrors), nameof(HasFilteredErrors), nameof(HasReliability),
                     nameof(HasCrashDumps), nameof(HasWarnings), nameof(SystemErrorCount), nameof(ReliabilityFailureCount),
                     nameof(IssuesHeader), nameof(DrivesHeader), nameof(DeviceProblemsHeader), nameof(SystemErrorsHeader),
                     nameof(ReliabilityHeader), nameof(CrashDumpsHeader),
                 })
        {
            OnPropertyChanged(name);
        }

        RaiseAssistantFlags();
    }

    private static string BuildSummary(HealthReport report)
    {
        int critical = report.Items.Count(i => i.Status == HealthStatus.Critical);
        int warning = report.Items.Count(i => i.Status == HealthStatus.Warning);
        if (critical > 0)
        {
            return critical == 1
                ? "One problem needs attention now. Start with the red item below."
                : $"{critical} problems need attention now. Start with the red items below.";
        }

        if (warning > 0)
        {
            return warning == 1
                ? "Nothing urgent, but one thing is worth a look."
                : $"Nothing urgent, but {warning} things are worth a look.";
        }

        return report.Items.Count == 0
            ? "No checks could run. See the notes below."
            : "Everything we checked looks fine. Keep backups current and check again now and then.";
    }

    private void Launch(string target, string? arguments = null)
    {
        var result = ShellLauncher.Open(target, arguments);
        if (!result.Success)
        {
            _notifications.Warning("Could not open", result.Message);
        }
    }

    private bool SafeIsConfigured()
    {
        try
        {
            return _assistant.IsConfigured;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Assistant configuration could not be read.");
            return false;
        }
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }

    private static int Rank(HealthStatus s) => s switch
    {
        HealthStatus.Critical => 3,
        HealthStatus.Warning => 2,
        HealthStatus.Unknown => 1,
        _ => 0,
    };

    protected override void OnBusyError(Exception exception) => _logger.LogError(exception, "Health analysis failed.");
}
