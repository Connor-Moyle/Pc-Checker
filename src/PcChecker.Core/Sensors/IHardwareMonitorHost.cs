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

    Task EnsureOpenAsync(CancellationToken cancellationToken);

    /// <summary>Runs <paramref name="func"/> against the computer under the host's lock; returns default when closed.</summary>
    T? WithComputer<T>(Func<IComputer, T> func);

    /// <summary>Runs <paramref name="action"/> against the computer under the host's lock; no-op when closed.</summary>
    void WithComputer(Action<IComputer> action);
}
