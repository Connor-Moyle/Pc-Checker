using System.Globalization;
using PcChecker.Core.Common;
using PcChecker.Core.Memory;

namespace PcChecker.Core.Upgrades;

public static partial class UpgradeAdvisor
{
    /// <summary>The RAM tuning page; the app falls back to the Memory page when it is not there.</summary>
    public const string RamTuningPage = "ram-tuning";

    private static void AddMemoryAdvice(AdviceContext ctx)
    {
        var tr = ctx.Tr;
        var layout = ctx.Layout;
        var modules = ctx.Facts.Hardware?.MemoryModules ?? [];
        if (layout.ModuleCount == 0)
        {
            return;
        }

        // Laptops: the memory is often soldered or the slots cannot be swapped, so a "move the stick" fix is not offered.
        if (layout.SameChannel && !ctx.IsLaptop)
        {
            ctx.Fixes.Add(new FixFirstItem("memory.same-channel", UpgradeArea.Memory, Severity.Medium,
                tr.T("Both memory sticks are in the same channel"),
                tr.F("They run in single channel, with half the memory bandwidth. Move one so they sit in {0} (check your board's manual). It is free and makes games smoother.", layout.RecommendedSlots),
                "memory", tr.T("Open Memory")));
        }
        else if (layout.WrongSlots)
        {
            ctx.Fixes.Add(new FixFirstItem("memory.slots", UpgradeArea.Memory, Severity.Low,
                tr.F("Memory is in slots {0}", string.Join(" + ", layout.CurrentSlots)),
                tr.F("Most four-slot boards want two sticks in {0} (the second and fourth slot from the processor). Those slots reach high XMP/EXPO speeds most reliably. Check your board's manual.", layout.RecommendedSlots),
                "memory", tr.T("Open Memory")));
        }

        if (layout.MixedKits)
        {
            ctx.Fixes.Add(new FixFirstItem("memory.mixed", UpgradeArea.Memory, Severity.Low,
                tr.T("Your memory sticks come from different kits"),
                tr.T("Mixed sticks often refuse to run at their rated speed together. If the PC crashes with XMP/EXPO on, set a lower memory speed in the BIOS, or replace them with one matched kit."),
                "memory", tr.T("Open Memory")));
        }

        string? platform = ctx.Cpu.IsAmd ? "AMD" : ctx.Cpu.IsIntel ? "Intel" : null;
        var xmp = MemoryRules.EvaluateXmp(modules, platform, ctx.CpuInfo?.Name);
        if (xmp.IsDisabled)
        {
            ctx.Fixes.Add(new FixFirstItem("memory.xmp", UpgradeArea.Memory, Severity.Medium,
                tr.F("Your memory runs at {0} MT/s instead of its rated {1} MT/s", xmp.ConfiguredMhz ?? 0, xmp.RatedMhz ?? 0),
                tr.F("Turn on {0} in the BIOS to get the speed you paid for. It is the safest free speed-up there is; RAM tuning walks you through it.", xmp.ProfileName),
                RamTuningPage, tr.T("Open RAM tuning")));
        }

        // Single stick: a second matching one doubles the bandwidth.
        if (layout.ModuleCount == 1 && (ctx.Slots is null or > 1))
        {
            bool igpu = ctx.GpuClass.Tier == GpuTier.Integrated;
            int score = igpu ? 78 : 64;
            ctx.Upgrades.Add(new UpgradeSuggestion("memory.dual", UpgradeArea.Memory, score, ImpactFor(score),
                tr.T("Add a second memory stick for dual channel"),
                igpu
                    ? tr.T("Only one stick is installed, so the memory runs in single channel. Integrated graphics use system memory, so a second stick makes a big difference.")
                    : tr.T("Only one stick is installed, so the memory runs in single channel with half the bandwidth. A second matching stick makes games smoother."),
                ImpactLabel(ImpactFor(score), tr),
                [tr.F("A matching {0} stick of the same size and speed, or one new 2-stick kit", ctx.MemoryType ?? "RAM")],
                ctx.IsLaptop ? tr.T("Laptops: check the manual first; many have one stick soldered or only one slot.") : null,
                "memory", tr.T("Open Memory")));
        }

        AddMemorySizeAdvice(ctx);
    }

    private static void AddMemorySizeAdvice(AdviceContext ctx)
    {
        var tr = ctx.Tr;
        var pressure = ctx.Pressure;
        if (MemoryLayoutRules.RecommendedTotalGb(ctx.TotalMemoryBytes, pressure) is not { } target || ctx.TotalMemoryBytes is not { } total)
        {
            return;
        }

        double gb = total / (1024d * 1024 * 1024);
        int score = gb < 7 ? 90 : gb < 15 ? (pressure.IsOftenFull ? 85 : 62) : gb < 31 ? 66 : 45;
        string installed = Math.Round(gb).ToString("0", CultureInfo.InvariantCulture);
        string why = pressure.IsOftenFull
            ? tr.F("{0} GB is installed and memory was over 85 % full for {1} % of the recorded time (peak {2} %). Windows then swaps to the drive and games stutter.",
                installed, Math.Round(pressure.HighShare * 100).ToString("0", CultureInfo.InvariantCulture),
                Math.Round(pressure.PeakPercent ?? 0).ToString("0", CultureInfo.InvariantCulture))
            : tr.F("{0} GB is installed. 16 GB is the comfortable minimum for games and a browser today; 32 GB leaves room for both at once.", installed);
        string kit = ctx.IsLaptop
            ? tr.F("{0} GB in total as laptop memory (SO-DIMM {1})", target, ctx.MemoryType ?? "DDR")
            : tr.F("One {0} GB {1} kit of two matched sticks (2 x {2} GB)", target, ctx.MemoryType ?? "RAM", target / 2);
        ctx.Upgrades.Add(new UpgradeSuggestion("memory.capacity", UpgradeArea.Memory, score, ImpactFor(score),
            tr.F("Upgrade to {0} GB of memory", target), why, ImpactLabel(ImpactFor(score), tr), [kit],
            ctx.IsLaptop
                ? tr.T("Check the laptop's manual first: many thin laptops have soldered memory that cannot be upgraded.")
                : tr.T("Buy one matched kit instead of adding sticks to the ones you have, and check your board's memory support list (QVL)."),
            "memory", tr.T("Open Memory")));
    }
}
