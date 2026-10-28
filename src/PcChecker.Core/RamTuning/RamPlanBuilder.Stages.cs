using System.Globalization;
using PcChecker.Core.Hardware;
using PcChecker.Core.Localization;

namespace PcChecker.Core.RamTuning;

public static partial class RamPlanBuilder
{
    private static void AddStage3(List<RamPlanStep> steps, RamFacts facts, RamTarget? target, Translator tr)
    {
        var p = facts.Platform;
        var maker = facts.BoardMaker;
        var board = facts.BoardName;
        string Path(BiosSetting s) => BiosMenuPaths.For(s, maker, board, p.Kind, tr);
        int? configured = facts.ConfiguredMts;
        int? rated = facts.RatedMts;
        bool four = facts.Modules.Count >= 4;

        if (target is null)
        {
            steps.Add(new RamPlanStep(RamStage.PlatformTuning, "generic", tr.T("Stop at the memory profile"),
                tr.T("PC Checker has no tested tuning rules for this processor, so the safe plan ends with the memory profile."),
                tr.T("XMP/EXPO and nothing more."), tr.T("Manual tuning without platform rules is guesswork."), RamStepState.NotApplicable));
            return;
        }

        switch (p.Kind)
        {
            case RamPlatformKind.Am5:
            {
                if (four)
                {
                    steps.Add(new RamPlanStep(RamStage.PlatformTuning, "am5-four", tr.T("With four modules, aim for 5200–5600 MT/s"),
                        tr.T("Four DDR5 modules load the memory controller much more. EXPO speeds often do not train; 5200–5600 usually does."),
                        "5200–5600 MT/s", tr.T("Higher speeds may not boot or may crash later."), configured is >= 5200 and <= 5600 ? RamStepState.Done : RamStepState.ToDo)
                    {
                        MenuPath = Path(BiosSetting.MemoryFrequency),
                    });
                }
                else
                {
                    bool fastKit = rated > 6400;
                    var state = fastKit && configured > 6400 ? RamStepState.ToDo
                        : configured is >= 5900 and <= 6450 ? RamStepState.Done
                        : RamStepState.Optional;
                    steps.Add(new RamPlanStep(RamStage.PlatformTuning, "am5-ratio", tr.T("Run 6000 MT/s with UCLK = MEMCLK (1:1)"),
                        fastKit
                            ? tr.F("Your kit is rated {0} MT/s. At that speed Ryzen runs the memory controller at half speed (1:2), which is usually slower. Set the memory to 6000 (6200–6400 if it trains) and UCLK DIV1 MODE to UCLK=MEMCLK.", rated ?? 0)
                            : tr.T("Ryzen 7000/9000 are fastest when the memory controller runs at the memory clock (1:1). DDR5-6000 does that on every chip; 6200–6400 on many."),
                        tr.T("6000 MT/s; 6200–6400 MT/s only if it passes the tests."),
                        tr.T("Low at 6000. 6200 and above may not train on every chip: go back to 6000 if it does not boot."), state)
                    {
                        MenuPath = Path(BiosSetting.MemoryFrequency) + "; " + Path(BiosSetting.UclkMode),
                    });
                }

                steps.Add(new RamPlanStep(RamStage.PlatformTuning, "am5-fclk", tr.T("Set FCLK to 2000 MHz"),
                    tr.T("FCLK is the Infinity Fabric speed that links the cores and the memory controller. Auto often picks less."),
                    tr.T("2000 MHz; 2100 MHz if stable."),
                    tr.T("Too high shows as WHEA hardware errors, stutter or crashes: go back to 2000."), RamStepState.Optional)
                {
                    MenuPath = Path(BiosSetting.Fclk),
                });

                steps.Add(new RamPlanStep(RamStage.PlatformTuning, "am5-mcr", tr.T("Turn on Memory Context Restore"),
                    tr.T("Saves the memory training, so later starts take seconds instead of a minute."),
                    tr.T("On. If the PC becomes unstable or fails to wake from sleep, set Power Down Enable to Disabled, or turn it off again."),
                    tr.T("Low."), RamStepState.Check)
                {
                    MenuPath = Path(BiosSetting.MemoryContextRestore),
                });

                steps.Add(new RamPlanStep(RamStage.PlatformTuning, "am5-vsoc", tr.T("Check the SoC voltage (VSOC)"),
                    tr.T("EXPO raises the processor's SoC voltage. 1.20–1.25 V is enough for DDR5-6000 on most chips."),
                    tr.F("At or below {0:0.00} V. Never above.", RamTuningRules.Am5VsocMax),
                    tr.T("Above 1.30 V can damage the processor."), RamStepState.Check)
                {
                    MenuPath = Path(BiosSetting.SocVoltage),
                });

                if (p.Zen == ZenGeneration.Zen5)
                {
                    steps.Add(new RamPlanStep(RamStage.PlatformTuning, "am5-8000", tr.T("DDR5-8000 with UCLK 1:2 (two-slot boards)"),
                        tr.T("Ryzen 9000 can run 8000 MT/s and faster with the memory controller at half speed. It only beats a tuned 6000–6400 1:1 by a little."),
                        tr.T("Two-slot board, a kit rated 8000+, FCLK 2000–2100, VSOC no more than 1.30 V."),
                        tr.T("Hard to make stable; long training times."), RamStepState.Optional)
                    {
                        Advanced = true,
                        MenuPath = Path(BiosSetting.MemoryFrequency),
                    });
                }

                break;
            }

            case RamPlatformKind.Am4:
            case RamPlatformKind.Am4Legacy:
            {
                bool legacy = p.Kind == RamPlatformKind.Am4Legacy;
                int safe = target.SafeMts;
                var state = configured is { } c && Math.Abs(c - safe) <= 70 ? RamStepState.Done
                    : rated is { } r && r >= safe ? RamStepState.ToDo : RamStepState.Optional;
                steps.Add(new RamPlanStep(RamStage.PlatformTuning, "am4-ratio",
                    legacy ? tr.F("Aim for DDR4-{0}", safe) : tr.F("Run DDR4-{0} with FCLK {1} MHz (1:1)", safe, safe / 2),
                    legacy
                        ? tr.T("Ryzen 1000/2000 memory controllers top out around 3200–3466 MT/s; faster memory still helps them a lot.")
                        : tr.T("Ryzen 3000/5000 are fastest when the Infinity Fabric (FCLK) runs at half the memory speed (1:1). Auto sometimes leaves it lower."),
                    legacy
                        ? tr.F("{0} MT/s; {1} if stable.", safe, target.StretchMts ?? safe)
                        : tr.F("{0} MT/s with FCLK {1} MHz; {2} MT/s with FCLK {3} MHz if stable.", safe, safe / 2, target.StretchMts ?? safe, target.FclkStretchMhz ?? safe / 2),
                    tr.T("Too high FCLK shows as WHEA 19 errors or stutter: go back one step."), state)
                {
                    MenuPath = Path(BiosSetting.MemoryFrequency) + (legacy ? string.Empty : "; " + Path(BiosSetting.Fclk)),
                });
                steps.Add(new RamPlanStep(RamStage.PlatformTuning, "am4-vsoc", tr.T("Check the SoC voltage (VSOC)"),
                    tr.T("XMP can push the SoC voltage high. DDR4-3600 needs about 1.05–1.10 V."),
                    tr.F("At or below {0:0.00} V.", RamTuningRules.Am4VsocMax), tr.T("More voltage than needed only adds heat and wear."), RamStepState.Check)
                {
                    MenuPath = Path(BiosSetting.SocVoltage),
                });
                break;
            }

            case RamPlatformKind.IntelLga1700Ddr5:
                steps.Add(new RamPlanStep(RamStage.PlatformTuning, "intel-speed", tr.F("Aim for about {0} MT/s in Gear 2", target.SafeMts),
                    four
                        ? tr.T("Four DDR5 modules are hard on the memory controller; 5200–5600 MT/s usually works.")
                        : tr.T("DDR5 runs in Gear 2 on these chips. Two modules usually manage 6000–6800 MT/s; two-slot boards go higher."),
                    tr.F("{0} MT/s; up to {1} MT/s if stable.", target.SafeMts, target.StretchMts ?? target.SafeMts),
                    tr.T("Higher speeds depend on your processor's memory controller: step back 200 MT/s if it is unstable."),
                    configured >= target.SafeMts * 0.97 ? RamStepState.Done : RamStepState.Optional)
                {
                    MenuPath = Path(BiosSetting.MemoryFrequency),
                });
                steps.Add(new RamPlanStep(RamStage.PlatformTuning, "intel-sa", tr.T("Check System Agent and VDD2 voltages"),
                    tr.T("Some boards set these very high with XMP. More is not better: too much can stop the memory from training."),
                    tr.F("VCCSA at or below {0:0.00} V, VDD2 at or below {1:0.00} V.", RamTuningRules.Lga1700VccsaMax, RamTuningRules.Lga1700Vdd2Max),
                    tr.T("High System Agent voltage wears the processor over time."), RamStepState.Check)
                {
                    MenuPath = Path(BiosSetting.SocVoltage),
                });
                break;

            case RamPlatformKind.IntelLga1700Ddr4:
                steps.Add(new RamPlanStep(RamStage.PlatformTuning, "intel-gear1", tr.T("Keep Gear 1 up to DDR4-3600–4000"),
                    tr.T("In Gear 1 the memory controller runs at the full memory clock. Above about 3600–4000 MT/s the board switches to Gear 2, which is usually slower."),
                    tr.F("{0} MT/s in Gear 1; up to {1} if stable.", target.SafeMts, target.StretchMts ?? target.SafeMts),
                    tr.T("Gear 1 at high speed needs a good memory controller; step back if unstable."), RamStepState.Optional)
                {
                    MenuPath = Path(BiosSetting.GearMode),
                });
                break;

            case RamPlatformKind.IntelArrowLake:
                steps.Add(new RamPlanStep(RamStage.PlatformTuning, "arl-boost", tr.T("Turn on Intel 200S Boost (K processors on Z890)"),
                    tr.T("Intel's own one-click profile: faster fabric and die-to-die clocks and memory up to DDR5-8000, within limits that keep Intel's warranty."),
                    tr.F("One kit, one module per channel; VDD2 at or below {0:0.00} V, VCCSA at or below {1:0.00} V.", RamTuningRules.ArrowLakeVdd2Max, RamTuningRules.ArrowLakeVccsaMax),
                    tr.T("Low; Intel covers it under warranty."), RamStepState.Optional));
                steps.Add(new RamPlanStep(RamStage.PlatformTuning, "arl-cudimm", tr.T("DDR5-8000+ with CUDIMM modules"),
                    tr.T("CUDIMM modules have their own clock driver and reach 8000 MT/s and more on good boards."),
                    tr.T("Two modules, a board that lists the kit; stay inside 200S Boost's voltages."),
                    tr.T("Needs a strong memory controller; step back if unstable."), RamStepState.Optional)
                {
                    Advanced = true,
                    MenuPath = Path(BiosSetting.MemoryFrequency),
                });
                break;
        }
    }

