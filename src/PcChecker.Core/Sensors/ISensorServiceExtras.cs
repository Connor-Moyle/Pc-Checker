namespace PcChecker.Core.Sensors;

/// <summary>
/// Optional extras a sensor service can offer beyond <see cref="ISensorService"/>: notes about partially
/// available hardware, a min/max reset and a restart for the "Retry" button. The pages probe for this with
/// a type check so the contract itself stays unchanged.
/// </summary>
public interface ISensorServiceExtras
{
    /// <summary>Non-fatal notes, e.g. "No CPU temperature sensor found: run as administrator."</summary>
    IReadOnlyList<string> Warnings { get; }

    /// <summary>Starts the session min/max of every sensor again from the current value.</summary>
    void ResetMinMax();

    /// <summary>Stops, re-opens the hardware monitor and starts polling again.</summary>
    void Restart();
}
