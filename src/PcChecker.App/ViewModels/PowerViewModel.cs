using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Localization;
using PcChecker.App.Services;
using PcChecker.App.ViewModels.Power;
using PcChecker.Core.Power;
using PcChecker.Core.Restore;
using PcChecker.Core.Sensors;
using PcChecker.Core.Settings;

namespace PcChecker.App.ViewModels;

/// <summary>
/// View model for the Power page: headline wattages, the total-draw history, a per-component table, the battery,
/// the Windows power plan selector and the electricity price used for cost estimates.
/// </summary>
public sealed partial class PowerViewModel : ViewModelBase
{
    private const int SparklinePoints = 120;

    private readonly IPowerService _power;
    private readonly ISensorService _sensors;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly ILogger<PowerViewModel> _logger;
    private readonly Dictionary<string, PowerComponentRow> _rows = new(StringComparer.Ordinal);
    private DateTimeOffset _lastUiUpdate = DateTimeOffset.MinValue;
    private int _uiPending;
    private bool _subscribed;

    [ObservableProperty]
    private string _totalText = "—";

    [ObservableProperty]
    private string _totalSubtitle = Loc.T("Waiting for readings");

    [ObservableProperty]
    private string _cpuText = "—";

    [ObservableProperty]
    private string _gpuText = "—";

    [ObservableProperty]
    private string _otherText = "—";

    [ObservableProperty]
    private string _batteryText = "—";

    [ObservableProperty]
    private string _batterySubtitle = string.Empty;

    [ObservableProperty]
    private string _energyText = "0.0";

    [ObservableProperty]
    private string _energySubtitle = string.Empty;

    [ObservableProperty]
    private string _costHourText = "—";

    [ObservableProperty]
    private string _costDayText = "—";

    [ObservableProperty]
    private string _sessionCostText = "—";

    [ObservableProperty]
    private string _historySummary = string.Empty;

    [ObservableProperty]
    private bool _isEstimate = true;

    [ObservableProperty]
    private bool _hasPowerData;

    [ObservableProperty]
    private bool _hasBattery;

    [ObservableProperty]
    private double _batteryPercent;

    [ObservableProperty]
    private string _batteryStatusText = Loc.T("No battery detected — this looks like a desktop PC.");