    private static void AddStage4(List<RamPlanStep> steps, RamFacts facts, RamIcGuess ic, RamTarget? target, Translator tr)
    {
        var p = facts.Platform;
        if (target is null)
        {
            return;
        }

        string path = BiosMenuPaths.For(BiosSetting.Timings, facts.BoardMaker, facts.BoardName, p.Kind, tr);
        int mts = facts.ConfiguredMts is > 0 && facts.Xmp.State == Overclocking.XmpEvaluation.Enabled ? facts.ConfiguredMts.Value
            : RamPlanBuilder.ExpectedMts(facts, target) ?? target.SafeMts;
        bool ddr5 = p.Kind is RamPlatformKind.Am5 or RamPlatformKind.IntelLga1700Ddr5 or RamPlatformKind.IntelArrowLake;

        if (!ic.IsKnownEnough || RamIcCatalog.TrfcNs(ic.Ic) is null)
        {
            steps.Add(new RamPlanStep(RamStage.ManualTimings, "find-ic", tr.T("Manual timings: find out your memory chips first"),
                tr.F("Safe timing values depend on the chips. {0}", ic.Reason),
                tr.T("Until the chips are known, keep the profile's timings."), tr.T("Timings meant for other chips may not boot."), RamStepState.NotApplicable)
            {
                Advanced = true,
            });
        }
        else
        {
            var (start, tight) = RamIcCatalog.TrfcNs(ic.Ic)!.Value;
            int startClocks = RamIcCatalog.NsToClocks(start, mts);
            int tightClocks = RamIcCatalog.NsToClocks(tight, mts);
            steps.Add(new RamPlanStep(RamStage.ManualTimings, "trfc", tr.F("Lower tRFC to about {0} ns", start),
                tr.F("tRFC is how long a refresh takes; the profile sets it very loose for {0}. Lowering it is the easiest timing gain.", ic.Name),
                tr.F("{0} ns = {1} clocks at {2} MT/s; {3} ns ({4}) if stable.", start, startClocks, mts, tight, tightClocks)
                    + (ddr5 ? " " + tr.T("Leave tRFC2 and tRFCsb on Auto.") : string.Empty),
                tr.T("Too low will not boot or fails memory tests: go back to the profile value."), RamStepState.Optional)
            {
                Advanced = true,
                MenuPath = path,
            });

            if (ddr5 && ic.Ic is DramIc.HynixDdr5Gen16 or DramIc.HynixDdr5Gen24)
            {
                steps.Add(new RamPlanStep(RamStage.ManualTimings, "twr", tr.T("Set tWR to 48"),
                    tr.T("Write recovery time. Profiles often set 72–96; 48 runs on most kits, even at high speeds."),
                    tr.T("48. Leave tRTP on Auto."), tr.T("Low; go back to Auto if a test fails."), RamStepState.Optional)
                {
                    Advanced = true,
                    MenuPath = path,
                });
            }
        }

        // tREFI is not adjustable on AM4 (Zen 2/3); everywhere else it is the biggest single gain, but heat-sensitive.
        if (p.Kind is not (RamPlatformKind.Am4 or RamPlatformKind.Am4Legacy))
        {
            double? hottest = facts.DimmTemperatures.Count == 0 ? null : facts.DimmTemperatures.Max(t => t.Celsius);
            bool tooHot = hottest >= RamTuningRules.DimmTightTimingsMaxC;
            string value = ddr5 ? "32768–50000" : "32768";
            steps.Add(new RamPlanStep(RamStage.ManualTimings, "trefi", tr.F("Raise tREFI to {0}", value),
                tr.T("tREFI is the time between refreshes. Longer means the memory is busy refreshing less often, but warm memory loses data faster."),
                tr.F("{0}, only while the modules stay below {1:0} °C under load.", value, RamTuningRules.DimmTightTimingsMaxC),
                tooHot
                    ? tr.F("Your modules reach {0:0} °C, too warm for this. Add airflow first.", hottest ?? 0)
                    : tr.T("Errors climb with temperature: a hot day can make it unstable."),
                tooHot ? RamStepState.NotApplicable : RamStepState.Optional)
            {
                Advanced = true,
                MenuPath = path,
            });
        }

        steps.Add(new RamPlanStep(RamStage.ManualTimings, "one-at-a-time", tr.T("Change one timing at a time"),
            tr.T("After each change, start Windows, run the quick check and a stability tool. If it fails, you know which change did it."),
            tr.T("One change, one test, one saved profile."), tr.T("Several changes at once make errors impossible to trace."), RamStepState.Optional)
        {
            Advanced = true,
        });
    }

