namespace PcChecker.Core.Overclocking;

/// <summary>Which CPU tuning tool is available and where.</summary>
public sealed record CpuToolDetection(CpuControlMethod Method, string? ToolPath, string Note);

/// <summary>
/// Finds Intel XTU, AMD Ryzen Master or ryzenadj. Pure apart from the injected file-exists probe so it is testable.
/// </summary>
public static class CpuToolLocator
{
    public static CpuToolDetection Detect(string cpuVendor, Func<string, bool> fileExists, string? appDirectory = null, string? pathVariable = null)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        bool intel = cpuVendor.Contains("Intel", StringComparison.OrdinalIgnoreCase);
        bool amd = cpuVendor.Contains("AMD", StringComparison.OrdinalIgnoreCase);

        if (intel)
        {
            var xtu = VendorSoftwareCatalog.IntelXtu.Locate(fileExists);
            return xtu is null
                ? new CpuToolDetection(CpuControlMethod.None, null, "Intel Extreme Tuning Utility is not installed. Install it to tune ratios and voltages from here, or use the BIOS.")
                : new CpuToolDetection(CpuControlMethod.IntelXtuCli, xtu, "Intel XTU command line found; ratio, voltage offset and power limit can be set from here.");
        }

        if (amd)
        {
            var adj = LocateRyzenAdj(fileExists, appDirectory, pathVariable);
            if (adj is not null)
            {
                return new CpuToolDetection(CpuControlMethod.RyzenAdj, adj, "ryzenadj found; power limits can be set from here (laptops/APUs). Ratios and voltages stay in the BIOS.");
            }

            var master = VendorSoftwareCatalog.AmdRyzenMaster.Locate(fileExists);
            return master is null
                ? new CpuToolDetection(CpuControlMethod.None, null, "AMD Ryzen Master is not installed. Install it for Precision Boost Overdrive and Curve Optimizer, or use the BIOS.")
                : new CpuToolDetection(CpuControlMethod.RyzenMaster, master, "AMD Ryzen Master is installed; PC Checker opens it for CPU tuning because AMD does not publish a tuning API.");
        }

        return new CpuToolDetection(CpuControlMethod.None, null, "CPU vendor not recognised; tune the CPU in the BIOS.");
    }

    /// <summary>Looks for ryzenadj.exe in <c>tools\</c> next to the app and then on PATH.</summary>
    public static string? LocateRyzenAdj(Func<string, bool> fileExists, string? appDirectory, string? pathVariable)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        if (!string.IsNullOrEmpty(appDirectory))
        {
            foreach (var candidate in new[] { Path.Combine(appDirectory, "tools", "ryzenadj.exe"), Path.Combine(appDirectory, "ryzenadj.exe") })
            {
                if (fileExists(candidate))
                {
                    return candidate;
                }
            }
        }

        foreach (var dir in (pathVariable ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(dir, "ryzenadj.exe");
            if (fileExists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Builds the ryzenadj arguments for a power limit (ryzenadj takes milliwatts).</summary>
    public static string RyzenAdjArguments(int powerLimitW, int? tctlTempC = null)
    {
        int mw = Math.Clamp(powerLimitW, 5, 400) * 1000;
        var args = $"--stapm-limit={mw} --fast-limit={mw} --slow-limit={mw}";
        if (tctlTempC is { } t)
        {
            args += $" --tctl-temp={Math.Clamp(t, 50, 105)}";
        }

        return args;
    }
}
