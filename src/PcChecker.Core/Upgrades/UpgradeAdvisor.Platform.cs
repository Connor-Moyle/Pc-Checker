using System.Globalization;
using PcChecker.Core.Common;
using PcChecker.Core.Hardware;

namespace PcChecker.Core.Upgrades;

/// <summary>One class of current graphics card the advisor can point to, with its typical board power.</summary>
public sealed record GpuSuggestionClass(int Level, IReadOnlyList<string> Cards, int BoardPowerW);

public static partial class UpgradeAdvisor
{
    /// <summary>Current graphics card classes, from 1080p (0) to the very top (4). Names only; never prices.</summary>
    public static IReadOnlyList<GpuSuggestionClass> GpuClasses { get; } =
    [
        new(0, ["GeForce RTX 5060 Ti 16 GB", "Radeon RX 9060 XT 16 GB"], 180),
        new(1, ["GeForce RTX 5070", "Radeon RX 9070"], 250),
        new(2, ["GeForce RTX 5070 Ti", "Radeon RX 9070 XT"], 304),
        new(3, ["GeForce RTX 5080"], 360),
        new(4, ["GeForce RTX 5090"], 575),
    ];

    /// <summary>
    /// The class worth buying: at least what the screen needs (1080p 0, 1440p 1, 4K 3) and one step above the current
    /// card (an aging or old card counts one step lower). Null when the current card is already at the top.
    /// </summary>
    public static GpuSuggestionClass? SuggestGpuClass(GpuClass current, GpuAge age, int? screenWidth)
    {
        ArgumentNullException.ThrowIfNull(current);
        int resolution = screenWidth is >= 3800 ? 3 : screenWidth is >= 2500 ? 1 : 0;
        int level = current.Tier switch
        {
            GpuTier.Mainstream => 0,
            GpuTier.UpperMid => 1,
            GpuTier.High => 2,
            GpuTier.Enthusiast => 3,
            _ => -1,
        };
        if (age is GpuAge.Aging or GpuAge.Old && level >= 0)
        {
            level--;
        }

        if (current.Tier == GpuTier.Enthusiast && age == GpuAge.Current)
        {
            return null;
        }

        int wanted = Math.Clamp(Math.Max(resolution, level + 1), 0, GpuClasses.Count - 1);
        return GpuClasses[wanted];
    }

    private static void AddProcessorAdvice(AdviceContext ctx)
    {
        var tr = ctx.Tr;
        var cpu = ctx.Cpu;
        if (CpuUpgradePaths.NeedsRaptorLakeMicrocode(cpu, ctx.CpuInfo?.Get("Registry.Microcode")) == true)
        {
            ctx.Fixes.Add(new FixFirstItem("cpu.microcode", UpgradeArea.Processor, Severity.High,
                tr.T("Update the BIOS for your 13th/14th gen Intel processor"),
                tr.F("Its microcode ({0}) is older than 0x12B, the fix for the known instability that can permanently damage these chips. Install the newest BIOS from the board maker's support page.",
                    ctx.CpuInfo?.Get("Registry.Microcode") ?? "?"),
                "drivers", tr.T("Open Drivers")));
        }

        if (ctx.IsLaptop || cpu.Socket is null)
        {
            return;
        }

        var path = CpuUpgradePaths.PathFor(cpu.Socket);
        var options = CpuUpgradePaths.UpgradesFrom(cpu);
        var balance = ctx.Balance;
        bool cpuLimited = balance.Verdict == BoundVerdict.CpuLimited;
        int? tier = CpuUpgradePaths.TierOf(cpu);
        bool old = cpu.IntelGeneration is <= 9 || cpu.Zen is ZenGeneration.Zen1 or ZenGeneration.ZenPlus or ZenGeneration.Zen2;
        string share = Math.Round(balance.CpuLimitedShare * 100).ToString("0", CultureInfo.InvariantCulture);

        if (path is not null && options.Count > 0)
        {
            int score = cpuLimited ? (balance.Confidence == AdviceConfidence.High ? 80 : 74) : old ? 55 : 22;
            string why = cpuLimited
                ? tr.F("In your recorded games the graphics card waited for the processor for {0} % of the gaming time. A faster processor raises the frame rate there.", share)
                : old
                    ? tr.T("Your processor is several generations old. A drop-in chip for the same board is a solid step without replacing anything else.")
                    : tr.T("Your processor is not what holds your games back right now. Only worth it for heavy work apps, or if CPU-heavy games stutter.");
            var names = options.Select(o => o.ForGaming && !o.ForWork ? tr.F("{0} (best for games)", o.Name)
                : o.ForWork && !o.ForGaming ? tr.F("{0} (best for work apps)", o.Name)
                : tr.F("{0} (games and work)", o.Name)).ToList();
            ctx.Upgrades.Add(new UpgradeSuggestion("cpu.dropin", UpgradeArea.Processor, score, ImpactFor(score),
                tr.F("A faster processor for your {0} board", cpu.Socket), why, ImpactLabel(ImpactFor(score), tr), names,
                tr.T(path.BeforeYouSwap), "hardware", tr.T("Open Hardware")));
        }

        bool win11Blocked = ctx.Windows11.Checks.Any(c => c.Id == "cpu" && c.Passed == false);
        bool topOfSocket = options.Count == 0 || options.All(o => o.Tier <= (tier ?? 0) + 1);
        int platformScore = win11Blocked ? 72
            : cpuLimited && (path is null || path.EndOfPlatform) && topOfSocket ? 66
            : path is { Options.Count: 0 } ? 58
            : 0;
        if (platformScore > 0)
        {
            string why = win11Blocked
                ? tr.T("Your processor is too old for Windows 11, and Windows 10 no longer gets regular security updates. A new board, processor and memory fixes both.")
                : cpuLimited
                    ? tr.T("Your games wait for the processor, and your board has no much faster chip left. The next real step is a new board, processor and memory.")
                    : tr.T("This platform is too old for a worthwhile drop-in processor. A new board, processor and memory is the next real step.");
            ctx.Upgrades.Add(new UpgradeSuggestion("cpu.platform", UpgradeArea.Processor, platformScore, ImpactFor(platformScore),
                tr.T("A new platform: board, processor and memory"), why, ImpactLabel(ImpactFor(platformScore), tr),
                [
                    tr.T("AM5 board with a Ryzen 7 9800X3D (fastest for games)"),
                    tr.T("LGA1851 board with a Core Ultra 7 265K (strong in work apps)"),
                    tr.T("32 GB DDR5 memory (2 x 16 GB, 6000 MT/s)"),
                ],
                tr.T("Your graphics card, drives and case carry over, and the PSU if it is big enough. Windows may ask to be activated again after a board change."),
                "hardware", tr.T("Open Hardware")));
        }
    }