    private static IReadOnlyList<string> IfItDoesNotBoot(RamFacts facts, Translator tr)
    {
        string step = facts.MemoryType?.Contains("DDR4", StringComparison.OrdinalIgnoreCase) == true || facts.Platform.Kind is RamPlatformKind.Am4 or RamPlatformKind.Am4Legacy or RamPlatformKind.IntelLga1700Ddr4
            ? "3600 → 3466 MT/s"
            : "6000 → 5800 MT/s";
        return
        [
            tr.T("Wait up to 5 minutes. Memory training can keep the screen black for 1–3 minutes, longer with four modules. Do not switch off during that time."),
            tr.T("If the PC restarts a few times and then shows a message such as \"Overclocking failed\", the board has gone back to safe settings by itself. Press F1 (or Delete) to enter the BIOS."),
            tr.T("Still black after 5 minutes: switch off and clear the BIOS settings (CMOS) with the Clear CMOS button on the back panel, the CLR_CMOS jumper (see the manual), or by unplugging the PC and removing the coin battery for a few minutes."),
            tr.F("Load your saved profile, then go one speed step lower ({0}) or undo the last timing you changed.", step),
            tr.T("Windows starts but crashes, or a test finds errors: undo the last change. Never raise a voltage above the table to fix it."),
        ];
    }

