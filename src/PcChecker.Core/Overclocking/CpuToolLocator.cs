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
    /// <param name="cpuName">
    /// The processor name. ryzenadj only supports Ryzen laptop and APU firmware (Raven … Strix Point, Van Gogh); desktop
    /// Ryzen and Dragon Range HX fail with "CPU family not supported", so ryzenadj is only used for those parts and
    /// Ryzen Master is offered otherwise. Unknown names keep the old behaviour.
    /// </param>
    /// <param name="ryzenMasterInstallDirectory">Ryzen Master's folder from its uninstall entry, when known.</param>
    public static CpuToolDetection Detect(string cpuVendor, Func<string, bool> fileExists, string? appDirectory = null, IReadOnlyList<string>? trustedRoots = null,
        string? cpuName = null, string? ryzenMasterInstallDirectory = null)
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
            var traits = Hardware.CpuTraits.Parse(cpuName);
            bool ryzenAdjCapable = string.IsNullOrWhiteSpace(cpuName) || traits.IsRyzenMobileOrApu;
            var adj = ryzenAdjCapable ? LocateRyzenAdj(fileExists, appDirectory, trustedRoots ?? []) : null;
            if (adj is not null)
            {
                return new CpuToolDetection(CpuControlMethod.RyzenAdj, adj, "ryzenadj found; power limits can be set from here (laptops/APUs). Ratios and voltages stay in the BIOS. "
                    + RyzenAdjDriverNote(adj, fileExists));
            }

            var master = LocateRyzenMaster(fileExists, ryzenMasterInstallDirectory, trustedRoots ?? []);
            if (master is null)
            {
                return new CpuToolDetection(CpuControlMethod.None, null, traits.IsRyzenMobileOrApu
                    ? "No CPU tool found. Ryzen Master does not support most laptops and APUs; place ryzenadj in Program Files\\RyzenAdj to set power limits from here."
                    : "AMD Ryzen Master is not installed. Install it for Precision Boost Overdrive and Curve Optimizer, or use the BIOS.");
            }

            string caveat = traits.IsRyzenMobileOrApu ? " Ryzen Master does not support most laptops and APUs; ryzenadj is the tool for those." : string.Empty;
            return new CpuToolDetection(CpuControlMethod.RyzenMaster, master, "AMD Ryzen Master is installed; PC Checker opens it for CPU tuning because AMD does not publish a tuning API." + caveat);
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

    /// <summary>
    /// Builds the ryzenadj arguments for a sustained power limit (ryzenadj takes milliwatts). STAPM and the slow limit
    /// get the sustained value; the fast (short boost) limit is 25 % higher, so short bursts still boost.
    /// </summary>
    public static string RyzenAdjArguments(int powerLimitW, int? tctlTempC = null)
    {
        int sustained = Math.Clamp(powerLimitW, 5, 400);
        int mw = sustained * 1000;
        int fastMw = FastLimitW(sustained) * 1000;
        var args = $"--stapm-limit={mw} --fast-limit={fastMw} --slow-limit={mw}";
        if (tctlTempC is { } t)
        {
            args += $" --tctl-temp={Math.Clamp(t, 50, 105)}";
        }

        return args;
    }

    /// <summary>The short-boost (fast) limit for a sustained limit: 25 % higher, never lower than the sustained value.</summary>
    public static int FastLimitW(int sustainedW) => Math.Clamp((int)Math.Round(sustainedW * 1.25, MidpointRounding.AwayFromZero), sustainedW, 400);

    /// <summary>
    /// Reads ryzenadj's output as well as its exit code: every value it applied prints "Sucessfully set" (ryzenadj's
    /// own spelling); a value the firmware refused prints "Failed to set", "... is rejected by SMU" or "... timeout";
    /// a driver that could not load prints "unable to init". Success needs at least one confirmed value, no refused
    /// value and exit code 0 (ryzenadj returns its error count). Returns success and a message.
    /// </summary>
    public static (bool Success, string Message) ParseRyzenAdjOutput(string? stdOut, string? stdErr, int exitCode)
    {
        var text = ((stdOut ?? string.Empty) + "\n" + (stdErr ?? string.Empty)).Trim();
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int set = lines.Count(l => l.Contains("Sucessfully set", StringComparison.OrdinalIgnoreCase) || l.Contains("Successfully set", StringComparison.OrdinalIgnoreCase));
        var failed = lines.Where(l => l.Contains("Failed to set", StringComparison.OrdinalIgnoreCase) || l.Contains("not supported", StringComparison.OrdinalIgnoreCase)
            || l.Contains("Unable to", StringComparison.OrdinalIgnoreCase) || l.Contains("rejected by SMU", StringComparison.OrdinalIgnoreCase)
            || l.EndsWith("timeout", StringComparison.OrdinalIgnoreCase) || l.Contains(" timeout ", StringComparison.OrdinalIgnoreCase)).ToList();

        if (text.Contains("unable to init", StringComparison.OrdinalIgnoreCase) || text.Contains("WinRing0", StringComparison.OrdinalIgnoreCase))
        {
            return (false, WinRing0BlockedMessage);
        }

        if (text.Contains("CPU family not supported", StringComparison.OrdinalIgnoreCase) || text.Contains("not supported by this", StringComparison.OrdinalIgnoreCase))
        {
            return (false, "ryzenadj does not support this CPU (it is made for Ryzen laptops and APUs). Use Ryzen Master or the BIOS.");
        }

        if (failed.Count > 0)
        {
            return (false, "ryzenadj could not set every limit: " + string.Join("; ", failed.Take(3)));
        }

        if (set == 0)
        {
            return (false, exitCode == 0 ? "ryzenadj ran but did not confirm any change." : $"ryzenadj failed (exit code {exitCode}).");
        }

        if (exitCode != 0)
        {
            return (false, $"ryzenadj set {set} limit(s) but reported an error (exit code {exitCode}), so not every limit may be applied.");
        }

        return (true, $"ryzenadj confirmed {set} limit(s).");
    }

    /// <summary>Plain-English explanation when Windows blocked ryzenadj's WinRing0 driver.</summary>
    public const string WinRing0BlockedMessage =
        "ryzenadj could not load its WinRing0 driver. Windows 11 blocks that driver (Microsoft vulnerable driver blocklist, Memory Integrity, Defender \"HackTool:Win32/Winring0\"). "
        + "PC Checker does not turn those protections off. Use your laptop maker's performance modes or AMD's own tools instead.";

    /// <summary>A warning when WinRing0x64.sys is not next to ryzenadj.exe (it cannot work without it), else empty.</summary>
    public static string RyzenAdjDriverNote(string ryzenAdjPath, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        var folder = Path.GetDirectoryName(ryzenAdjPath);
        if (string.IsNullOrEmpty(folder))
        {
            return string.Empty;
        }

        return fileExists(Path.Combine(folder, "WinRing0x64.sys"))
            ? "Windows 11 may still block its WinRing0 driver; PC Checker reports that clearly if it happens."
            : "WinRing0x64.sys is missing next to ryzenadj.exe, so it will not be able to set limits.";
    }

    /// <summary>
    /// Ryzen Master's exe: the uninstall entry's folder first, then the default install folders. The uninstall entry's
    /// folder is used only inside <paramref name="trustedRoots"/> (Program Files), the same rule as ryzenadj, because the
    /// elevated app launches what it finds.
    /// </summary>
    public static string? LocateRyzenMaster(Func<string, bool> fileExists, string? installDirectory, IReadOnlyList<string> trustedRoots)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(trustedRoots);
        if (!string.IsNullOrWhiteSpace(installDirectory) && IsUnderTrustedRoot(installDirectory, trustedRoots))
        {
            foreach (var name in new[] { "AMD Ryzen Master.exe", "AMDRyzenMaster.exe", Path.Combine("bin", "AMD Ryzen Master.exe"), Path.Combine("bin", "AMDRyzenMaster.exe") })
            {
                var candidate = Path.Combine(installDirectory, name);
                if (fileExists(candidate))
                {
                    return candidate;
                }
            }
        }

        return VendorSoftwareCatalog.AmdRyzenMaster.Locate(fileExists);
    }

    /// <summary>The install folder from an uninstall entry: InstallLocation, else the folder of DisplayIcon ("path,0").</summary>
    public static string? InstallFolderFromUninstall(string? installLocation, string? displayIcon)
    {
        if (!string.IsNullOrWhiteSpace(installLocation))
        {
            return installLocation.Trim().Trim('"');
        }

        if (string.IsNullOrWhiteSpace(displayIcon))
        {
            return null;
        }

        var icon = displayIcon.Trim().Trim('"');
        int comma = icon.LastIndexOf(',');
        if (comma > 0 && icon[(comma + 1)..].Trim().All(c => char.IsDigit(c) || c == '-'))
        {
            icon = icon[..comma].Trim().Trim('"');
        }

        return Path.GetDirectoryName(icon);
    }

    /// <summary>
    /// What to tell the user before opening Ryzen Master: it does not support most laptops/APUs, and it refuses to run
    /// while Virtualization-Based Security is on. Null when there is nothing to say.
    /// </summary>
    public static string? RyzenMasterCaveat(string? cpuName, bool? vbsRunning)
    {
        var parts = new List<string>();
        if (Hardware.CpuTraits.IsRyzenMobileOrApu(cpuName))
        {
            parts.Add("Ryzen Master does not support most laptops and APUs; ryzenadj is the tool for those.");
        }

        if (vbsRunning == true)
        {
            parts.Add("Virtualization-Based Security (Core isolation / Memory Integrity) is on, and Ryzen Master may refuse to start until it is turned off. PC Checker does not change this for you.");
        }

        return parts.Count == 0 ? null : string.Join(" ", parts);
    }
}
