namespace PcChecker.Core.Overclocking;

/// <summary>A vendor's own tuning program: where it usually lives and where to get it.</summary>
public sealed record VendorSoftware(string Name, string DownloadUrl, IReadOnlyList<string> CandidatePaths, string Purpose)
{
    /// <summary>First candidate that exists on this machine, or null when not installed.</summary>
    public string? Locate(Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        return CandidatePaths.Select(Expand).FirstOrDefault(fileExists);
    }

    public static string Expand(string path) => Environment.ExpandEnvironmentVariables(path);
}

/// <summary>Pure catalog of vendor tuning software for GPUs and CPUs (ADDENDUM §K fallbacks).</summary>
public static class VendorSoftwareCatalog
{
    public static VendorSoftware NvidiaApp { get; } = new("NVIDIA app", "https://www.nvidia.com/en-us/software/nvidia-app/",
        [@"%ProgramFiles%\NVIDIA Corporation\NVIDIA app\CEF\NVIDIA app.exe", @"%ProgramFiles%\NVIDIA Corporation\NVIDIA App\CEF\NVIDIA App.exe", @"%ProgramFiles%\NVIDIA Corporation\NVIDIA GeForce Experience\NVIDIA GeForce Experience.exe"],
        "Automatic tuning, driver updates and per-game settings for GeForce cards.");

    public static VendorSoftware AmdAdrenalin { get; } = new("AMD Software: Adrenalin Edition", "https://www.amd.com/en/support/download/drivers.html",
        [@"%ProgramFiles%\AMD\CNext\CNext\RadeonSoftware.exe", @"%ProgramFiles%\AMD\CNext\CNext\AMDRSSrcExt.exe"],
        "Performance → Tuning: clocks, power, fan curves and undervolting for Radeon cards.");

    public static VendorSoftware IntelGraphicsSoftware { get; } = new("Intel Graphics Software", "https://www.intel.com/content/www/us/en/products/docs/discrete-gpus/arc/software/graphics-software.html",
        [@"%ProgramFiles%\Intel\Intel Graphics Software\IntelGraphicsSoftware.exe", @"%ProgramFiles%\Intel\Intel(R) Arc(TM) Control\ArcControl.exe"],
        "Performance tuning, fan control and driver updates for Arc cards.");

    public static VendorSoftware IntelXtu { get; } = new("Intel Extreme Tuning Utility (XTU)", "https://www.intel.com/content/www/us/en/download/17881/intel-extreme-tuning-utility-intel-xtu.html",
        [@"%ProgramFiles%\Intel\Intel(R) Extreme Tuning Utility\Client\XtuCli.exe", @"%ProgramFiles%\Intel\Intel(R) Extreme Tuning Utility\Client\XTUCLI.exe", @"%ProgramFiles%\Intel\Intel(R) Extreme Tuning Utility\Client\XtuClient.exe", @"%ProgramFiles%\Intel\Intel(R) Extreme Tuning Utility\XTUCLI.exe"],
        "Core ratios, voltage offsets and power limits for unlocked Intel CPUs.");

    public static VendorSoftware AmdRyzenMaster { get; } = new("AMD Ryzen Master", "https://www.amd.com/en/products/software/ryzen-master.html",
        [@"%ProgramFiles%\AMD\RyzenMaster\bin\AMDRyzenMaster.exe", @"%ProgramFiles%\AMD\RyzenMaster\bin\AMDRyzenMasterCLI.exe"],
        "Precision Boost Overdrive, Curve Optimizer and memory tuning for Ryzen CPUs.");

    public static VendorSoftware RyzenAdj { get; } = new("ryzenadj", "https://github.com/FlyGoat/RyzenAdj/releases",
        [@"tools\ryzenadj.exe", @"ryzenadj.exe"],
        "Open-source power limit tool for Ryzen laptops and APUs.");

    public static VendorSoftware ForGpu(GpuVendor vendor) => vendor switch
    {
        GpuVendor.Amd => AmdAdrenalin,
        GpuVendor.Intel => IntelGraphicsSoftware,
        _ => NvidiaApp,
    };

    public static VendorSoftware? ForCpu(string? cpuVendorOrName)
    {
        if (string.IsNullOrWhiteSpace(cpuVendorOrName))
        {
            return null;
        }

        if (cpuVendorOrName.Contains("Intel", StringComparison.OrdinalIgnoreCase))
        {
            return IntelXtu;
        }

        return cpuVendorOrName.Contains("AMD", StringComparison.OrdinalIgnoreCase) || cpuVendorOrName.Contains("Ryzen", StringComparison.OrdinalIgnoreCase) ? AmdRyzenMaster : null;
    }

    /// <summary>Normalises Win32_Processor.Manufacturer ("GenuineIntel", "AuthenticAMD") to a display vendor.</summary>
    public static string CpuVendorName(string? manufacturerOrName)
    {
        if (string.IsNullOrWhiteSpace(manufacturerOrName))
        {
            return "Unknown";
        }

        if (manufacturerOrName.Contains("Intel", StringComparison.OrdinalIgnoreCase))
        {
            return "Intel";
        }

        return manufacturerOrName.Contains("AMD", StringComparison.OrdinalIgnoreCase) || manufacturerOrName.Contains("Ryzen", StringComparison.OrdinalIgnoreCase) ? "AMD" : "Unknown";
    }

    /// <summary>Plain-English BIOS steps for the CPU vendor, used when no tuning tool is installed.</summary>
    public static IReadOnlyList<string> BiosSteps(string cpuVendor)
    {
        bool amd = cpuVendor.Contains("AMD", StringComparison.OrdinalIgnoreCase);
        return
        [
            "Restart and press the BIOS key while the logo shows (usually Delete or F2).",
            amd ? "Open the Ai Tweaker / OC Tweaker / Advanced CPU page and enable Precision Boost Overdrive (PBO)." : "Open the Ai Tweaker / OC / Advanced CPU page.",
            amd ? "Use Curve Optimizer with small negative values (−10 to −20) for lower temperatures at the same speed." : "Raise the all-core ratio by one step (for example 53 → 54) and keep the voltage on Auto or add a small negative offset.",
            "Enable XMP / EXPO on the memory page so the RAM runs at its rated speed.",
            "Save and exit, then run the stress test on this page for at least 10 minutes and watch the temperatures.",
        ];
    }
}