    [ObservableProperty]
    private string _batteryDetailText = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<double> _history = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyPlanCommand))]
    private PowerPlanInfo? _selectedPlan;

    [ObservableProperty]
    private string _activePlanName = "—";

    [ObservableProperty]
    private string _selectedPlanExplanation = string.Empty;

    [ObservableProperty]
    private bool _plansUnavailable;

    [ObservableProperty]
    private double _pricePerKwh;

    [ObservableProperty]
    private string _currencySymbol = "$";

    [ObservableProperty]
    private string _statusText = string.Empty;

    public PowerViewModel(IPowerService power, ISensorService sensors, ISettingsService settings, IDialogService dialogs,
        INotificationService notifications, ILogger<PowerViewModel> logger)
        : base(settings)
    {
        _power = power;
        _sensors = sensors;
        _dialogs = dialogs;
        _notifications = notifications;
        _logger = logger;
        PricePerKwh = settings.Current.General.PricePerKwh;
        CurrencySymbol = settings.Current.General.CurrencySymbol;
    }

    public ObservableCollection<PowerComponentRow> Components { get; } = [];

    public ObservableCollection<PowerPlanInfo> Plans { get; } = [];

    public override async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (!_subscribed)
        {
            _subscribed = true;
            _power.Updated += OnPowerUpdated;
        }

        await RunBusyAsync(async ct =>
        {
            _sensors.Start();
            await LoadPlansAsync(ct);
            await WaitForSnapshotAsync(TimeSpan.FromSeconds(5), ct);
        }, Loc.T("Reading power information…"));
    }

    public override Task RefreshAsync(CancellationToken cancellationToken) => RunBusyAsync(async ct =>
    {
        await LoadPlansAsync(ct);
        if (_power.Latest is { } latest)
        {
            Apply(latest);
        }
    }, Loc.T("Refreshing…"));

    [RelayCommand]
    private Task RefreshPlansAsync() => RefreshAsync(CancellationToken.None);

    [RelayCommand(CanExecute = nameof(CanApplyPlan))]
    private async Task ApplyPlanAsync()
    {
        if (SelectedPlan is not { } plan)
        {
            return;
        }

        bool ok = await _dialogs.ConfirmAsync(
            Loc.T("Switch power plan?"),
            Loc.F("Windows will switch to the \"{0}\" power plan.", plan.Name) + "\n\n" + PowerPlanParser.Explain(plan.Id) + "\n\n"
            + Loc.T("You can switch back at any time from this page.") + "\n" + RestorePointPolicyText.Note(Settings.Current.RestorePoints.CreateBeforeChanges),
            Loc.T("Switch plan"));
        if (!ok)
        {
            return;
        }

        var previous = Plans.FirstOrDefault(p => p.IsActive);
        await RunBusyAsync(async ct =>
        {
            var result = previous is null
                ? await _power.SetActivePowerPlanAsync(plan.Id, ct)
                : await UndoOffers.RunAsync(
                    () => _power.SetActivePowerPlanAsync(plan.Id, ct),
                    async undoCt =>
                    {
                        // Undo from the success notification: back to the plan that was active, and the list re-read.
                        var back = await _power.SetActivePowerPlanAsync(previous.Id, undoCt);
                        DispatcherHelper.PostToUi(async () =>
                        {
                            try
                            {
                                await LoadPlansAsync(CancellationToken.None);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogDebug(ex, "Could not re-read the power plans after an undo.");
                            }
                        });
                        return back;
                    },
                    plan.Name);
            if (result.Success)
            {
                _notifications.Success(Loc.T("Power plan changed"), result.Message);
            }
            else
            {
                _notifications.Error(Loc.T("Power plan not changed"), result.Message);
                ErrorMessage = result.Message;
            }

            await LoadPlansAsync(ct);
        }, Loc.T("Switching power plan…"));
    }

    private bool CanApplyPlan() => SelectedPlan is { IsActive: false };

    [RelayCommand]
    private async Task SavePriceAsync()
    {
        if (PricePerKwh < 0 || double.IsNaN(PricePerKwh))
        {
            ErrorMessage = Loc.T("The electricity price cannot be negative.");
            return;
        }

        Settings.Current.General.PricePerKwh = Math.Round(PricePerKwh, 4);
        Settings.Current.General.CurrencySymbol = string.IsNullOrWhiteSpace(CurrencySymbol) ? "$" : CurrencySymbol.Trim();
        try
        {
            await Settings.SaveAsync();
            CurrencySymbol = Settings.Current.General.CurrencySymbol;
            _notifications.Success(Loc.T("Price saved"), Loc.F("Costs are now calculated at {0}{1:0.###} per kWh.", CurrencySymbol, PricePerKwh));
            if (_power.Latest is { } latest)
            {
                Apply(latest);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Saving the electricity price failed.");
            ErrorMessage = Loc.T("The price could not be saved:") + " " + ex.Message;
        }
    }

    [RelayCommand]
    private void ResetSession()
    {
        (_power as IPowerServiceExtras)?.ResetSession();
        if (_power.Latest is { } latest)
        {
            Apply(latest);
        }

        _notifications.Info(Loc.T("Session reset"), Loc.T("Energy and cost counters start again from zero."));
    }

    partial void OnSelectedPlanChanged(PowerPlanInfo? value) =>
        SelectedPlanExplanation = value is null ? string.Empty : PowerPlanParser.Explain(value.Id);

    protected override void OnBusyError(Exception exception) => _logger.LogError(exception, "Power page failed.");

    private async Task LoadPlansAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<PowerPlanInfo> plans;
        try
        {
            plans = await _power.GetPowerPlansAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Power plans could not be read.");
            plans = [];
        }

        Plans.Clear();
        foreach (var plan in plans)
        {
            Plans.Add(plan);
        }

        PlansUnavailable = plans.Count == 0;
        var active = plans.FirstOrDefault(p => p.IsActive);
        ActivePlanName = active?.Name ?? Loc.T("Unknown");
        SelectedPlan = active ?? plans.FirstOrDefault();
        ApplyPlanCommand.NotifyCanExecuteChanged();
    }

    private async Task WaitForSnapshotAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.Now + timeout;
        while (DateTimeOffset.Now < deadline)
        {
            if (_power.Latest is { } latest)
            {
                Apply(latest);
                return;
            }

            await Task.Delay(150, cancellationToken);
        }

        StatusText = _sensors.IsAvailable
            ? Loc.T("No power readings yet.")
            : Loc.T("Hardware sensors are not available:") + " " + (_sensors.UnavailableReason ?? Loc.T("unknown reason")) + " "
                + Loc.T("Battery readings still work when a battery is present.");
    }

    private void OnPowerUpdated(object? sender, PowerSnapshot snapshot)
    {
        // The service can publish several times a second; the page only needs one update per second.
        if (DateTimeOffset.Now - _lastUiUpdate < TimeSpan.FromMilliseconds(900) || Interlocked.Exchange(ref _uiPending, 1) == 1)
        {
            return;
        }

        DispatcherHelper.PostToUi(() =>
        {
            try
            {
                Apply(snapshot);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Applying a power snapshot to the page failed.");
            }
            finally
            {
                Interlocked.Exchange(ref _uiPending, 0);
            }
        });
    }

    private void Apply(PowerSnapshot snapshot)
    {
        _lastUiUpdate = DateTimeOffset.Now;
        var culture = CultureInfo.CurrentCulture;
        var extras = _power as IPowerServiceExtras;
        var breakdown = extras?.Breakdown ?? PowerMath.Aggregate(_sensors.Latest?.Readings ?? []);
        var battery = extras?.Battery ?? BatteryReading.None();
        double price = Settings.Current.General.PricePerKwh;
        string symbol = Settings.Current.General.CurrencySymbol;

        IsEstimate = extras?.IsEstimate ?? true;
        HasPowerData = snapshot.EstimatedTotalW is not null;
        TotalText = Watts(snapshot.EstimatedTotalW);
        TotalSubtitle = snapshot.EstimatedTotalW is null
            ? Loc.T("No power sensors found on this PC")
            : IsEstimate ? Loc.T("Estimated from component sensors") : Loc.T("Measured by the power supply");
        CpuText = Watts(snapshot.CpuPackageW);
        GpuText = Watts(snapshot.GpuW);
        OtherText = Watts(snapshot.OtherW);

        EnergyText = snapshot.SessionEnergyWh < 10 ? snapshot.SessionEnergyWh.ToString("0.00", culture) : snapshot.SessionEnergyWh.ToString("0.0", culture);
        var since = extras?.SessionStarted ?? DateTimeOffset.MinValue;
        EnergySubtitle = since == DateTimeOffset.MinValue ? Loc.T("since the app started") : Loc.F("since {0}", since.ToLocalTime().ToString("HH:mm", culture));
        CostHourText = Money(PowerMath.CostPerHour(snapshot.EstimatedTotalW, price), symbol);
        CostDayText = Money(PowerMath.CostPerDay(snapshot.EstimatedTotalW, price), symbol);
        SessionCostText = Money(PowerMath.SessionCost(snapshot.SessionEnergyWh, price), symbol);

        ApplyBattery(snapshot, battery);
        ApplyComponents(breakdown, snapshot.EstimatedTotalW);
        ApplyHistory();
        StatusText = Loc.F("Updated {0}", snapshot.Timestamp.ToLocalTime().ToString("HH:mm:ss", culture));
    }

    private void ApplyBattery(PowerSnapshot snapshot, BatteryReading battery)
    {
        HasBattery = battery.Present;
        if (!battery.Present)
        {
            BatteryText = "—";
            BatterySubtitle = Loc.T("No battery");
            BatteryPercent = 0;
            BatteryStatusText = Loc.T("No battery detected — this looks like a desktop PC.");
            BatteryDetailText = battery.Note ?? string.Empty;
            return;
        }

        BatteryPercent = battery.Percent ?? 0;
        if (battery.OnBattery)
        {
            BatteryText = Watts(battery.DischargeW);
            BatterySubtitle = Loc.T("Discharging");
            BatteryStatusText = Loc.F("Running on battery · {0}%", battery.Percent?.ToString(CultureInfo.CurrentCulture) ?? "?");
            BatteryDetailText = battery.EstimatedRemaining is { } left
                ? Loc.F("About {0} remaining at the current rate.", Duration(left))
                : Loc.T("Time remaining is not known yet.");
        }
        else
        {
            BatteryText = battery.Charging ? Watts(battery.ChargeW) : "0";
            BatterySubtitle = battery.Charging ? Loc.T("Charging") : Loc.T("Plugged in");
            string percent = battery.Percent?.ToString(CultureInfo.CurrentCulture) ?? "?";
            BatteryStatusText = battery.Charging ? Loc.F("Plugged in · {0}% · charging", percent) : Loc.F("Plugged in · {0}%", percent);
            BatteryDetailText = battery.FullChargeCapacityMwh is { } full && battery.RemainingCapacityMwh is { } remaining
                ? Loc.F("{0:0.0} Wh of {1:0.0} Wh stored.", remaining / 1000.0, full / 1000.0)
                : string.Empty;
        }
    }

    private void ApplyComponents(PowerBreakdown breakdown, double? totalW)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var component in breakdown.Components.OrderByDescending(c => c.Watts))
        {
            string key = component.HardwareName + "|" + component.Name;
            seen.Add(key);
            if (!_rows.TryGetValue(key, out var row))
            {
                row = new PowerComponentRow(component);
                _rows[key] = row;
                Components.Add(row);
            }

            row.Update(component, totalW);
        }

        for (int i = Components.Count - 1; i >= 0; i--)
        {
            if (!seen.Contains(Components[i].Key))
            {
                _rows.Remove(Components[i].Key);
                Components.RemoveAt(i);
            }
        }
    }

    private void ApplyHistory()
    {
        var history = _power.GetHistory(TimeSpan.FromMinutes(Math.Max(1, Settings.Current.Monitoring.HistoryMinutes)));
        var values = history.Select(h => h.Value).ToList();
        History = SensorMath.Downsample(values, SparklinePoints);
        var stats = SensorMath.Summarize(values);
        HistorySummary = stats is null
            ? Loc.T("Collecting history…")
            : Loc.F("Average {0:0} W · peak {1:0} W · low {2:0} W over the last {3} min", stats.Average, stats.Max, stats.Min, Settings.Current.Monitoring.HistoryMinutes);
    }

    private static string Watts(double? value) => value is { } w && !double.IsNaN(w)
        ? w.ToString(w < 10 ? "0.0" : "0", CultureInfo.CurrentCulture)
        : "—";

    private static string Money(double amount, string symbol) =>
        amount <= 0 ? symbol + "0.00" : symbol + amount.ToString(amount < 0.1 ? "0.000" : "0.00", CultureInfo.CurrentCulture);

    private static string Duration(TimeSpan span) => span.TotalHours >= 1
        ? $"{(int)span.TotalHours} h {span.Minutes:00} min"
        : $"{Math.Max(1, (int)span.TotalMinutes)} min";
}
