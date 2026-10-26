namespace PcChecker.Core.Overclocking;

/// <summary>Inputs for the support matrix: what was detected, independent of how.</summary>
public sealed record SupportFacts(
    GpuVendor GpuVendor,
    GpuControlMethod GpuMethod,
    GpuLimits? GpuLimits,
    bool GpuIsIntegrated,
    string CpuVendor,
    CpuControlMethod CpuMethod,
    string XmpState,
    bool FanHostAvailable);

/// <summary>Pure builder for the "Supported hardware" table shown on the Overclock page (ADDENDUM §H).</summary>
public static class SupportMatrixBuilder
{
    public static IReadOnlyList<(string Feature, string Vendor, string Level, string Note)> Build(SupportFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var rows = new List<(string, string, string, string)>();
        string gpuVendor = GpuVendorDetector.DisplayName(facts.GpuVendor);
        var limits = facts.GpuLimits;

        // GPU rows
        switch (facts.GpuMethod)
        {
            case GpuControlMethod.NvApi:
                rows.Add(("GPU core and memory offset", gpuVendor, limits is { CoreOffsetMinMhz: 0, CoreOffsetMaxMhz: 0 } ? SupportLevel.MonitorOnly : SupportLevel.Full,
                    "Applied through the NVIDIA driver (NVAPI performance states); resets on reboot."));
                rows.Add(("GPU power limit", gpuVendor, limits is null || limits.PowerLimitMinPercent == limits.PowerLimitMaxPercent ? SupportLevel.Unsupported : SupportLevel.Full,
                    limits is null || limits.PowerLimitMinPercent == limits.PowerLimitMaxPercent ? "The driver did not expose a power policy for this card." : "NVIDIA power policy."));
                rows.Add(("GPU temperature limit", gpuVendor, limits is null || limits.TempLimitMinC == limits.TempLimitMaxC ? SupportLevel.Unsupported : SupportLevel.Full,
                    limits is null || limits.TempLimitMinC == limits.TempLimitMaxC ? "The driver did not expose a thermal policy for this card." : "NVIDIA thermal policy."));
                break;
            case GpuControlMethod.AmdAdl:
                rows.Add(("GPU core and memory clock", gpuVendor, SupportLevel.Experimental, "Overdrive8 through atiadlxx.dll; every value is checked against the driver's own range. AMD Software must be installed."));
                rows.Add(("GPU power limit", gpuVendor, SupportLevel.Experimental, "Overdrive8 power percentage (relative to the card's default)."));
                rows.Add(("GPU temperature limit", gpuVendor, SupportLevel.Experimental, "Overdrive8 operating temperature target."));
                break;
            case GpuControlMethod.IntelIgcl:
                rows.Add(("GPU core and memory offset", gpuVendor, SupportLevel.Experimental, "Intel Graphics Control Library (ControlLib.dll). Arc discrete cards only."));
                rows.Add(("GPU power limit", gpuVendor, SupportLevel.Experimental, "IGCL sustained power limit."));
                rows.Add(("GPU temperature limit", gpuVendor, SupportLevel.Experimental, "IGCL temperature limit."));
                break;
            default:
                string why = facts.GpuVendor switch
                {
                    _ when facts.GpuIsIntegrated => "Integrated graphics share the CPU's power budget and cannot be tuned here.",
                    GpuVendor.Nvidia => "The NVIDIA driver did not expose tuning (nvapi64.dll missing or an unsupported card).",
                    GpuVendor.Amd => "AMD Software: Adrenalin Edition is not installed or Overdrive is locked.",
                    GpuVendor.Intel => "Intel Graphics Software / the Control Library is not installed.",
                    _ => "No graphics card was recognised.",
                };
                rows.Add(("GPU core and memory offset", gpuVendor, SupportLevel.Unsupported, why));
                rows.Add(("GPU power limit", gpuVendor, SupportLevel.Unsupported, why));
                rows.Add(("GPU temperature limit", gpuVendor, SupportLevel.Unsupported, why));
                break;
        }

        rows.Add(("GPU fan speed", gpuVendor,
            facts.FanHostAvailable ? SupportLevel.Full : facts.GpuMethod == GpuControlMethod.NvApi ? SupportLevel.Partial : SupportLevel.Unsupported,
            facts.FanHostAvailable ? "Through the shared hardware monitor; fan curves live on the Fans page."
                : facts.GpuMethod == GpuControlMethod.NvApi ? "Fixed percentage through NVAPI coolers; the hardware monitor driver is not loaded so curves are unavailable."
                : "Use the Fans page once the hardware monitor driver is available."));

        // CPU rows
        string cpuVendor = string.IsNullOrWhiteSpace(facts.CpuVendor) ? "Unknown" : facts.CpuVendor;
        switch (facts.CpuMethod)
        {
            case CpuControlMethod.IntelXtuCli:
                rows.Add(("CPU all-core ratio and voltage offset", cpuVendor, SupportLevel.Partial, "Through the Intel XTU command line; tunable names vary by version, so unknown layouts fail safely."));
                rows.Add(("CPU power limit", cpuVendor, SupportLevel.Partial, "Through Intel XTU (long-duration power limit)."));
                break;
            case CpuControlMethod.RyzenAdj:
                rows.Add(("CPU all-core ratio and voltage offset", cpuVendor, SupportLevel.Unsupported, "ryzenadj cannot set ratios or voltages; use the BIOS (Precision Boost Overdrive / Curve Optimizer)."));
                rows.Add(("CPU power limit", cpuVendor, SupportLevel.Experimental, "Through ryzenadj (STAPM / fast / slow limits). Intended for laptops and APUs."));
                break;
            case CpuControlMethod.RyzenMaster:
            case CpuControlMethod.RyzenMasterCli:
                rows.Add(("CPU all-core ratio and voltage offset", cpuVendor, SupportLevel.Unsupported, "AMD Ryzen Master is installed; PC Checker opens it for you because its tuning API is not public."));
                rows.Add(("CPU power limit", cpuVendor, SupportLevel.Unsupported, "Set PPT/TDC/EDC limits in Ryzen Master or the BIOS."));
                break;
            default:
                string hint = cpuVendor.Contains("Intel", StringComparison.OrdinalIgnoreCase)
                    ? "Install Intel Extreme Tuning Utility (XTU) or change these in the BIOS."
                    : cpuVendor.Contains("AMD", StringComparison.OrdinalIgnoreCase)
                        ? "Install AMD Ryzen Master or change these in the BIOS."
                        : "Change these in the BIOS.";
                rows.Add(("CPU all-core ratio and voltage offset", cpuVendor, SupportLevel.Unsupported, hint));
                rows.Add(("CPU power limit", cpuVendor, SupportLevel.Unsupported, hint));
                break;
        }

        rows.Add(("CPU temperature and clock readout", cpuVendor, SupportLevel.MonitorOnly, "Live from the hardware monitor; used by the stress test watchdog."));

        // RAM row
        string ramNote = facts.XmpState switch
        {
            XmpEvaluation.Disabled => "XMP/EXPO is off: enable it in the BIOS to run memory at its rated speed. See the Memory page.",
            XmpEvaluation.Enabled => "XMP/EXPO is active. Timings and voltages can only be changed in the BIOS. See the Memory page.",
            _ => "Memory profile state unknown; the Memory page shows module details.",
        };
        rows.Add(("Memory XMP / EXPO", cpuVendor, SupportLevel.MonitorOnly, ramNote));

        return rows;
    }
}
