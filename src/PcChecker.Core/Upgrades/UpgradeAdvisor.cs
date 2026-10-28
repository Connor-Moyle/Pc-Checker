using System.Globalization;
using PcChecker.Core.Common;
using PcChecker.Core.Hardware;
using PcChecker.Core.Localization;
using PcChecker.Core.Storage;

namespace PcChecker.Core.Upgrades;

/// <summary>
/// Turns <see cref="UpgradeFacts"/> into the Upgrade advice page: the PC at a glance, the free fixes and the ranked
/// upgrades. Pure: everything it needs is in the facts, and every sentence goes through the translator.
/// </summary>
public static partial class UpgradeAdvisor
{
    /// <summary>At most this many upgrades are listed.</summary>
    public const int MaxUpgrades = 6;

    /// <summary>Builds the whole report.</summary>
    public static UpgradeReport Build(UpgradeFacts facts, Translator tr)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(tr);
        var ctx = new AdviceContext(facts, tr);

        AddMemoryAdvice(ctx);
        AddStorageAdvice(ctx);
        AddProcessorAdvice(ctx);
        AddGraphicsAdvice(ctx);
        AddWindowsAdvice(ctx);

        var notes = new List<string>
        {
            tr.T("Prices are left out on purpose: they change daily and differ by country. Compare current prices before you buy."),
            tr.T("Expected impact is described in words. Real gains depend on the game, its settings and your screen."),
        };
        if (ctx.IsLaptop)
        {
            notes.Insert(0, tr.T("This is a laptop: the processor and graphics are soldered in, so only memory (when it has slots; check the manual) and storage can usually be upgraded."));
        }

