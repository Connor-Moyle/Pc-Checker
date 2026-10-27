using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Localization;
using PcChecker.App.Services;
using PcChecker.Core.Battery;
using PcChecker.Core.Common;
using PcChecker.Core.History;
using PcChecker.Core.Settings;

namespace PcChecker.App.ViewModels;

/// <summary>
/// Battery page: health, cycles and capacity against the design, how long a charge lasts now and when new, the
/// capacity history chart, recent use and tips. Desktops get one clear empty state. Everything here only reads;
/// "Open the full report" has Windows write its HTML battery report to Documents and opens it.
/// </summary>
public sealed partial class BatteryViewModel : ViewModelBase
{
    public const string BatterySettingsUri = "ms-settings:batterysaver";
    public const string PowerSettingsUri = "ms-settings:powersleep";
    private const int MaxHistoryRows = 14;

    private readonly IBatteryService _battery;
    private readonly INotificationService _notifications;
    private readonly ILogger<BatteryViewModel> _logger;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    [NotifyPropertyChangedFor(nameof(ShowContent))]
    [NotifyCanExecuteChangedFor(nameof(OpenFullReportCommand))]
    private BatteryPageState _state = BatteryPageState.NotLoaded;

    [ObservableProperty]
    private string _subtitle = Loc.T("How worn your battery is and how long a charge lasts.");

    [ObservableProperty]
    private string _emptyTitle = Loc.T("This PC has no battery");

    [ObservableProperty]
    private string _emptyMessage = string.Empty;

    [ObservableProperty]
    private string _healthText = "—";

    [ObservableProperty]
    private string _healthSubtitle = string.Empty;

    [ObservableProperty]
    private HealthStatus _healthStatus = HealthStatus.Unknown;

    [ObservableProperty]
    private string _cyclesText = "—";

    [ObservableProperty]
    private string _cyclesSubtitle = string.Empty;

    [ObservableProperty]
    private string _capacityText = "—";

    [ObservableProperty]
    private string _capacitySubtitle = string.Empty;

    [ObservableProperty]
    private string _lifeText = "—";

    [ObservableProperty]
    private string _lifeSubtitle = string.Empty;

    [ObservableProperty]
    private string _chargeText = "—";

    [ObservableProperty]
    private double _chargePercent;

