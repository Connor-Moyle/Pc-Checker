using System.Globalization;
using PcChecker.Core.Displays;
using PcChecker.Core.Hardware;
using PcChecker.Core.Localization;
using PcChecker.Core.Storage;

namespace PcChecker.Core.Upgrades;

/// <summary>The facts the advisor's sections share, worked out once, plus the items they add.</summary>
internal sealed class AdviceContext
{
    public AdviceContext(UpgradeFacts facts, Translator tr)
    {
        Facts = facts;
        Tr = tr;
        var hw = facts.Hardware;
        IsLaptop = UpgradeAdvisor.IsLaptop(hw);
        CpuInfo = hw?.Processors.FirstOrDefault();
        Cpu = CpuUpgradePaths.Identify(CpuInfo?.Name, CpuInfo?.Socket);
        if (IsLaptop && !Cpu.IsLaptop)
        {
            Cpu = Cpu with { IsLaptop = true, Socket = null };
        }

        Gpu = hw is null ? null : HardwareSummary.PrimaryGpu(hw);
        GpuClass = GpuRules.Classify(Gpu?.Name, Gpu?.IsIntegrated == true);
        long? registryVram = long.TryParse(Gpu?.Get("Registry.qwMemorySize"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var q) ? q : null;
        VramGb = Gpu?.IsIntegrated == true ? null : GpuRules.VramGb(HardwareMappings.ResolveVramBytes(Gpu?.AdapterRamBytes, registryVram));
        Screen = MainScreen(facts.Displays, Gpu);

        var modules = hw?.MemoryModules ?? [];
        TotalMemoryBytes = hw?.TotalMemoryBytes;
        MemoryType = modules.Select(m => m.MemoryType).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
        Slots = hw is null ? null : HardwareSummary.MemorySlots(hw);
        Layout = MemoryLayoutRules.Evaluate(modules, Slots, hw?.Motherboard?.Manufacturer, IsLaptop);
        Pressure = MemoryLayoutRules.EvaluatePressure(facts.History);

        SystemDrive = FindSystemDrive(facts);
        Balance = GamingBalanceRules.Analyze(facts.History);
        Psu = PsuEstimator.Estimate(Cpu, GpuClass);
        Windows11 = BuildWindows11(facts, Cpu, TotalMemoryBytes, SystemDrive, tr);
    }

    public UpgradeFacts Facts { get; }

    public Translator Tr { get; }

    public bool IsLaptop { get; }

    public CpuInfo? CpuInfo { get; }

    public CpuIdentity Cpu { get; }

    public GpuInfo? Gpu { get; }

    public GpuClass GpuClass { get; }

    public int? VramGb { get; }

    public DisplayMode? Screen { get; }

    public long? TotalMemoryBytes { get; }

    public string? MemoryType { get; }

    public int? Slots { get; }

    public MemoryLayout Layout { get; }

    public MemoryPressure Pressure { get; }

    public PhysicalDrive? SystemDrive { get; }

    public BoundAnalysis Balance { get; }

    public PsuEstimate Psu { get; }

    public Windows11Readiness Windows11 { get; }

    public List<FixFirstItem> Fixes { get; } = [];

    public List<UpgradeSuggestion> Upgrades { get; } = [];

    /// <summary>The year of <see cref="UpgradeFacts.Now"/>, for the graphics card's age.</summary>
    public int Year => Facts.Now.Year;

    /// <summary>The primary display's current mode, else the graphics card's current resolution.</summary>
    private static DisplayMode? MainScreen(IReadOnlyList<DisplayInfo> displays, GpuInfo? gpu)
    {
        var primary = displays.FirstOrDefault(d => d.IsPrimary) ?? displays.FirstOrDefault();
        if (primary is not null && primary.Current.Width > 0)
        {
            return primary.Current;
        }

        return gpu is { CurrentHorizontalResolution: > 0, CurrentVerticalResolution: > 0 }
            ? new DisplayMode(gpu.CurrentHorizontalResolution.Value, gpu.CurrentVerticalResolution.Value, gpu.CurrentRefreshRate ?? 60, 32)
            : null;
    }

    private static PhysicalDrive? FindSystemDrive(UpgradeFacts facts)
    {
        var drives = facts.Drives;
        return drives.FirstOrDefault(d => d.IsSystemDisk)
            ?? drives.FirstOrDefault(d => d.Volumes.Any(v => string.Equals(StorageCodes.NormalizeDriveLetter(v), "C:", StringComparison.OrdinalIgnoreCase)))
            ?? drives.FirstOrDefault(d => d.IsBootDisk);
    }

    private static Windows11Readiness BuildWindows11(UpgradeFacts facts, CpuIdentity cpu, long? memory, PhysicalDrive? system, Translator tr)
    {
        var security = facts.Security;
        int? build = security?.Windows?.Build is > 0 ? security.Windows.Build : null;
        if (build is null && int.TryParse(facts.Hardware?.OperatingSystem?.BuildNumber, NumberStyles.Integer, CultureInfo.InvariantCulture, out var b))
        {
            build = b;
        }

        bool legacy = security?.LegacyBios ?? facts.Hardware?.Bios?.IsUefi == false;
        bool? secureBoot = security?.SecureBoot ?? facts.Hardware?.Bios?.SecureBootEnabled;
        var inputs = new Windows11Inputs(build, security?.Tpm, secureBoot, legacy, CpuUpgradePaths.IsWindows11Supported(cpu), memory,
            system?.SizeBytes is > 0 ? system.SizeBytes : null);
        return Windows11Rules.Evaluate(inputs, tr);
    }
}