        var fixes = ctx.Fixes.OrderByDescending(f => f.Severity).ToList();
        var upgrades = Rank(ctx.Upgrades);
        return new UpgradeReport(facts.Now, ctx.IsLaptop, BuildGlance(ctx), fixes, upgrades, ctx.Balance,
            ctx.IsLaptop ? null : ctx.Psu, ctx.Windows11, notes, facts.Warnings);
    }

    /// <summary>Highest score first, at most <see cref="MaxUpgrades"/>, without duplicate ids.</summary>
    public static IReadOnlyList<UpgradeSuggestion> Rank(IEnumerable<UpgradeSuggestion> upgrades)
    {
        ArgumentNullException.ThrowIfNull(upgrades);
        return upgrades
            .GroupBy(u => u.Id, StringComparer.Ordinal)
            .Select(g => g.MaxBy(u => u.Score)!)
            .Where(u => u.Score > 0)
            .OrderByDescending(u => u.Score)
            .ThenBy(u => u.Area)
            .Take(MaxUpgrades)
            .ToList();
    }

    /// <summary>Impact in words from a score: 70 and up big, 40 and up noticeable, otherwise small.</summary>
    public static UpgradeImpact ImpactFor(int score) => score >= 70 ? UpgradeImpact.Big : score >= 40 ? UpgradeImpact.Noticeable : UpgradeImpact.Small;

    /// <summary>"Big difference", "Noticeable difference" or "Small difference".</summary>
    public static string ImpactLabel(UpgradeImpact impact, Translator tr)
    {
        ArgumentNullException.ThrowIfNull(tr);
        return impact switch
        {
            UpgradeImpact.Big => tr.T("Big difference"),
            UpgradeImpact.Noticeable => tr.T("Noticeable difference"),
            _ => tr.T("Small difference"),
        };
    }

    /// <summary>True for laptops and other portable PCs (PC type "Mobile", a portable chassis, a laptop CPU or a battery).</summary>
    public static bool IsLaptop(HardwareInventory? hardware)
    {
        if (hardware is null)
        {
            return false;
        }

        var system = hardware.System;
        if (system?.PcSystemType is { } type && type.Contains("Mobile", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string chassis = system?.ChassisType ?? string.Empty;
        if (new[] { "Laptop", "Notebook", "Portable", "Sub notebook", "Convertible", "Detachable", "Tablet", "Hand held" }
            .Any(c => chassis.Equals(c, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var cpu = hardware.Processors.FirstOrDefault();
        return cpu is not null && CpuUpgradePaths.Identify(cpu.Name).IsLaptop;
    }

    private static IReadOnlyList<GlanceItem> BuildGlance(AdviceContext ctx)
    {
        var tr = ctx.Tr;
        var hw = ctx.Facts.Hardware;
        var items = new List<GlanceItem>();
        var cpu = hw?.Processors.FirstOrDefault();
        items.Add(new GlanceItem("cpu", UpgradeArea.Processor, tr.T("Processor"), cpu?.Name ?? tr.T("Unknown"),
            Join(ctx.Cpu.Socket, cpu?.Cores is { } cores ? tr.F("{0} cores / {1} threads", cores, cpu.LogicalProcessors ?? cores) : null)));

        string? gpuDetail = Join(ctx.VramGb is { } vram ? tr.F("{0} GB", vram) : null,
            ctx.GpuClass.ReleaseYear is null ? null : ctx.GpuClass.Generation,
            GpuRules.DescribeLink(ctx.Facts.GpuLink?.CurrentGen, ctx.Facts.GpuLink?.CurrentWidth));
        items.Add(new GlanceItem("gpu", UpgradeArea.Graphics, tr.T("Graphics"), ctx.Gpu?.Name ?? tr.T("Unknown"), gpuDetail));

        string memValue = ctx.TotalMemoryBytes is { } total ? Join(ByteSize.Format(total), ctx.MemoryType) ?? ByteSize.Format(total) : tr.T("Unknown");
        string? channel = ctx.Layout.Mode switch
        {
            ChannelMode.DualChannel => tr.T("dual channel"),
            ChannelMode.SingleChannel => tr.T("single channel"),
            _ => null,
        };
        int? speed = hw?.MemoryModules.Select(m => m.ConfiguredClockSpeedMhz ?? m.SpeedMhz).Where(s => s is > 0).Min();
        string? sticks = ctx.Layout.ModuleCount > 0 && hw is not null && hw.MemoryModules.FirstOrDefault()?.CapacityBytes is { } size
            ? ctx.Layout.ModuleCount + " x " + ByteSize.Format(size) : null;
        items.Add(new GlanceItem("memory", UpgradeArea.Memory, tr.T("Memory"), memValue,
            Join(sticks, speed is { } mts ? mts.ToString(CultureInfo.InvariantCulture) + " MT/s" : null, channel,
                ctx.Layout.CurrentSlots.Count > 0 && ctx.Layout.CurrentSlots.All(s => s.Length <= 3) ? tr.F("slots {0}", string.Join(", ", ctx.Layout.CurrentSlots)) : null)));

        var system = ctx.SystemDrive;
        int others = ctx.Facts.Drives.Count(d => d.Role != DriveRole.PoolMember && !ReferenceEquals(d, system));
        items.Add(new GlanceItem("storage", UpgradeArea.Storage, tr.T("Storage"), system?.Model ?? tr.T("Unknown"),
            Join(system is null ? null : DriveKind(system, tr), others > 0 ? tr.F("+ {0} more drives", others) : null)));

        var board = hw?.Motherboard;
        items.Add(new GlanceItem("board", UpgradeArea.Processor, tr.T("Motherboard"),
            board is null ? tr.T("Unknown") : string.Join(" ", new[] { Maker(board.Manufacturer), board.Product }.Where(p => !string.IsNullOrWhiteSpace(p))) is { Length: > 0 } name ? name : tr.T("Unknown"),
            board?.Chipset));

        if (ctx.IsLaptop)
        {
            items.Add(new GlanceItem("psu", UpgradeArea.PowerSupply, tr.T("Power supply"), tr.T("Laptop charger"), null));
        }
        else
        {
            var psu = ctx.Psu;
            items.Add(new GlanceItem("psu", UpgradeArea.PowerSupply, tr.T("Power supply (estimate)"), tr.F("About {0} W recommended", psu.RecommendedWatts),
                tr.F("Processor about {0} W, graphics about {1} W, rest about {2} W. Software cannot read the PSU: check its label.", psu.CpuWatts, psu.GpuWatts, psu.RestWatts)));
        }

        if (ctx.Screen is { } screen)
        {
            items.Add(new GlanceItem("screen", UpgradeArea.Graphics, tr.T("Main screen"),
                tr.F("{0} × {1} at {2} Hz", screen.Width, screen.Height, screen.RefreshHz), null));
        }

        return items;
    }

    internal static string DriveKind(PhysicalDrive drive, Translator tr) => drive.Media switch
    {
        DriveMedia.Hdd => tr.T("hard drive"),
        DriveMedia.Ssd when drive.BusType.Contains("NVMe", StringComparison.OrdinalIgnoreCase) => tr.T("NVMe SSD"),
        DriveMedia.Ssd when drive.BusType.Contains("SATA", StringComparison.OrdinalIgnoreCase) => tr.T("SATA SSD"),
        DriveMedia.Ssd => tr.T("SSD"),
        _ => drive.BusType,
    };

    internal static string? Join(params string?[] parts)
    {
        var present = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        return present.Count == 0 ? null : string.Join(" · ", present);
    }

    private static string? Maker(string? manufacturer)
    {
        if (string.IsNullOrWhiteSpace(manufacturer))
        {
            return null;
        }

        var m = manufacturer.Trim();
        return m.StartsWith("ASUSTeK", StringComparison.OrdinalIgnoreCase) ? "ASUS"
            : m.StartsWith("Micro-Star", StringComparison.OrdinalIgnoreCase) ? "MSI"
            : m.StartsWith("Gigabyte", StringComparison.OrdinalIgnoreCase) ? "Gigabyte"
            : m;
    }
}
