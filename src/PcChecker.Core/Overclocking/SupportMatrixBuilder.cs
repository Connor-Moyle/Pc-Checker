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
    bool FanHostAvailable)
{
    /// <summary>The GPU's name (decides whether an AMD card uses Overdrive8 for its fan).</summary>
    public string? GpuName { get; init; }

    /// <summary>True when AMD Software reports manual tuning (Overdrive) as switched off.</summary>
    public bool GpuTuningSwitchedOff { get; init; }

    public string? CpuName { get; init; }
}

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
            {
                string off = facts.GpuTuningSwitchedOff ? " Overdrive is disabled in AMD Software → Performance → Tuning: turn on manual tuning there first." : string.Empty;
                string level = facts.GpuTuningSwitchedOff ? SupportLevel.Partial : SupportLevel.Experimental;
                rows.Add(("GPU max frequency and memory clock", gpuVendor, level, "Overdrive8 through atiadlxx.dll; every value is checked against the driver's own range and read back. AMD Software must be installed." + off));
                rows.Add(("GPU voltage offset (undervolt)", gpuVendor, limits is { VoltageOffsetSupported: true } ? level : SupportLevel.Unsupported,
                    limits is { VoltageOffsetSupported: true }
                        ? $"Overdrive8 voltage offset ({limits.VoltageOffsetMinMv} to {limits.VoltageOffsetMaxMv} mV): the main and safest Radeon RX 7000/9000 tuning." + off
                        : "This card does not offer a voltage offset through Overdrive8."));
                rows.Add(("GPU minimum clock", gpuVendor, level, "Kept at least 100 MHz below the maximum automatically when the maximum is lowered."));
                rows.Add(("GPU power limit", gpuVendor, level, "Overdrive8 power percentage (relative to the card's default)." + off));
                rows.Add(("GPU temperature limit", gpuVendor, level, "Overdrive8 operating temperature target (most Radeons allow only about 90 °C and up)."));
                break;
            }
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

        bool amdOverdrive8 = facts.GpuVendor == GpuVendor.Amd && (facts.GpuMethod == GpuControlMethod.AmdAdl || Fans.AmdGpuFanRules.IsOverdrive8Card(facts.GpuName));
        if (amdOverdrive8)
        {
            // LibreHardwareMonitor only sends Overdrive5 fan commands, which Radeon RX 5000 and newer ignore.
            bool curve = facts.GpuMethod == GpuControlMethod.AmdAdl && limits is { FanCurve: true };
            rows.Add(("GPU fan speed", gpuVendor, curve ? SupportLevel.Experimental : SupportLevel.Unsupported,
                curve ? "Overdrive8 fan curve: a fixed percentage is sent as a flat curve. The Fans page shows this fan read-only."
                    : "This Radeon ignores the hardware monitor's fan commands; set the fan in AMD Software → Performance → Tuning."));
        }
        else
        {
            rows.Add(("GPU fan speed", gpuVendor,
                facts.FanHostAvailable ? SupportLevel.Full : facts.GpuMethod == GpuControlMethod.NvApi ? SupportLevel.Partial : SupportLevel.Unsupported,
                facts.FanHostAvailable ? "Through the shared hardware monitor; fan curves live on the Fans page."
                    : facts.GpuMethod == GpuControlMethod.NvApi ? "Fixed percentage through NVAPI coolers; the hardware monitor driver is not loaded so curves are unavailable."
                    : "Use the Fans page once the hardware monitor driver is available."));
        }

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
                rows.Add(("CPU power limit", cpuVendor, SupportLevel.Experimental, "Through ryzenadj (sustained limit, short boost 25 % higher). Laptops and APUs only; firmware and maker apps can overwrite it, so it is re-applied while the profile is active."));
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
                        ? (Hardware.CpuTraits.IsRyzenMobileOrApu(facts.CpuName)
                            ? "Place ryzenadj in Program Files\\RyzenAdj to set power limits on this Ryzen laptop/APU, or use the maker's app."
                            : "Use AMD Ryzen Master or the BIOS (Precision Boost Overdrive, Curve Optimizer).")
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
