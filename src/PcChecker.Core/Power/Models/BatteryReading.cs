namespace PcChecker.Core.Power;

/// <summary>What the battery reports right now. <see cref="Present"/> is false on desktops.</summary>
public sealed record BatteryReading(
    bool Present,
    bool OnBattery,
    bool Charging,
    int? Percent,
    double? DischargeW,
    double? ChargeW,
    int? RemainingCapacityMwh,
    int? FullChargeCapacityMwh,
    TimeSpan? EstimatedRemaining,
    string? Note)
{
    public static BatteryReading None(string? note = null) => new(false, false, false, null, null, null, null, null, null, note);
}