    private static void AddGraphicsAdvice(AdviceContext ctx)
    {
        var tr = ctx.Tr;
        if (ctx.Gpu is null)
        {
            return;
        }

        if (GpuRules.IsLinkNarrowed(ctx.Facts.GpuLink) && ctx.Facts.GpuLink is { } link && !ctx.Gpu.IsIntegrated)
        {
            ctx.Fixes.Add(new FixFirstItem("gpu.link", UpgradeArea.Graphics, Severity.Medium,
                tr.F("The graphics card runs with x{0} lanes instead of x{1}", link.CurrentWidth ?? 0, link.MaxWidth ?? 0),
                tr.T("Usually the card sits in a lower slot, is not pushed fully in, or an M.2 drive shares its lanes. Use the top full-length slot and check your board's manual."),
                "hardware", tr.T("Open Hardware")));
        }

        if (ctx.IsLaptop)
        {
            return;
        }

        var gpu = ctx.GpuClass;
        var age = GpuRules.AgeOf(gpu, ctx.Year);
        var balance = ctx.Balance;
        var screen = ctx.Screen;
        var fit = GpuRules.EvaluateVram(ctx.VramGb, screen?.Width);
        var reasons = new List<string>();
        int score;
        if (gpu.Tier == GpuTier.Integrated)
        {
            score = 80;
            reasons.Add(tr.T("Games run on the processor's built-in graphics. Any current graphics card is a huge step."));
        }
        else
        {
            score = 0;
            if (balance.Verdict == BoundVerdict.GpuLimited)
            {
                score += balance.Confidence switch { AdviceConfidence.High => 70, AdviceConfidence.Medium => 62, _ => 52 };
                reasons.Add(tr.F("In your recorded games the graphics card was the limit for {0} % of the gaming time, while the processor had room to spare.",
                    Math.Round(balance.GpuLimitedShare * 100).ToString("0", CultureInfo.InvariantCulture)));
            }

            if (fit is VramFit.TooLittle or VramFit.Tight && screen is { } s)
            {
                score += fit == VramFit.TooLittle ? 25 : 15;
                reasons.Add(fit == VramFit.TooLittle
                    ? tr.F("{0} GB of video memory is too little for {1} × {2} in new games: expect stutter or blurry textures.", ctx.VramGb ?? 0, s.Width, s.Height)
                    : tr.F("{0} GB of video memory is tight for {1} × {2} in new games; some need lower texture settings.", ctx.VramGb ?? 0, s.Width, s.Height));
            }

            if (age is GpuAge.Old or GpuAge.Aging)
            {
                score += age == GpuAge.Old ? 20 : 10;
                reasons.Add(tr.F("Its generation ({0}) came out in {1} and misses newer features such as the latest upscaling.",
                    gpu.Generation, gpu.ReleaseYear ?? 0));
            }

            if (balance.Verdict == BoundVerdict.CpuLimited)
            {
                score = Math.Max(0, score - 20);
            }
        }

        if (score < 15 || SuggestGpuClass(gpu, age, screen?.Width) is not { } target)
        {
            return;
        }

        var withNewCard = ctx.Psu with { GpuWatts = target.BoardPowerW, GpuFromTable = true };
        ctx.Upgrades.Add(new UpgradeSuggestion("gpu.upgrade", UpgradeArea.Graphics, score, ImpactFor(score),
            tr.T("A faster graphics card"), string.Join(" ", reasons), ImpactLabel(ImpactFor(score), tr), target.Cards,
            tr.F("Plan for a good-quality PSU of about {0} W with such a card (estimate). If yours is smaller or more than about 8 years old, replace it too, and check that the card fits your case.",
                withNewCard.RecommendedWatts),
            "hardware", tr.T("Open Hardware")));
    }

    private static void AddWindowsAdvice(AdviceContext ctx)
    {
        var tr = ctx.Tr;
        var w = ctx.Windows11;
        if (w.Status == Win11Status.ReadyAfterBiosChange)
        {
            foreach (var check in w.Checks.Where(c => c.Passed == false && c.FixableInBios))
            {
                ctx.Fixes.Add(new FixFirstItem("win11." + check.Id, UpgradeArea.Windows, Severity.Medium,
                    tr.F("Windows 11 needs a BIOS change: {0}", check.Title), check.Detail, "security", tr.T("Open Security")));
            }
        }
        else if (w.Status == Win11Status.Ready)
        {
            ctx.Fixes.Add(new FixFirstItem("win11.upgrade", UpgradeArea.Windows, Severity.Medium,
                tr.T("Upgrade to Windows 11 for free"), w.Summary, "windows-update", tr.T("Open Windows Update")));
        }
    }
}