    [ObservableProperty]
    private string _liveText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoChart))]
    private bool _hasChart;

    [ObservableProperty]
    private IReadOnlyList<HistoryPoint> _chartPoints = [];

    [ObservableProperty]
    private DateTimeOffset _chartFrom = DateTimeOffset.Now.AddDays(-7);

    [ObservableProperty]
    private DateTimeOffset _chartTo = DateTimeOffset.Now;

    [ObservableProperty]
    private double _chartMinimum;

    [ObservableProperty]
    private double _chartMaximum = 100;

    [ObservableProperty]
    private double _chartDesign = double.NaN;

    [ObservableProperty]
    private TimeSpan _chartGap = TimeSpan.FromDays(14);

    [ObservableProperty]
    private string _chartCaption = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarnings))]
    private string? _warnings;

    public BatteryViewModel(ISettingsService settings, IBatteryService battery, INotificationService notifications,
        ILogger<BatteryViewModel> logger)
        : base(settings)
    {
        _battery = battery;
        _notifications = notifications;
        _logger = logger;
        RecentRows.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasRecentRows));
        HistoryRows.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasHistoryRows));
    }

    public ObservableCollection<BatteryUsageRow> RecentRows { get; } = [];

    public ObservableCollection<BatteryHistoryRow> HistoryRows { get; } = [];

    public ObservableCollection<BatteryTipRow> Tips { get; } = [];

    public ObservableCollection<KeyValuePair<string, string>> Details { get; } = [];

    public bool ShowEmptyState => State is BatteryPageState.NoBattery or BatteryPageState.UpsOnly;

    public bool ShowContent => State == BatteryPageState.Ready;

    public bool HasNoChart => !HasChart;

    public bool HasRecentRows => RecentRows.Count > 0;

    public bool HasHistoryRows => HistoryRows.Count > 0;

    public bool HasWarnings => !string.IsNullOrWhiteSpace(Warnings);

    /// <summary>Reading the battery changes nothing, so the page fills itself in every mode.</summary>
    public override Task InitializeAsync(CancellationToken cancellationToken) => LoadAsync(cancellationToken);

    public override Task RefreshAsync(CancellationToken cancellationToken) => LoadAsync(cancellationToken);

    protected override void OnBusyError(Exception exception) => _logger.LogError(exception, "Battery page operation failed.");

    [RelayCommand]
    private Task RefreshNowAsync() => LoadAsync(CancellationToken.None);

    private Task LoadAsync(CancellationToken outer) => RunBusyAsync(async busyToken =>
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(busyToken, outer);
        var progress = new Progress<ProgressUpdate>(p => BusyText = p.Stage + "…");
        var overview = await _battery.LoadAsync(progress, linked.Token);
        Apply(overview);
    }, Loc.T("Reading the battery…"));

    [RelayCommand(CanExecute = nameof(CanOpenFullReport))]
    private Task OpenFullReportAsync() => RunBusyAsync(async ct =>
    {
        var result = await _battery.SaveFullReportAsync(ct);
        if (!result.Success || result.Value is not { } path)
        {
            ErrorMessage = result.Message;
            return;
        }

        _notifications.Success(Loc.T("Battery report saved"), path);
        var opened = ShellLauncher.Open(path);
        if (!opened.Success)
        {
            ErrorMessage = opened.Message;
        }
    }, Loc.T("Creating the battery report…"));

    private bool CanOpenFullReport() => State == BatteryPageState.Ready;

    [RelayCommand]
    private void OpenBatterySettings() => Open(BatterySettingsUri);

    [RelayCommand]
    private void OpenPowerSettings() => Open(PowerSettingsUri);

    [RelayCommand]
    private void OpenReportsFolder()
    {
        try
        {
            Directory.CreateDirectory(_battery.ReportsFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorMessage = Loc.F("The reports folder could not be created: {0}", ex.Message);
            return;
        }

        Open(_battery.ReportsFolder);
    }

    private void Apply(BatteryOverview overview)
    {
        State = BatteryPageRules.Classify(overview);
        Warnings = overview.Warnings.Count == 0 ? null : string.Join("\n", overview.Warnings);
        if (State == BatteryPageState.UpsOnly)
        {
            EmptyTitle = Loc.T("This PC has no battery");
            EmptyMessage = Loc.T("The only battery Windows reports belongs to a UPS (battery backup) plugged in over USB. Its health and run time are shown by the UPS maker's own software.");
            Subtitle = Loc.T("Desktop PCs run from the mains, so there is no battery to check.");
            ClearContent();
            return;
        }

        if (State != BatteryPageState.Ready)
        {
            EmptyTitle = Loc.T("This PC has no battery");
            EmptyMessage = Loc.T("Battery health applies to laptops and tablets. If you use a UPS (battery backup), its maker's software shows the health of its battery.");
            Subtitle = Loc.T("Desktop PCs run from the mains, so there is no battery to check.");
            ClearContent();
            return;
        }

        var summary = BatteryMath.Summarize(overview);
        ApplyTiles(summary);
        ApplyLive(overview.Live);
        ApplyChart(overview.Report.CapacityHistory, summary);
        ApplyLists(overview.Report);
        ApplyDetails(overview, summary);
        ApplyTips(summary, overview.Report.UsageHistory);
        Subtitle = Loc.F("Checked {0}. How worn your battery is and how long a charge lasts.",
            overview.Captured.ToLocalTime().ToString("t", CultureInfo.CurrentCulture));
    }

    private void ApplyTiles(BatterySummary s)
    {
        HealthStatus = BatteryMath.Status(s.WearPercent);
        HealthText = s.HealthPercent is { } health ? Math.Round(health).ToString("0", CultureInfo.CurrentCulture) : "—";
        HealthSubtitle = s.WearPercent switch
        {
            null => Loc.T("The battery does not report its capacity"),
            < 1 => Loc.T("As good as new"),
            { } wear => Loc.F("Holds {0} % less than when new", Math.Round(wear).ToString("0", CultureInfo.CurrentCulture)),
        };

        CyclesText = s.CycleCount?.ToString("N0", CultureInfo.CurrentCulture) ?? "—";
        CyclesSubtitle = s.CycleCount is null
            ? Loc.T("Not counted by this battery")
            : Loc.T("Most batteries are rated for 300 to 1,000");

        CapacityText = s.FullChargeCapacityMwh is { } full ? (full / 1000.0).ToString("0.0", CultureInfo.CurrentCulture) : "—";
        CapacitySubtitle = s.DesignCapacityMwh is { } design
            ? Loc.F("Design capacity: {0}", BatteryMath.FormatWh(design))
            : Loc.T("Design capacity not reported");

        LifeText = BatteryMath.FormatDuration(s.LifeNow);
        LifeSubtitle = s.LifeWhenNew is { } whenNew
            ? Loc.F("When new: {0}", BatteryMath.FormatDuration(whenNew))
            : s.LifeNow is null ? Loc.T("Not enough use on battery yet") : Loc.T("In typical use, from full");
    }

    private void ApplyLive(BatteryLiveStatus? live)
    {
        ChargePercent = live?.ChargePercent ?? 0;
        ChargeText = live?.ChargePercent is { } p ? p.ToString("0", CultureInfo.CurrentCulture) + " %" : "—";
        if (live is null)
        {
            LiveText = Loc.T("The charge state could not be read.");
            return;
        }

        string rate = live.RateW is { } w ? w.ToString("0.0", CultureInfo.CurrentCulture) : string.Empty;
        LiveText = live.State switch
        {
            BatteryPowerState.Discharging when live.TimeRemaining is { } left && rate.Length > 0 =>
                Loc.F("On battery, using {0} W. About {1} left.", rate, BatteryMath.FormatDuration(left)),
            BatteryPowerState.Discharging when rate.Length > 0 => Loc.F("On battery, using {0} W.", rate),
            BatteryPowerState.Discharging => Loc.T("On battery."),
            BatteryPowerState.Charging when rate.Length > 0 => Loc.F("Plugged in, charging at {0} W.", rate),
            BatteryPowerState.Charging => Loc.T("Plugged in and charging."),
            BatteryPowerState.PluggedInNotCharging => Loc.T("Plugged in, not charging. Many laptops pause charging when full or at a charge limit."),
            _ => Loc.T("The charge state could not be read."),
        };
    }

    private void ApplyChart(IReadOnlyList<BatteryCapacityPoint> history, BatterySummary summary)
    {
        var series = BatteryChartBuilder.CapacitySeries(history);
        HasChart = BatteryPageRules.CanDrawCapacityChart(history);
        ChartPoints = series.Points;
        ChartFrom = series.From;
        ChartTo = series.To;
        ChartMinimum = series.Minimum;
        ChartMaximum = series.Maximum;
        ChartDesign = series.DesignWh;
        ChartGap = series.GapThreshold;
        string span = HasChart ? Span(series.To - series.From) : string.Empty;
        ChartCaption = summary.MonthlyLossPercent is { } loss
            ? Loc.F("Full charge capacity over {0}, losing about {1} % of the design capacity a month. The dashed line is the design capacity.",
                span, loss.ToString("0.0", CultureInfo.CurrentCulture))
            : Loc.F("Full charge capacity over {0}. The dashed line is the design capacity.", span);
    }

    private void ApplyLists(BatteryReportData report)
    {
        RecentRows.Clear();
        foreach (var entry in BatteryPageRules.RecentRows(report.RecentUsage))
        {
            RecentRows.Add(BatteryUsageRow.From(entry));
        }

        HistoryRows.Clear();
        var estimates = report.LifeEstimates.GroupBy(e => e.Start.Date).ToDictionary(g => g.Key, g => g.Last());
        foreach (var period in report.UsageHistory.OrderByDescending(u => u.Start).Take(MaxHistoryRows))
        {
            HistoryRows.Add(BatteryHistoryRow.From(period, estimates.GetValueOrDefault(period.Start.Date)));
        }
    }

    private void ApplyDetails(BatteryOverview overview, BatterySummary summary)
    {
        Details.Clear();
        foreach (var pack in overview.Packs)
        {
            string prefix = overview.Packs.Count > 1 ? pack.Name + ": " : string.Empty;
            if (overview.Packs.Count == 1)
            {
                Details.Add(new(Loc.T("Battery"), pack.Name));
            }

            if (pack.Manufacturer is { } maker)
            {
                Details.Add(new(prefix + Loc.T("Manufacturer"), maker));
            }

            if (pack.Chemistry is { } chemistry)
            {
                Details.Add(new(prefix + Loc.T("Chemistry"), chemistry));
            }

            Details.Add(new(prefix + Loc.T("Design capacity"), BatteryMath.FormatWh(pack.DesignCapacityMwh)));
            Details.Add(new(prefix + Loc.T("Full charge capacity"), BatteryMath.FormatWh(pack.FullChargeCapacityMwh)));
        }

        if (summary.WearPercent is { } wear)
        {
            Details.Add(new(Loc.T("Wear"), wear.ToString("0.0", CultureInfo.CurrentCulture) + " %"));
        }

        if (overview.Report.CurrentEstimate?.StandbyAtFullCharge is { } standby)
        {
            Details.Add(new(Loc.T("Asleep from full"), BatteryMath.FormatDuration(standby)));
        }
    }

    private void ApplyTips(BatterySummary summary, IReadOnlyList<BatteryUsagePeriod> usage)
    {
        Tips.Clear();
        foreach (var id in BatteryPageRules.Tips(summary, usage))
        {
            Tips.Add(id switch
            {
                BatteryPageRules.TipIds.Replace => new BatteryTipRow(Loc.T("Think about a new battery"),
                    Loc.T("It has lost much of its capacity. A replacement from the laptop maker brings back the original run time.")),
                BatteryPageRules.TipIds.ChargeLimit => new BatteryTipRow(Loc.T("Use a charge limit if you are often plugged in"),
                    Loc.T("Sitting at 100 % wears a battery faster. Most makers' apps (Lenovo Vantage, MyASUS, Dell Optimizer, HP, Surface) can stop charging at about 80 %.")),
                BatteryPageRules.TipIds.BatterySaver => new BatteryTipRow(Loc.T("Turn on battery saver"),
                    Loc.T("Windows can dim the screen and limit background activity when the charge is low. Choose when in Battery settings.")),
                _ => new BatteryTipRow(Loc.T("Keep it cool"),
                    Loc.T("Heat ages a battery fastest. Avoid gaming on a soft surface and leaving the laptop in a hot car.")),
            });
        }
    }

    private void ClearContent()
    {
        RecentRows.Clear();
        HistoryRows.Clear();
        Tips.Clear();
        Details.Clear();
        HasChart = false;
        ChartPoints = [];
    }

    private static string Span(TimeSpan span) => span.TotalDays switch
    {
        >= 60 => Loc.F("{0} months", Math.Round(span.TotalDays / 30.44).ToString("0", CultureInfo.CurrentCulture)),
        >= 14 => Loc.F("{0} weeks", Math.Round(span.TotalDays / 7).ToString("0", CultureInfo.CurrentCulture)),
        _ => Loc.F("{0} days", Math.Max(1, Math.Round(span.TotalDays)).ToString("0", CultureInfo.CurrentCulture)),
    };

    private void Open(string target)
    {
        var result = ShellLauncher.Open(target);
        if (!result.Success)
        {
            ErrorMessage = result.Message;
        }
    }
}
