using PcChecker.Core.Common;
using PcChecker.Core.Optimization;

namespace PcChecker.Core.Latency;

public sealed record NetworkLatencyResult(string Target, string Label, double? AvgMs, double? MinMs, double? MaxMs, double? JitterMs, double LossPercent, int Sent);

public sealed record LatencyFinding(string Id, Severity Severity, string Title, string Detail, string? ActionId, RiskLevel Risk, bool AdvancedOnly);

public sealed record LatencyReport(
    double? DpcTimePercent,
    double? InterruptTimePercent,
    double? DpcsQueuedPerSec,
    double? InterruptsPerSec,
    double? TimerResolutionMs,
    double? TimerResolutionMinMs,
    bool? HpetForced,
    bool? DynamicTickDisabled,
    bool? HagsEnabled,
    string? PowerPlan,
    bool? CoreParkingAllowed,
    bool? UsbSelectiveSuspend,
    bool? PcieAspmEnabled,
    bool? GameMode,
    bool? PointerPrecision,
    int? MousePollingHz,
    double? DnsMs,
    IReadOnlyList<NetworkLatencyResult> Network,
    IReadOnlyList<LatencyFinding> Findings,
    int Score,
    DateTimeOffset Captured,
    IReadOnlyList<string> Warnings)
{
    /// <summary>GUID of the active power plan; the rules compare this, because <see cref="PowerPlan"/> is translated.</summary>
    public Guid? PowerPlanId { get; init; }

    public static LatencyReport Empty(string? warning = null) =>
        new(null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, [], [], 0, DateTimeOffset.Now, warning is null ? [] : [warning]);
}
