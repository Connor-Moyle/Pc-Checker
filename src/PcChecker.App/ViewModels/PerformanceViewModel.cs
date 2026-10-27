using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Services;
using PcChecker.App.Views.Pages;
using PcChecker.Core.Common;
using PcChecker.Core.Optimization;
using PcChecker.Core.Performance;
using PcChecker.Core.Settings;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace PcChecker.App.ViewModels;

/// <summary>View model for the Performance page: slowdown scan, findings with one-click fixes, startup programs, services and busy processes.</summary>
public sealed partial class PerformanceViewModel : ViewModelBase
{
    private readonly IPerformanceAnalyzer _analyzer;
    private readonly IOptimizationService _optimization;
    private readonly OptimizationActionRunner _runner;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly INavigationService _navigation;
    private readonly ILogger<PerformanceViewModel> _logger;
    private readonly List<PerformanceServiceRow> _allServices = [];
    private readonly HashSet<string> _ignored = new(StringComparer.OrdinalIgnoreCase);

    [ObservableProperty] private bool _hasReport;
    [ObservableProperty] private int _score;
    [ObservableProperty] private string _scoreHeadline = "Not scanned yet";
    [ObservableProperty] private string _scoreDetail = "Run a scan to see what is slowing this PC down.";
    [ObservableProperty] private string _findingsSummary = string.Empty;
    [ObservableProperty] private string? _capturedText;
    [ObservableProperty] private bool _isRebootRequired;
    [ObservableProperty] private bool _isActionRunning;
    [ObservableProperty] private string _uptimeText = "—";
    [ObservableProperty] private string _bootTimeText = "—";
    [ObservableProperty] private string _memoryText = "—";
    [ObservableProperty] private double _memoryPercent;
    [ObservableProperty] private string _powerPlanText = "—";
    [ObservableProperty] private string _startupText = "—";
    [ObservableProperty] private string _processText = "—";
    [ObservableProperty] private string _serviceSearch = string.Empty;
    [ObservableProperty] private bool _showMicrosoftServices;
    [ObservableProperty] private bool _isProcessesExpanded = true;
    [ObservableProperty] private bool _isServicesExpanded;
    [ObservableProperty] private bool _isStartupExpanded = true;
    [ObservableProperty] private string? _warningsText;
    [ObservableProperty] private bool _hasWarnings;
    [ObservableProperty] private string _progressText = string.Empty;

    public PerformanceViewModel(ISettingsService settings, IPerformanceAnalyzer analyzer, IOptimizationService optimization,
        OptimizationActionRunner runner, IDialogService dialogs, INotificationService notifications, INavigationService navigation,
        ILogger<PerformanceViewModel> logger)
        : base(settings)
    {
        _analyzer = analyzer;
        _optimization = optimization;
        _runner = runner;
        _dialogs = dialogs;
        _notifications = notifications;
        _navigation = navigation;
        _logger = logger;
        _isRebootRequired = runner.IsRebootRequired;
        _isActionRunning = runner.IsRunning;
        runner.RebootRequiredChanged += (_, _) => DispatcherHelper.PostToUi(() => IsRebootRequired = _runner.IsRebootRequired);
        runner.RunningChanged += (_, _) => DispatcherHelper.PostToUi(() => IsActionRunning = _runner.IsRunning);

        QuickWins =
        [
            new("Latency", "Find and fix input lag, DPC spikes and timer settings.", SymbolRegular.Timer24, OpenLatencyCommand),
            new("Startup", "Choose exactly what starts with Windows and manage boot options.", SymbolRegular.Power24, OpenStartupCommand),
            new("Memory", "Test RAM, check XMP/EXPO and tune the page file.", SymbolRegular.Memory16, OpenMemoryCommand),
            new("Cleanup", "Free disk space by removing temporary and junk files.", SymbolRegular.Broom24, OpenCleanupCommand),
        ];
    }

