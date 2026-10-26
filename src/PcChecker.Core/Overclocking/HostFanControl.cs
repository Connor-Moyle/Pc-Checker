using LibreHardwareMonitor.Hardware;
using PcChecker.Core.Common;
using PcChecker.Core.Sensors;

namespace PcChecker.Core.Overclocking;

/// <summary>GPU fan control through the shared LibreHardwareMonitor host (ADDENDUM §E, §H).</summary>
public static class HostFanControl
{
    /// <summary>True when the host is open and the GPU exposes at least one software-controllable fan.</summary>
    public static bool IsAvailable(IHardwareMonitorHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (!host.IsOpen)
        {
            return false;
        }

        try
        {
            return host.WithComputer(computer => GpuFanControls(computer).Any());
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Fixes every GPU fan at <paramref name="percent"/>, or returns them to automatic when null.</summary>
    public static OperationResult Set(IHardwareMonitorHost host, int? percent)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (!host.IsOpen)
        {
            return OperationResult.Fail("The hardware monitor is not open, so fan control is unavailable.");
        }

        try
        {
            int changed = 0;
            host.WithComputer(computer =>
            {
                foreach (var control in GpuFanControls(computer))
                {
                    if (percent is { } p)
                    {
                        control.SetSoftware(Math.Clamp(p, (int)control.MinSoftwareValue, (int)control.MaxSoftwareValue));
                    }
                    else
                    {
                        control.SetDefault();
                    }

                    changed++;
                }
            });

            return changed == 0
                ? OperationResult.Fail("The hardware monitor found no controllable GPU fan.")
                : OperationResult.Ok(percent is null ? "GPU fans returned to automatic control." : $"GPU fans fixed at {percent} %.");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail("GPU fan control failed: " + ex.Message, ex);
        }
    }

    /// <summary>Current GPU fan percentage from the host, when available.</summary>
    public static double? CurrentPercent(IHardwareMonitorHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (!host.IsOpen)
        {
            return null;
        }

        try
        {
            return host.WithComputer(computer => GpuHardware(computer)
                .SelectMany(h => h.Sensors)
                .FirstOrDefault(s => s.SensorType == SensorType.Control)?.Value);
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<IHardware> GpuHardware(IComputer computer) =>
        computer.Hardware.Where(h => h.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel);

    private static IEnumerable<IControl> GpuFanControls(IComputer computer) =>
        GpuHardware(computer).SelectMany(h => h.Sensors).Where(s => s.SensorType == SensorType.Control && s.Control is not null).Select(s => s.Control!);
}
