using PcChecker.Core.Hardware;

namespace PcChecker.Core.Overclocking.GpuLoad;

/// <summary>Plain-English stress-test advice that depends on the CPU. Pure.</summary>
public static class StressAdvice
{
    /// <summary>
    /// On Ryzen, Curve Optimizer and PBO instability shows under light single-core boost loads and at idle, which an
    /// all-core test does not reach. Null for other CPUs.
    /// </summary>
    public static string? AmdSingleCoreNote(string? cpuName)
    {
        var traits = CpuTraits.Parse(cpuName);
        if (!traits.IsAmd || traits.FormFactor is CpuFormFactor.Laptop or CpuFormFactor.Handheld)
        {
            return null;
        }

        return "Ryzen: this test loads every core at once. Curve Optimizer and PBO problems show up under light single-core boost and at idle, so also run a per-core test (for example CoreCycler or y-cruncher single-threaded) and check the Health page for WHEA hardware errors afterwards.";
    }
}
