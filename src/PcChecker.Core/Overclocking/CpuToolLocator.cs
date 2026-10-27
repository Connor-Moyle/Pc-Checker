namespace PcChecker.Core.Overclocking;

/// <summary>Which CPU tuning tool is available and where.</summary>
public sealed record CpuToolDetection(CpuControlMethod Method, string? ToolPath, string Note);

/// <summary>
/// Finds Intel XTU, AMD Ryzen Master or ryzenadj. Pure apart from the injected file-exists probe so it is testable.
/// </summary>
public static class CpuToolLocator
{
    /// <param name="trustedRoots">
    /// Folders only administrators can write to (Program Files). PC Checker runs elevated, so ryzenadj is only taken
    /// from inside these; PATH and user-writable folders are never searched.
    /// </param>
    public static CpuToolDetection Detect(string cpuVendor, Func<string, bool> fileExists, string? appDirectory = null, IReadOnlyList<string>? trustedRoots = null)
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
            var adj = LocateRyzenAdj(fileExists, appDirectory, trustedRoots ?? []);
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

    /// <summary>
    /// Looks for ryzenadj.exe in admin-only folders: <c>RyzenAdj\</c> or <c>PC Checker\tools\</c> under a trusted root, and
    /// <c>tools\</c> next to the app only when the app itself lives under a trusted root. The elevated app would run
    /// whatever it finds, so a folder a normal user can write to (PATH entries, Downloads, the desktop) is never used.
    /// </summary>
    public static string? LocateRyzenAdj(Func<string, bool> fileExists, string? appDirectory, IReadOnlyList<string> trustedRoots)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(trustedRoots);
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(appDirectory) && IsUnderTrustedRoot(appDirectory, trustedRoots))
        {
            candidates.Add(Path.Combine(appDirectory, "tools", "ryzenadj.exe"));
            candidates.Add(Path.Combine(appDirectory, "ryzenadj.exe"));
        }

        foreach (var root in trustedRoots.Where(r => !string.IsNullOrWhiteSpace(r)))
        {
            candidates.Add(Path.Combine(root, "RyzenAdj", "ryzenadj.exe"));
            candidates.Add(Path.Combine(root, "PC Checker", "tools", "ryzenadj.exe"));
        }

        return candidates.FirstOrDefault(fileExists);
    }

    /// <summary>True when <paramref name="directory"/> is one of <paramref name="trustedRoots"/> or inside one (after resolving "..").</summary>
    public static bool IsUnderTrustedRoot(string directory, IReadOnlyList<string> trustedRoots)
    {
        ArgumentNullException.ThrowIfNull(trustedRoots);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        string dir = Normalize(directory);
        return trustedRoots.Where(r => !string.IsNullOrWhiteSpace(r)).Any(root =>
        {
            string r = Normalize(root);
            return dir.Length > r.Length
                ? dir.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                : dir.Equals(r, StringComparison.OrdinalIgnoreCase);
        });

        static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
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
