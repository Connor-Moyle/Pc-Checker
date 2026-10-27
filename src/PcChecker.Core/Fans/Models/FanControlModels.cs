using PcChecker.Core.Common;
using PcChecker.Core.Sensors;

namespace PcChecker.Core.Fans;

/// <summary>A fan-related sensor as seen by the hardware monitor, reduced to plain values so pairing stays testable.</summary>
public sealed record FanSensorInfo(
    string Id,
    string HardwareId,
    string HardwareName,
    HardwareKind Hardware,
    SensorKind Kind,
    string Name,
    int Index,
    double? Value);

/// <summary>A control sensor matched with the RPM sensor that measures the same fan (when one was found).</summary>
public sealed record FanPair(FanSensorInfo Control, FanSensorInfo? Rpm, string Reason);

/// <summary>Raw view of one hardware control for the advanced panel.</summary>
public sealed record RawFanControl(
    string Identifier,
    string HardwareName,
    string SensorName,
    string ControlMode,
    double? SoftwareValue,
    double MinSoftwareValue,
    double MaxSoftwareValue,
    double? CurrentPercent,
    string? PairedRpmIdentifier);

/// <summary>Why the fan service intervened (for the alerts strip on the Fans page).</summary>
public enum FanAlertKind
{
    ForcedFullSpeed,
    TemperatureSourceMissing,
    ControlLost,
    RecoveredFromCrash,
}

/// <summary>A notable fan-control event, such as fans being forced to 100 % because a part ran too hot.</summary>
public sealed record FanControlAlert(DateTimeOffset Timestamp, FanAlertKind Kind, string FanId, string FanName, string Message, Severity Severity);

/// <summary>A temperature sensor the curve editor can use as its input.</summary>
public sealed record FanTemperatureSource(string Id, string Label, HardwareKind Hardware, double? Value);

/// <summary>
/// Extra capabilities beyond <see cref="IFanControlService"/> that both the Windows and the demo service expose:
/// confirmed below-minimum speeds, the advanced raw control list, alerts and a plain-English capability summary.
/// </summary>
public interface IFanControlAdvanced
{
    /// <summary>Like <see cref="IFanControlService.SetManualAsync"/> but may go below the configured minimum when the user confirmed it.</summary>
    Task<OperationResult> SetManualAsync(string fanId, double percent, bool allowBelowMinimum, CancellationToken cancellationToken);

    /// <summary>Puts one fan on a curve (validated and normalized first) without touching the other fans or the active profile.</summary>
    Task<OperationResult> SetCurveAsync(string fanId, FanCurve curve, CancellationToken cancellationToken);

    /// <summary>What this PC lets the app control, one line per hardware group (e.g. "Case fans: Nuvoton NCT6798D on the motherboard").</summary>
    IReadOnlyList<string> CapabilityNotes { get; }

    /// <summary>Temperature sensors a curve can follow, with their current values. May wait on the hardware monitor lock: call it off the UI thread.</summary>
    IReadOnlyList<FanTemperatureSource> GetTemperatureSources();

    /// <summary>Every control the hardware monitor exposes, for the advanced panel. May wait on the hardware monitor lock: call it off the UI thread.</summary>
    IReadOnlyList<RawFanControl> GetRawControls();

    /// <summary>Recent alerts (newest last).</summary>
    IReadOnlyList<FanControlAlert> Alerts { get; }

    event EventHandler<FanControlAlert>? AlertRaised;

    /// <summary>Which fans are currently under software control (manual or curve).</summary>
    IReadOnlyList<string> SoftwareControlledFanIds { get; }

    /// <summary>
    /// Runs once at app start: reverts fans a crashed previous run left under software control and applies the
    /// saved profile when "apply on startup" is on. Returns plain-English notices for the dashboard.
    /// </summary>
    Task<IReadOnlyList<string>> RunStartupAsync(CancellationToken cancellationToken);

    /// <summary>Runs at app exit: returns fans to automatic control when the setting asks for it and stops the loop.</summary>
    Task ShutdownAsync();
}

/// <summary>Motherboard fan readings the monitor sees: RPM sensors, controls, all sensors on the board's chips, and whether a chip was found.</summary>
public sealed record MotherboardSensorCounts(int Fans, int Controls, int Sensors, bool ChipFound)
{
    public static MotherboardSensorCounts None { get; } = new(0, 0, 0, false);
}

/// <summary>The outcome of "Reload sensors": the motherboard counts before and after re-opening the hardware monitor.</summary>
public sealed record SensorReloadResult(bool Success, string Message, MotherboardSensorCounts Before, MotherboardSensorCounts After);

/// <summary>
/// Diagnostics and recovery for the Fans page: the "Why is a fan missing?" report and a safe "Reload sensors" that
/// returns every fan to automatic, re-opens the hardware monitor (picking up a PawnIO driver installed meanwhile) and
/// detects the fans again.
/// </summary>
public interface IFanDiagnostics
{
    /// <summary>Builds the diagnostic report. May wait on the hardware monitor lock: call it off the UI thread.</summary>
    Task<FanDiagnosticReport> GetDiagnosticsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Returns all fans to automatic, closes and re-opens the hardware monitor and detects the fans again. Refused while
    /// a fan cannot be handed back (held until the PC restarts after a crash) or did not return to automatic.
    /// </summary>
    Task<SensorReloadResult> ReloadSensorsAsync(CancellationToken cancellationToken);
}

/// <summary>Plain-English text for a sensor reload. Pure.</summary>
public static class SensorReloadText
{
    public static string Describe(MotherboardSensorCounts before, MotherboardSensorCounts after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (!after.ChipFound)
        {
            return "Sensors reloaded, but still no motherboard fan controller chip was found. Open \"Why is a fan missing?\" on the Fans page for the reason.";
        }

        int newFans = Math.Max(0, after.Fans - before.Fans);
        int newControls = Math.Max(0, after.Controls - before.Controls);
        if (newFans == 0 && newControls == 0)
        {
            return $"Sensors reloaded: {Count(after.Fans, "motherboard fan")} and {Count(after.Controls, "fan control")} found ({Count(after.Sensors, "motherboard sensor")} in total).";
        }

        return $"Sensors reloaded: {Count(newFans, "motherboard fan")} and {Count(newControls, "fan control")} appeared ({Count(after.Sensors, "motherboard sensor")} in total).";
    }

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
}
