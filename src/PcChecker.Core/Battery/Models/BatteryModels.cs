namespace PcChecker.Core.Battery;

/// <summary>What a battery was doing during one stretch of the battery report's recent usage.</summary>
public enum BatteryUsageState
{
    Unknown,

    /// <summary>The screen was on and the PC was in use.</summary>
    Active,

    /// <summary>Sleep or Modern Standby (connected standby).</summary>
    Standby,

    /// <summary>Hibernated or shut down.</summary>
    Off,

    /// <summary>The marker powercfg writes when it creates the report; it only closes the previous stretch.</summary>
    ReportGenerated,
}

/// <summary>Whether the battery is charging, running the PC, or idle on the charger right now.</summary>
public enum BatteryPowerState
{
    Unknown,
    Discharging,
    Charging,
    PluggedInNotCharging,
}

/// <summary>What kind of computer Windows says this is (Win32_ComputerSystem.PCSystemType).</summary>
public enum PcFormFactor
{
    Unknown,
    Desktop,
    Laptop,
    Server,
}

/// <summary>One battery pack as Windows and the battery report describe it. Capacities are in milliwatt-hours.</summary>
public sealed record BatteryPackInfo(
    string Name,
    string? Manufacturer,
    string? Chemistry,
    int? DesignCapacityMwh,
    int? FullChargeCapacityMwh,
    int? CycleCount)
{
    /// <summary>Lost capacity in percent of the design capacity (0 for a battery that still holds its design charge).</summary>
    public double? WearPercent => BatteryMath.WearPercent(DesignCapacityMwh, FullChargeCapacityMwh);

    /// <summary>Full-charge capacity in percent of the design capacity, at most 100.</summary>
    public double? HealthPercent => BatteryMath.HealthPercent(DesignCapacityMwh, FullChargeCapacityMwh);
}

/// <summary>The live charge state. <see cref="RateW"/> is the charge or discharge rate in watts, always positive.</summary>
public sealed record BatteryLiveStatus(
    int? ChargePercent,
    BatteryPowerState State,
    double? RateW,
    int? RemainingMwh,
    TimeSpan? TimeRemaining);

/// <summary>One row of the battery report's capacity history (one day or one week).</summary>
public sealed record BatteryCapacityPoint(
    DateTime Start,
    DateTime End,
    int? DesignCapacityMwh,
    int? FullChargeCapacityMwh,
    int? CycleCount);

/// <summary>
/// One stretch of the battery report's recent usage: from <see cref="Start"/> for <see cref="Duration"/> the PC was in
/// <see cref="State"/>, on the charger or not, and the charge went from <see cref="StartChargeMwh"/> to
/// <see cref="EndChargeMwh"/> (the next stretch's starting charge).
/// </summary>
public sealed record BatteryUsageEntry(
    DateTime Start,
    TimeSpan Duration,
    BatteryUsageState State,
    bool OnAc,
    int? StartChargeMwh,
    int? EndChargeMwh,
    int? FullChargeCapacityMwh)
{
    /// <summary>Charge at the start in percent of the full-charge capacity.</summary>
    public double? StartPercent => BatteryMath.PercentOf(StartChargeMwh, FullChargeCapacityMwh);

    /// <summary>Percentage points lost during the stretch (negative while charging).</summary>
    public double? DrainPercent => BatteryMath.DrainPercent(this);

    /// <summary>Percentage points lost per hour (negative while charging); null for stretches under a few minutes.</summary>
    public double? DrainPercentPerHour => BatteryMath.DrainPercentPerHour(this);
}

/// <summary>One row of the battery report's usage history: how long the PC was used and asleep, on battery and plugged in.</summary>
public sealed record BatteryUsagePeriod(
    DateTime Start,
    DateTime End,
    TimeSpan ActiveOnBattery,
    TimeSpan StandbyOnBattery,
    TimeSpan ActiveOnAc,
    TimeSpan StandbyOnAc)
{
    public TimeSpan OnBattery => ActiveOnBattery + StandbyOnBattery;

    public TimeSpan OnAc => ActiveOnAc + StandbyOnAc;
}

/// <summary>
/// One row of the battery report's battery life estimates: how long a full battery lasts in use and asleep, at the
/// capacity the battery had then and at its design capacity, based on how the PC was used in that period.
/// </summary>
public sealed record BatteryLifeEstimate(
    DateTime Start,
    DateTime End,
    int? FullChargeCapacityMwh,
    TimeSpan? ActiveAtFullCharge,
    TimeSpan? StandbyAtFullCharge,
    int? DesignCapacityMwh,
    TimeSpan? ActiveAtDesign,
    TimeSpan? StandbyAtDesign);

/// <summary>The report's overall estimate since the operating system was installed.</summary>
public sealed record BatteryRuntimeEstimate(
    TimeSpan? ActiveAtFullCharge,
    TimeSpan? StandbyAtFullCharge,
    TimeSpan? ActiveAtDesign,
    TimeSpan? StandbyAtDesign);

/// <summary>Everything read from <c>powercfg /batteryreport /xml</c>. Times are the PC's local times.</summary>
public sealed record BatteryReportData(
    DateTime? ScanTime,
    string? PlatformRole,
    IReadOnlyList<BatteryPackInfo> Batteries,
    IReadOnlyList<BatteryCapacityPoint> CapacityHistory,
    IReadOnlyList<BatteryUsageEntry> RecentUsage,
    IReadOnlyList<BatteryUsagePeriod> UsageHistory,
    IReadOnlyList<BatteryLifeEstimate> LifeEstimates,
    BatteryRuntimeEstimate? CurrentEstimate)
{
    public static BatteryReportData Empty { get; } = new(null, null, [], [], [], [], [], null);
}

/// <summary>
/// The Battery page's data: the packs (WMI merged with the report), the live charge state, the parsed battery report
/// and whatever could not be read. <see cref="HasBattery"/> is false on desktops.
/// </summary>
public sealed record BatteryOverview(
    bool HasBattery,
    PcFormFactor FormFactor,
    IReadOnlyList<BatteryPackInfo> Packs,
    BatteryLiveStatus? Live,
    BatteryReportData Report,
    DateTimeOffset Captured,
    IReadOnlyList<string> Warnings)
{
    public static BatteryOverview NoBattery(PcFormFactor formFactor, DateTimeOffset captured, IReadOnlyList<string>? warnings = null) =>
        new(false, formFactor, [], null, BatteryReportData.Empty, captured, warnings ?? []);
}

/// <summary>The headline numbers for all packs together (laptops with two batteries add up).</summary>
public sealed record BatterySummary(
    int? DesignCapacityMwh,
    int? FullChargeCapacityMwh,
    double? HealthPercent,
    double? WearPercent,
    int? CycleCount,
    TimeSpan? LifeNow,
    TimeSpan? LifeWhenNew,
    double? MonthlyLossPercent);
