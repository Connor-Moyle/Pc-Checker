using LibreHardwareMonitor.Hardware;

namespace PcChecker.Core.Sensors;

/// <summary>Demo host: never opens a real monitor, so callers take their "unavailable" paths.</summary>
public sealed class DemoHardwareMonitorHost : IHardwareMonitorHost
{
    public bool IsOpen => false;

    public string? OpenError => "Demo mode: the hardware monitor driver is not loaded.";

    public Task EnsureOpenAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public T? WithComputer<T>(Func<IComputer, T> func) => default;

    public void WithComputer(Action<IComputer> action)
    {
    }
}