    private static IReadOnlyList<string> Notes(RamFacts facts, RamTarget? target, Translator tr)
    {
        var notes = new List<string>();
        if (target?.OfficialMts is { } official)
        {
            notes.Add(tr.F("The processor's official memory speed is {0} MT/s. Anything faster, XMP/EXPO included, is overclocking by the maker's definition; at the speeds in this plan it rarely causes trouble.", official));
        }

        if (facts.IsBrandPc)
        {
            notes.Add(tr.T("Brand PCs (Dell, HP, Lenovo and others) usually have no memory settings in the BIOS; gaming models sometimes offer XMP."));
        }

        if (facts.DimmTemperatures.Count > 0 && facts.DimmTemperatures.Max(t => t.Celsius) >= RamTuningRules.DimmWarnC)
        {
            notes.Add(tr.F("The memory modules run at {0:0} °C. Above about {1:0} °C DDR5 needs a fan blowing over it, even at the profile's settings.",
                facts.DimmTemperatures.Max(t => t.Celsius), RamTuningRules.DimmWarnC));
        }

        notes.Add(tr.T("PC Checker cannot change BIOS settings. Follow the steps in the BIOS, then press \"Check my change\" here."));
        return notes;
    }

    /// <summary>A plain description of the platform ("AMD Ryzen 7000/9000 on AM5 (DDR5)").</summary>
    public static string Describe(RamPlatform platform, Translator? tr = null)
    {
        ArgumentNullException.ThrowIfNull(platform);
        tr ??= Translator.English;
        string gen = platform.IntelGeneration is { } g ? g.ToString(CultureInfo.InvariantCulture) : "?";
        return platform.Kind switch
        {
            RamPlatformKind.Am5 => tr.F("AMD Ryzen on AM5 ({0}), DDR5", CpuTraits.GenerationName(platform.Zen) ?? "Zen 4/5") + (platform.IsX3D ? " · 3D V-Cache" : string.Empty),
            RamPlatformKind.Am4 => tr.F("AMD Ryzen on AM4 ({0}), DDR4", CpuTraits.GenerationName(platform.Zen) ?? "Zen 2/3") + (platform.IsX3D ? " · 3D V-Cache" : string.Empty),
            RamPlatformKind.Am4Legacy => tr.F("AMD Ryzen on AM4 ({0}), DDR4", CpuTraits.GenerationName(platform.Zen) ?? "Zen"),
            RamPlatformKind.IntelLga1700Ddr5 => tr.F("Intel Core {0}th gen on LGA1700, DDR5", gen),
            RamPlatformKind.IntelLga1700Ddr4 => tr.F("Intel Core {0}th gen on LGA1700, DDR4", gen),
            RamPlatformKind.IntelArrowLake => tr.T("Intel Core Ultra 200S on LGA1851, DDR5"),
            RamPlatformKind.Mobile => tr.T("Laptop or mini PC"),
            RamPlatformKind.OtherDesktop => tr.T("Desktop without tested tuning rules"),
            _ => tr.T("Unknown processor"),
        };
    }
}