    public ObservableCollection<FindingItem> Findings { get; } = [];
    public ObservableCollection<PerformanceStartupRow> StartupItems { get; } = [];
    public ObservableCollection<PerformanceServiceRow> Services { get; } = [];
    public ObservableCollection<ProcessRow> TopProcesses { get; } = [];
    public ObservableCollection<DriveRow> Drives { get; } = [];
    public IReadOnlyList<QuickWin> QuickWins { get; }
    public IReadOnlyList<string> StartModes => ServiceClassifier.StartModes;
    public bool HasFindings => Findings.Count > 0;
    public bool HasNoFindings => HasReport && Findings.Count == 0;

    public override async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_analyzer.Last is { } last)
        {
            Load(last);
            return;
        }

        await ScanAsync();
    }

    public override Task RefreshAsync(CancellationToken cancellationToken) => ScanAsync();

    [RelayCommand]
    private Task ScanAsync() => RunBusyAsync(async ct =>
    {
        var progress = new Progress<ProgressUpdate>(p =>
        {
            BusyText = p.Percent is { } pct ? $"{p.Stage} ({pct:0}%)" : p.Stage;
            ProgressText = p.Detail ?? string.Empty;
        });
        var report = await _analyzer.AnalyzeAsync(progress, ct);
        Load(report);
    }, "Scanning for slowdowns…");

    private void Load(PerformanceReport report)
    {
        var known = _optimization.Actions;
        Findings.Clear();
        foreach (var f in report.Findings.Where(f => !_ignored.Contains(f.Id)))
        {
            Findings.Add(new FindingItem(f, known));
        }

        StartupItems.Clear();
        foreach (var item in report.StartupItems.OrderByDescending(s => s.Enabled).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            StartupItems.Add(new PerformanceStartupRow(item, ToggleStartupAsync));
        }

        _allServices.Clear();
        _allServices.AddRange(report.Services.Select(s => new PerformanceServiceRow(s)));
        ApplyServiceFilter();

        TopProcesses.Clear();
        foreach (var p in report.TopProcesses)
        {
            TopProcesses.Add(new ProcessRow(p));
        }

        Drives.Clear();
        foreach (var (drive, free, total, media) in report.Drives)
        {
            Drives.Add(new DriveRow(drive, free, total, media));
        }

        int high = report.Findings.Count(f => f.Severity >= Severity.High);
        Score = report.Score;
        ScoreHeadline = report.Score switch
        {
            >= 90 => "Running great",
            >= 75 => "Running well",
            >= 50 => "Could be faster",
            _ => "Needs attention",
        };
        ScoreDetail = report.Findings.Count == 0
            ? "No slowdowns were found."
            : $"{report.Findings.Count} finding{(report.Findings.Count == 1 ? string.Empty : "s")}" + (high > 0 ? $", {high} high priority" : string.Empty) + ".";
        FindingsSummary = ScoreDetail;
        CapturedText = $"Scanned {report.Captured:g}";
        BootTimeText = report.LastBootSeconds is { } boot ? $"{boot:0.#} s" : "Unknown";
        MemoryPercent = report.MemoryUsedPercent;
        MemoryText = report.MemoryTotalBytes > 0 ? $"{report.MemoryUsedPercent:0}% of {ByteSize.Format(report.MemoryTotalBytes)}" : "Unknown";
        PowerPlanText = report.ActivePowerPlan ?? "Unknown";
        UptimeText = report.Uptime is { } uptime ? PerformanceTextParsers.FormatUptime(uptime) : "Unknown";
        int enabledStartup = report.StartupItems.Count(s => s.Enabled);
        StartupText = $"{enabledStartup} enabled";
        ProcessText = report.TopProcesses.Count > 0 ? $"{report.TopProcesses[0].Name} ({report.TopProcesses[0].CpuPercent:0.#}% CPU)" : "—";
        WarningsText = report.Warnings.Count == 0 ? null : string.Join(Environment.NewLine, report.Warnings);
        HasWarnings = report.Warnings.Count > 0;
        HasReport = true;
        OnPropertyChanged(nameof(HasFindings));
        OnPropertyChanged(nameof(HasNoFindings));
    }

    partial void OnServiceSearchChanged(string value) => ApplyServiceFilter();

    partial void OnShowMicrosoftServicesChanged(bool value) => ApplyServiceFilter();

    private void ApplyServiceFilter()
    {
        Services.Clear();
        var query = ServiceSearch?.Trim() ?? string.Empty;
        foreach (var row in _allServices)
        {
            if (!ShowMicrosoftServices && row.IsMicrosoft && query.Length == 0)
            {
                continue;
            }

            if (query.Length > 0
                && !row.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                && !row.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Services.Add(row);
        }
    }

    [RelayCommand]
    private async Task FixAsync(FindingItem? finding)
    {
        if (finding is null)
        {
            return;
        }

        switch (finding.ActionKind)
        {
            case FindingActionKind.Optimization when finding.ActionId is not null:
                if (await _runner.RunAsync(finding.ActionId))
                {
                    finding.IsIgnored = true;
                    RemoveFinding(finding);
                }

                break;
            case FindingActionKind.StartupPage:
                IsStartupExpanded = true;
                _navigation.Navigate(typeof(StartupPage));
                break;
            case FindingActionKind.ProcessesSection:
                IsProcessesExpanded = true;
                _notifications.Info("Busy processes", "The busiest processes are listed further down this page.");
                break;
            case FindingActionKind.ServicesSection:
                IsServicesExpanded = true;
                if (!IsAdvancedMode)
                {
                    _notifications.Info("Services", "Turn on Advanced mode (bottom of the sidebar) to manage services.");
                }

                break;
        }
    }

    [RelayCommand]
    private void Ignore(FindingItem? finding)
    {
        if (finding is null)
        {
            return;
        }

        _ignored.Add(finding.Id);
        RemoveFinding(finding);
    }

    private void RemoveFinding(FindingItem finding)
    {
        Findings.Remove(finding);
        OnPropertyChanged(nameof(HasFindings));
        OnPropertyChanged(nameof(HasNoFindings));
    }

    private async Task ToggleStartupAsync(PerformanceStartupRow row, bool enabled)
    {
        if (row.IsBusy)
        {
            return;
        }

        bool confirmed = await _dialogs.ConfirmAsync(
            enabled ? $"Start {row.Name} with Windows?" : $"Stop {row.Name} starting with Windows?",
            enabled
                ? $"{row.Name} will launch automatically every time you sign in.\n\nWhere: {row.Location}"
                : $"{row.Name} will no longer launch at sign-in. You can still open it yourself, and you can turn this back on at any time.\n\nWhere: {row.Location}",
            enabled ? "Enable" : "Disable");
        if (!confirmed)
        {
            row.SetEnabledSilently(!enabled);
            return;
        }

        row.IsBusy = true;
        try
        {
            var item = row.Item;
            var result = await UndoOffers.RunAsync(
                () => _optimization.SetStartupItemEnabledAsync(item, enabled, CancellationToken.None),
                async undoCt =>
                {
                    var back = await _optimization.SetStartupItemEnabledAsync(item with { Enabled = enabled }, !enabled, undoCt);
                    if (back.Success)
                    {
                        DispatcherHelper.PostToUi(() =>
                        {
                            row.SetEnabledSilently(!enabled);
                            StartupText = $"{StartupItems.Count(s => s.IsEnabled)} enabled";
                        });
                    }

                    return back;
                },
                row.Name);
            if (result.Success)
            {
                _notifications.Success("Startup updated", result.Message);
                StartupText = $"{StartupItems.Count(s => s.IsEnabled)} enabled";
            }
            else
            {
                _notifications.Error("Startup not changed", result.Message);
                row.SetEnabledSilently(!enabled);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Startup toggle failed for {Item}.", row.Name);
            _notifications.Error("Startup not changed", ex.Message);
            row.SetEnabledSilently(!enabled);
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ApplyServiceModeAsync(PerformanceServiceRow? row)
    {
        if (row is null || !row.IsDirty || row.IsBusy)
        {
            return;
        }

        bool disabling = row.StartMode.Equals("Disabled", StringComparison.OrdinalIgnoreCase);
        bool coreToNonAutomatic = row.IsProtected && !ServiceClassifier.IsAutomatic(row.StartMode);
        if (coreToNonAutomatic)
        {
            row.Reset();
            _notifications.Warning("Core Windows service", $"{row.DisplayName} is part of Windows and must keep starting automatically. Choose Automatic or Automatic (Delayed).");
            return;
        }

        string warning = disabling
            ? "Disabled services cannot start at all, even when a program needs them. Prefer Manual unless you are sure.\n\n"
            : row.IsMicrosoft && !ServiceClassifier.IsAutomatic(row.StartMode)
                ? "This is a Windows service. Features that rely on it may stop working until it is set back.\n\n"
                : string.Empty;
        if (!await _dialogs.ConfirmAsync($"Change {row.DisplayName}?",
                $"Start mode: {row.OriginalStartMode} → {row.StartMode}.\n\n" + warning + _runner.RestorePointNote, "Change", isDanger: disabling || (row.IsMicrosoft && warning.Length > 0)))
        {
            row.Reset();
            return;
        }

        row.IsBusy = true;
        try
        {
            string oldMode = row.OriginalStartMode;
            string newMode = row.StartMode;
            var result = await UndoOffers.RunAsync(
                () => _optimization.SetServiceStartModeAsync(row.Name, newMode, CancellationToken.None),
                async undoCt =>
                {
                    // Undo from the success notification: the old start mode back, and the row showing it.
                    var back = await _optimization.SetServiceStartModeAsync(row.Name, oldMode, undoCt);
                    if (back.Success)
                    {
                        DispatcherHelper.PostToUi(() =>
                        {
                            row.StartMode = oldMode;
                            row.Commit();
                        });
                    }

                    return back;
                },
                row.DisplayName);
            if (result.Success)
            {
                row.Commit();
                _notifications.Success("Service updated", result.Message);
            }
            else
            {
                row.Reset();
                _notifications.Error("Service not changed", result.Message);
            }
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task EndTaskAsync(ProcessRow? row)
    {
        if (row is null || row.IsBusy)
        {
            return;
        }

        if (!row.CanEnd)
        {
            _notifications.Warning("Protected process", $"{row.Name} is part of Windows and cannot be ended safely.");
            return;
        }

        if (!await _dialogs.ConfirmAsync($"End {row.Name}?", $"Unsaved work in {row.Name} will be lost. The program can be started again normally.", "End task", isDanger: true))
        {
            return;
        }

        row.IsBusy = true;
        try
        {
            var result = await Task.Run(() => ProcessRules.TryKill(row.Pid, row.Name));
            if (result.Success)
            {
                TopProcesses.Remove(row);
                _notifications.Success("Task ended", result.Message);
            }
            else
            {
                _notifications.Error("Could not end task", result.Message);
            }
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        var report = _analyzer.Last;
        if (report is null)
        {
            _notifications.Info("Nothing to export", "Run a scan first.");
            return;
        }

        var path = await _dialogs.SaveFileAsync("Export performance report", $"performance-report-{DateTime.Now:yyyyMMdd-HHmm}.md",
            "Markdown report|*.md|JSON data|*.json|Text file|*.txt");
        if (path is null)
        {
            return;
        }

        try
        {
            var content = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? PerformanceReportFormatter.BuildJson(report)
                : PerformanceReportFormatter.BuildMarkdown(report);
            await File.WriteAllTextAsync(path, content);
            _notifications.Success("Report exported", path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Export failed.");
            _notifications.Error("Export failed", ex.Message);
        }
    }

    [RelayCommand] private void OpenLatency() => _navigation.Navigate(typeof(LatencyPage));
    [RelayCommand] private void OpenStartup() => _navigation.Navigate(typeof(StartupPage));
    [RelayCommand] private void OpenMemory() => _navigation.Navigate(typeof(MemoryPage));
    [RelayCommand] private void OpenCleanup() => _navigation.Navigate(typeof(CleanupPage));
    [RelayCommand] private void OpenOptimize() => _navigation.Navigate(typeof(OptimizePage));

    protected override void OnBusyError(Exception exception) => _logger.LogError(exception, "Performance page operation failed.");
}
