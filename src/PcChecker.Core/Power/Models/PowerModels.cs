namespace PcChecker.Core.Power;

public sealed record PowerSnapshot(
    DateTimeOffset Timestamp,
    double? CpuPackageW,
    double? GpuW,
    double? OtherW,
    double? EstimatedTotalW,
    double? BatteryDischargeW,
    int? BatteryPercent,
    bool OnBattery,
    double SessionEnergyWh);

public sealed record PowerPlanInfo(Guid Id, string Name, bool IsActive);
