using LibreHardwareMonitor.Hardware;

namespace PcChecker.Core.Sensors;

/// <summary>
/// The single shared LibreHardwareMonitor instance. Fans, Health (SMART) and Overclock read through this host
/// instead of opening their own <c>Computer</c>, which would conflict over the kernel driver.
/// </summary>
public interface IHardwareMonitorHost
{
    bool IsOpen { get; }

    /// <summary>Why the monitor could not be opened, or null when it is open.</summary>
    string? OpenError { get; }

    /// <summary>
    /// Why the CPU and motherboard sensors cannot be read even though the monitor opened (the PawnIO driver they need is
    /// not installed), or null when nothing is known to be missing.
    /// </summary>
    string? DriverProblem => null;

    /// <summary>
    /// Goes up by one every time the monitor is (re)opened. Sensor and control objects from an older generation belong to
    /// a closed monitor: callers that keep them (the fan service) must detect their hardware again.
    /// </summary>
    int Generation => 0;

    Task EnsureOpenAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Closes and re-opens the monitor, so a driver installed meanwhile (PawnIO) is picked up without restarting the app.
    /// Closing hands every fan the monitor changed back to the firmware. Returns true when it is open again.
    /// </summary>
    Task<bool> ReopenAsync(CancellationToken cancellationToken) => Task.FromResult(false);

    /// <summary>Runs <paramref name="func"/> against the computer under the host's lock; returns default when closed.</summary>
    T? WithComputer<T>(Func<IComputer, T> func);

    /// <summary>Runs <paramref name="action"/> against the computer under the host's lock; no-op when closed.</summary>
    void WithComputer(Action<IComputer> action);
}
