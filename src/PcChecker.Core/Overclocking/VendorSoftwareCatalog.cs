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
        [@"%ProgramFiles%\AMD\RyzenMaster\bin\AMDRyzenMaster.exe", @"%ProgramFiles%\AMD\RyzenMaster\bin\AMD Ryzen Master.exe", @"%ProgramFiles%\AMD\RyzenMaster\AMD Ryzen Master.exe"],
        "Precision Boost Overdrive, Curve Optimizer and memory tuning for Ryzen CPUs.");

    public static VendorSoftware RyzenAdj { get; } = new("ryzenadj", "https://github.com/FlyGoat/RyzenAdj/releases",
        [@"tools\ryzenadj.exe", @"ryzenadj.exe"],
        "Open-source power limit tool for Ryzen laptops and APUs.");

    /// <summary>Folders AMD Chipset Software installs to.</summary>
    public static IReadOnlyList<string> AmdChipsetSoftwareFolders { get; } =
        [@"%ProgramFiles(x86)%\AMD\Chipset_Software", @"%ProgramFiles%\AMD\Chipset_Software"];

    /// <summary>The NVIDIA Control Panel's folder (DCH drivers without the NVIDIA app still install it).</summary>
    public static IReadOnlyList<string> NvidiaControlPanelFolders { get; } =
        [@"%ProgramFiles%\NVIDIA Corporation\Control Panel Client"];

    /// <summary>True when AMD Chipset Software is installed (it manages AMD's System-class drivers).</summary>
    public static bool AmdChipsetSoftwareInstalled(Func<string, bool> directoryExists)
    {
        ArgumentNullException.ThrowIfNull(directoryExists);
        return AmdChipsetSoftwareFolders.Select(VendorSoftware.Expand).Any(directoryExists);
    }

    /// <summary>
    /// True when NVIDIA's driver tools are installed: the NVIDIA app (or GeForce Experience) or the NVIDIA Control
    /// Panel. One rule for every page, so Debloat and Drivers agree.
    /// </summary>
    public static bool NvidiaDriverToolsInstalled(Func<string, bool> fileExists, Func<string, bool> directoryExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(directoryExists);
        return NvidiaApp.Locate(fileExists) is not null || NvidiaControlPanelFolders.Select(VendorSoftware.Expand).Any(directoryExists);
    }

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

    /// <summary>
    /// Plain-English BIOS steps for the CPU, used when no tuning tool is installed. On AMD the steps follow the chip:
    /// Ryzen 5000X3D are locked (no PBO / Curve Optimizer), 7000X3D allow PBO and Curve Optimizer but no multiplier,
    /// laptops have no BIOS tuning, and the menu names of the main board makers are listed.
    /// </summary>
    public static IReadOnlyList<string> BiosSteps(string cpuVendor, string? cpuName = null)
    {
        bool amd = cpuVendor.Contains("AMD", StringComparison.OrdinalIgnoreCase) || Hardware.CpuTraits.IsAmd(cpuName);
        if (!amd)
        {
            return
            [
                "Restart and press the BIOS key while the logo shows (usually Delete or F2).",
                "Open the Ai Tweaker / OC / Advanced CPU page.",
                "Raise the all-core ratio by one step (for example 53 → 54) and keep the voltage on Auto or add a small negative offset.",
                "Enable XMP on the memory page so the RAM runs at its rated speed.",
                "Save and exit, then run the stress test on this page for at least 10 minutes and watch the temperatures.",
            ];
        }

        var traits = Hardware.CpuTraits.Parse(cpuName);
        if (traits.FormFactor is Hardware.CpuFormFactor.Laptop or Hardware.CpuFormFactor.Handheld)
        {
            return
            [
                "Ryzen laptops and handhelds have no CPU tuning in the BIOS. Use the maker's performance modes (for example Armoury Crate or Lenovo Vantage).",
                "For power limits, ryzenadj (placed in Program Files\\RyzenAdj) lets this page set a sustained limit.",
                "Lower limits mean a cooler, quieter laptop with longer battery life; higher limits only help while plugged in and cool.",
            ];
        }

        var steps = new List<string>
        {
            "Restart and press the BIOS key while the logo shows (usually Delete or F2).",
            "Find the AMD tuning menu: Advanced → AMD Overclocking (ASRock, MSI), Ai Tweaker → Precision Boost Overdrive (ASUS) or Tweaker → Advanced CPU Settings (Gigabyte).",
        };

        steps.AddRange(traits.X3dTuning switch
        {
            Hardware.X3dTuning.Locked =>
            [
                "Ryzen 5000X3D chips are locked: most boards offer no PBO and no Curve Optimizer for them. Leave the CPU at stock and tune memory instead.",
            ],
            Hardware.X3dTuning.PboAndCurveOptimizerOnly =>
            [
                "Ryzen 7000X3D chips allow PBO and Curve Optimizer but not multiplier changes. Enable PBO (Advanced) and set Curve Optimizer to All Cores, Negative, 10–20.",
            ],
            _ =>
            [
                "Enable Precision Boost Overdrive (PBO). PBO raises power limits, so the CPU runs hotter.",
                "Use Curve Optimizer: All Cores, Negative, 10 to 20. It lowers the voltage for each clock, so the CPU runs cooler at the same speed.",
            ],
        });

        if (traits.BoostsToTjMax)
        {
            steps.Add("Ryzen 7000/9000 boost until about 95 °C under heavy load. That is normal and by design, not overheating.");
        }

        steps.Add("Enable EXPO (or XMP / DOCP) on the memory page so the RAM runs at its rated speed. The first start after that can take 1–3 minutes on AM5: do not switch off.");
        steps.Add("Save and exit, then run the stress test on this page for 10 minutes, a per-core test (CoreCycler or y-cruncher single-threaded) for Curve Optimizer, and check the Health page for WHEA errors.");
        return steps;
    }
}
