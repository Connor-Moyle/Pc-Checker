namespace PcChecker.Core.Power;

/// <summary>
/// Details a power service can expose beyond <see cref="IPowerService"/>: whether the total is measured or
/// estimated, the per-component breakdown and the battery. The page probes for this with a type check.
/// </summary>
public interface IPowerServiceExtras
{
    /// <summary>True when the total is computed from component sensors instead of a PSU reading.</summary>
    bool IsEstimate { get; }

    /// <summary>Per-component wattages from the latest snapshot.</summary>
    PowerBreakdown Breakdown { get; }

    /// <summary>Latest battery reading (Present = false on desktops).</summary>
    BatteryReading Battery { get; }

    /// <summary>Starts the session energy counter again from zero.</summary>
    void ResetSession();

    /// <summary>When the session counter was last reset.</summary>
    DateTimeOffset SessionStarted { get; }
}
