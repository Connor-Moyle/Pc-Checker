using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Converters;
using PcChecker.App.Services;
using PcChecker.Core.Common;
using PcChecker.Core.Latency;
using PcChecker.Core.Optimization;
using PcChecker.Core.Settings;

namespace PcChecker.App.ViewModels;

/// <summary>View model for the Latency page: runs the check, shows the score, measurements, network table, findings with fixes, and recent runs.</summary>
public sealed partial class LatencyViewModel : ViewModelBase
{
    private const int HistorySize = 5;

    /// <summary>Sample length of the automatic first check: short, because the user did not ask for it yet.</summary>
    private const int QuickSampleSeconds = 5;
    public const string LatencyMonUrl = "https://www.resplendence.com/latencymon";
    public const string WpaUrl = "https://learn.microsoft.com/windows-hardware/test/wpt/";

    private readonly ILatencyService _latency;
    private readonly IOptimizationService _optimization;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly ILogger<LatencyViewModel> _logger;
    private readonly List<(LatencyFinding Finding, OptimizationAction? Action)> _allFindings = [];

    [ObservableProperty]
    private int _sampleSeconds = 10;

    [ObservableProperty]
    private bool _includeNetwork = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReport))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    private LatencyReport? _report;

    [ObservableProperty]
    private int _score;

    [ObservableProperty]
    private string _grade = "—";

    [ObservableProperty]
    private Brush _scoreBrush = HealthStatusToBrushConverter.Unknown;

    [ObservableProperty]
    private string? _capturedText;

    [ObservableProperty]
    private string? _summaryText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarnings))]
    private string? _warningsText;

    [ObservableProperty] private string _dpcText = "—";
    [ObservableProperty] private string _isrText = "—";
    [ObservableProperty] private string _dpcsText = "—";
    [ObservableProperty] private string _interruptsText = "—";
    [ObservableProperty] private string _timerText = "—";
    [ObservableProperty] private string? _timerSubtitle;
    [ObservableProperty] private string _pollingText = "—";
    [ObservableProperty] private string? _pollingSubtitle;
    [ObservableProperty] private string _dnsText = "—";
    [ObservableProperty] private string? _dnsSubtitle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHiddenAdvanced))]
    private int _hiddenAdvancedCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoFindings))]
    private int _visibleFindingCount;

    public LatencyViewModel(ISettingsService settings, ILatencyService latency, IOptimizationService optimization,
        IDialogService dialogs, INotificationService notifications, ILogger<LatencyViewModel> logger)
        : base(settings)
    {
        _latency = latency;
        _optimization = optimization;
        _dialogs = dialogs;
        _notifications = notifications;
        _logger = logger;
        PropertyChanged += OnOwnPropertyChanged;
    }

    public IReadOnlyList<int> SampleOptions { get; } = [5, 10, 30];
    public ObservableCollection<NetworkResultItem> NetworkItems { get; } = [];
    public ObservableCollection<LatencyFindingItem> Findings { get; } = [];
    public ObservableCollection<SettingRow> SettingRows { get; } = [];
    public ObservableCollection<LatencyRunSummary> History { get; } = [];

    public bool HasReport => Report is not null;
    public bool ShowEmptyState => Report is null && !IsBusy;
    public bool HasWarnings => !string.IsNullOrWhiteSpace(WarningsText);
    public bool HasNetwork => NetworkItems.Count > 0;
    public bool HasHistory => History.Count > 0;
    public bool HasHiddenAdvanced => HiddenAdvancedCount > 0;
    public bool ShowNoFindings => HasReport && VisibleFindingCount == 0;

    public override Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_latency.Last is { } last)
        {
            Apply(last);
            return Task.CompletedTask;
        }

        // First visit: run a short check so the page is populated; the user can pick a longer sample afterwards.
        return RunCheckCoreAsync(QuickSampleSeconds, "Starting a quick latency check…");
    }

    public override Task RefreshAsync(CancellationToken cancellationToken) => RunCheckAsync();

    [RelayCommand]
    private Task RunCheckAsync() => RunCheckCoreAsync(SampleSeconds, "Starting latency check…");

    private Task RunCheckCoreAsync(int sampleSeconds, string busyText) => RunBusyAsync(async ct =>
    {
        var progress = new Progress<ProgressUpdate>(p =>
            BusyText = p.Percent is { } pct ? $"{p.Stage} ({pct:0}%){(p.Detail is null ? string.Empty : " — " + p.Detail)}" : p.Stage);
        var report = await _latency.AnalyzeAsync(sampleSeconds, IncludeNetwork, progress, ct);
        Apply(report);
        PushHistory(report);
    }, busyText);

    [RelayCommand]
    private Task PingAgainAsync() => RunBusyAsync(async ct =>
    {
        var targets = Settings.Current.Latency.PingTargets
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => (t.Trim(), NetworkLatencyProbe.LabelFor(t.Trim())))
            .ToList();
        if (targets.Count == 0)
        {
            targets = [("gateway", "Your router"), ("1.1.1.1", "Cloudflare DNS"), ("8.8.8.8", "Google DNS")];
        }

        var results = await _latency.PingTestAsync(targets, Settings.Current.Latency.PingCount, ct);
        NetworkItems.Clear();
        foreach (var r in results)
        {
            NetworkItems.Add(new NetworkResultItem(r));
        }

        OnPropertyChanged(nameof(HasNetwork));
    }, "Pinging…");

    [RelayCommand]
    private async Task FixAsync(LatencyFindingItem? item)
    {
        if (item?.Action is not { } action || !item.CanFix)
        {
            return;
        }

        if (item.IsInfoOnly)
        {
            var info = await _optimization.ApplyAsync(action.Id, CancellationToken.None);
            await _dialogs.ShowMessageAsync(action.Title, info.Message);
            return;
        }

        var message = action.Description
            + (action.RequiresRestart ? "\n\nA restart is needed before this takes effect." : string.Empty)
            + (action.Reversible ? "\n\nYou can undo it later from the Optimize page." : string.Empty)
            + (action.Risk == RiskLevel.Advanced ? "\n\nThis is an advanced change. Only apply it if you know why you want it." : string.Empty)
            + "\n\nA restore point is created first when that is enabled in Settings.";
        bool confirmed = await _dialogs.ConfirmAsync(action.Title, message, "Apply fix", isDanger: OptimizationRisk.IsDanger(action));
        if (!confirmed)
        {
            return;
        }

        item.IsFixing = true;
        try
        {
            await RunBusyAsync(async ct =>
            {
                var result = await _optimization.ApplyAsync(action.Id, ct);
                item.ResultMessage = result.Message;
                if (result.Success)
                {
                    item.IsFixed = true;
                    _notifications.Success("Fix applied", result.Message);
                }
                else
                {
                    _notifications.Error("Fix failed", result.Message);
                }
            }, $"Applying: {action.Title}…");
        }
        finally
        {
            item.IsFixing = false;
        }
    }

    [RelayCommand]
    private void OpenLink(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        var result = ShellLauncher.Open(url);
        if (!result.Success)
        {
            _notifications.Warning("Could not open link", result.Message);
        }
    }

    protected override void OnBusyError(Exception exception) => _logger.LogError(exception, "Latency page operation failed.");

    private void Apply(LatencyReport report)
    {
        Report = report;
        Score = report.Score;
        Grade = LatencyRules.Grade(report.Score);
        ScoreBrush = LatencyRules.Status(report.Score) switch
        {
            HealthStatus.Good => HealthStatusToBrushConverter.Good,
            HealthStatus.Warning => HealthStatusToBrushConverter.Warning,
            _ => HealthStatusToBrushConverter.Critical,
        };
        CapturedText = $"Checked {report.Captured:g}";
        SummaryText = report.Findings.Count == 0
            ? "No latency problems found. Your PC responds as quickly as its hardware allows."
            : $"{report.Findings.Count} thing{(report.Findings.Count == 1 ? string.Empty : "s")} worth looking at. Fixes are listed below in order of impact.";
        WarningsText = report.Warnings.Count == 0 ? null : string.Join("\n", report.Warnings);

        DpcText = Percent(report.DpcTimePercent);
        IsrText = Percent(report.InterruptTimePercent);
        DpcsText = report.DpcsQueuedPerSec is { } d ? d.ToString("N0") : "—";
        InterruptsText = report.InterruptsPerSec is { } i ? i.ToString("N0") : "—";
        TimerText = report.TimerResolutionMs is { } t ? $"{t:0.00}" : "—";
        TimerSubtitle = report.TimerResolutionMinMs is { } tm ? $"Finest possible {tm:0.00} ms" : "Current system timer interval";
        PollingText = report.MousePollingHz is { } hz and > 0 ? hz.ToString("N0") : "—";
        PollingSubtitle = report.MousePollingHz is > 0 ? "Reports per second measured" : "Move the mouse during the check";
        DnsText = report.DnsMs is { } dns ? $"{dns:0}" : "—";
        DnsSubtitle = report.DnsMs is null ? (IncludeNetwork ? "Lookup failed or skipped" : "Network check was off") : "Time to look up www.microsoft.com";

        NetworkItems.Clear();
        foreach (var n in report.Network)
        {
            NetworkItems.Add(new NetworkResultItem(n));
        }

        OnPropertyChanged(nameof(HasNetwork));

        var actions = _optimization.Actions;
        _allFindings.Clear();
        foreach (var f in report.Findings.OrderByDescending(f => f.Severity))
        {
            var action = f.ActionId is null ? null : actions.FirstOrDefault(a => a.Id.Equals(f.ActionId, StringComparison.OrdinalIgnoreCase));
            _allFindings.Add((f, action));
        }

        FilterFindings();
        BuildSettingRows(report);
    }

    private void FilterFindings()
    {
        Findings.Clear();
        int hidden = 0;
        foreach (var (finding, action) in _allFindings)
        {
            if (finding.AdvancedOnly && !IsAdvancedMode)
            {
                hidden++;
                continue;
            }

            // A finding may stay visible while its fix is an advanced-only catalog action: show the advice without the button.
            var offered = action is { AdvancedOnly: true } && !IsAdvancedMode ? null : action;
            Findings.Add(new LatencyFindingItem(finding, offered));
        }

        HiddenAdvancedCount = hidden;
        VisibleFindingCount = Findings.Count;
    }

    private void BuildSettingRows(LatencyReport r)
    {
        SettingRows.Clear();
        SettingRows.Add(new("Power plan", r.PowerPlan ?? "Unknown"));
        SettingRows.Add(new("Hardware-accelerated GPU scheduling", OnOff(r.HagsEnabled, "Not supported or unknown")));
        SettingRows.Add(new("Game Mode", OnOff(r.GameMode)));
        SettingRows.Add(new("Enhance pointer precision (mouse acceleration)", OnOff(r.PointerPrecision)));
        SettingRows.Add(new("CPU core parking", r.CoreParkingAllowed switch { true => "Allowed", false => "Off (all cores stay awake)", _ => "Unknown" }));
        SettingRows.Add(new("USB selective suspend", OnOff(r.UsbSelectiveSuspend)));
        SettingRows.Add(new("PCI Express link power saving", OnOff(r.PcieAspmEnabled)));
        SettingRows.Add(new("HPET forced (useplatformclock)", r.HpetForced switch { true => "Yes", false => "No (default)", _ => "Unknown" }));
        SettingRows.Add(new("Dynamic tick", r.DynamicTickDisabled switch { true => "Off", false => "On (default)", _ => "Unknown" }));
    }

    private void PushHistory(LatencyReport report)
    {
        History.Insert(0, LatencyRunSummary.From(report));
        while (History.Count > HistorySize)
        {
            History.RemoveAt(History.Count - 1);
        }

        OnPropertyChanged(nameof(HasHistory));
    }

    private void OnOwnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IsAdvancedMode))
        {
            FilterFindings();
        }
        else if (e.PropertyName == nameof(IsBusy))
        {
            OnPropertyChanged(nameof(ShowEmptyState));
        }
    }

    private static string Percent(double? value) => value is { } v ? $"{v:0.0}" : "—";

    private static string OnOff(bool? value, string unknown = "Unknown") => value switch { true => "On", false => "Off", _ => unknown };
}
