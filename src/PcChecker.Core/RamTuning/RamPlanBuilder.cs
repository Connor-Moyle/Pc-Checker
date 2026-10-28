using PcChecker.Core.Drivers.Platform;
using PcChecker.Core.Localization;
using PcChecker.Core.Memory;
using XmpEvaluation = PcChecker.Core.Overclocking.XmpEvaluation;

namespace PcChecker.Core.RamTuning;

/// <summary>
/// Turns the detected facts into the personalised BIOS plan: stage 1 BIOS update (and Intel's microcode fix), stage 2
/// XMP/EXPO and slots, stage 3 platform tuning (ratio, FCLK, Memory Context Restore, voltages), stage 4 easy manual
/// timings for known chips (Advanced). Everything the plan suggests stays inside <see cref="RamTuningRules"/>. Pure.
/// </summary>
public static partial class RamPlanBuilder
{
    /// <summary>
    /// Intel's 0x12B microcode reached board makers at the end of September 2024 (Intel community post, 2024-09-25), so a
    /// BIOS released before this date cannot contain it.
    /// </summary>
    public static readonly DateTime IntelMicrocode12BDate = new(2024, 9, 25);

    /// <summary>
    /// AMD's VSOC cap (1.30 V) shipped in AGESA 1.0.0.7 BIOSes from the end of April 2023; older AM5 BIOSes may let EXPO set
    /// more.
    /// </summary>
    public static readonly DateTime Am5VsocCapDate = new(2023, 5, 1);

    public static RamTuningPlan Build(RamFacts facts, DateTime today, Translator? tr = null)
    {
        ArgumentNullException.ThrowIfNull(facts);
        tr ??= Translator.English;
        var platform = facts.Platform;
        string profile = ProfileName(platform.Kind, facts.BoardMaker);
        var mainIc = MainIc(facts);
        bool dualRank = facts.MaxRanks is >= 2;
        var target = RamTuningRules.TargetFor(platform, facts.Modules.Count, dualRank, facts.SlotCount);
        int? expected = ExpectedMts(facts, target);

        var steps = new List<RamPlanStep>();
        if (platform.Kind == RamPlatformKind.Mobile)
        {
            steps.Add(new RamPlanStep(RamStage.UpdateBios, "mobile", tr.T("Memory speed is set by the maker"),
                tr.T("Laptops, handhelds and most mini PCs have no memory settings in the BIOS; the memory runs at the speed the maker chose."),
                tr.T("Keep the BIOS up to date through the maker's support app."), tr.T("None."), RamStepState.NotApplicable));
        }
        else
        {
            AddStage1(steps, facts, today, tr);
            AddStage2(steps, facts, profile, tr);
            AddStage3(steps, facts, target, tr);
            AddStage4(steps, facts, mainIc, target, tr);
        }

        var (headline, detail, tone) = Headline(facts, profile, expected, tr);
        return new RamTuningPlan
        {
            Headline = headline,
            HeadlineDetail = detail,
            Tone = tone,
            Steps = steps,
            VoltageLimits = RamTuningRules.VoltageLimits(platform, mainIc.Ic, tr),
            Target = target,
            ExpectedMts = expected,
            ProfileName = profile,
            IfItDoesNotBoot = IfItDoesNotBoot(facts, tr),
            Notes = Notes(facts, target, tr),
        };
    }

    /// <summary>What the BIOS calls the memory profile on this platform and board.</summary>
    public static string ProfileName(RamPlatformKind kind, PcMaker maker) => kind switch
    {
        RamPlatformKind.Am5 => "EXPO",
        RamPlatformKind.Am4 or RamPlatformKind.Am4Legacy => maker switch
        {
            PcMaker.Asus => "D.O.C.P.",
            PcMaker.Msi => "A-XMP",
            _ => "XMP",
        },
        _ => "XMP",
    };

    /// <summary>
    /// The speed Windows should report once the plan's safe steps are done: the kit's rating, capped at the platform's
    /// safe target with four modules, and on AM5 at 6000 for kits rated above 6400 (1:1 beats 1:2).
    /// </summary>
    public static int? ExpectedMts(RamFacts facts, RamTarget? target)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.RatedMts is not { } rated || rated <= 0)
        {
            return null;
        }

        if (facts.Modules.Count >= 4 && target is not null)
        {
            return Math.Min(rated, target.FourModuleMts);
        }

        if (facts.Platform.Kind == RamPlatformKind.Am5 && rated > 6400)
        {
            return 6000;
        }

        return rated;
    }

    /// <summary>The chip of the kit (the first module with a known chip, or the first module).</summary>
    public static RamIcGuess MainIc(RamFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return facts.Modules.Select(m => m.Ic).FirstOrDefault(i => i.IsKnownEnough)
            ?? facts.Modules.Select(m => m.Ic).FirstOrDefault()
            ?? RamIcGuess.Unknown("No modules were reported.");
    }

    private static (string Headline, string Detail, RamPlanTone Tone) Headline(RamFacts facts, string profile, int? expected, Translator tr)
    {
        if (facts.Modules.Count == 0)
        {
            return (tr.T("Windows did not report the memory modules"), tr.T("The plan below is generic. Run PC Checker as administrator and refresh."), RamPlanTone.Info);
        }

        if (facts.Platform.Kind == RamPlatformKind.Mobile)
        {
            return (tr.F("Your memory runs at {0} MT/s", facts.ConfiguredMts ?? 0), tr.T("On laptops the maker fixes the memory speed, so there is nothing to tune in the BIOS."), RamPlanTone.Info);
        }

        if (facts.Layout.Mode == ChannelMode.Single)
        {
            return (tr.T("Your memory runs in single channel"), facts.Layout.SlotAdvice ?? string.Empty, RamPlanTone.Warning);
        }

        int configured = facts.ConfiguredMts ?? 0;
        int rated = facts.RatedMts ?? 0;
        switch (facts.Xmp.State)
        {
            case XmpEvaluation.Disabled:
                return (tr.F("{0} is off: your memory runs at {1} instead of {2} MT/s", profile, configured, rated),
                    tr.F("Turning on {0} in the BIOS is free speed you already paid for. Follow stages 1 and 2 below.", profile), RamPlanTone.Warning);
            case XmpEvaluation.Enabled when expected is { } e && configured > e * 1.03:
                return (tr.F("{0} is on: {1} MT/s", profile, configured),
                    tr.F("Your platform is usually fastest at about {0} MT/s; stage 3 explains why.", e), RamPlanTone.Info);
            case XmpEvaluation.Enabled:
                return (tr.F("{0} is on: your memory runs at its rated {1} MT/s", profile, configured),
                    tr.T("The safe part is done. Stages 3 and 4 add a few percent more, if you want to tinker."), RamPlanTone.Good);
            default:
                return (tr.F("Your memory runs at {0} MT/s; {1} may be off", configured, profile),
                    tr.T("Windows reports a standard speed, which is also what kits without a profile run at. Check the kit's label and the BIOS."), RamPlanTone.Info);
        }
    }

    private static void AddStage1(List<RamPlanStep> steps, RamFacts facts, DateTime today, Translator tr)
    {
        var platform = facts.Platform;
        string? date = facts.BiosDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        string version = facts.BiosVersion is null ? tr.T("Your BIOS") : tr.F("Your BIOS ({0})", facts.BiosVersion);
        RamStepState state;
        string title = tr.T("Update the BIOS first");
        string what;
        if (platform.NeedsIntelMicrocodeFix && facts.BiosDate is { } d1 && d1 < IntelMicrocode12BDate)
        {
            state = RamStepState.ToDo;
            title = tr.T("Update the BIOS (needed: Intel microcode fix)");
            what = tr.F("{0} is from {1}, before Intel's 0x12B microcode. 13th and 14th gen Core chips need it (or the newer 0x12F) to avoid the Vmin shift damage that causes crashes.", version, date);
        }
        else if (platform.Kind == RamPlatformKind.Am5 && facts.BiosDate is { } d2 && d2 < Am5VsocCapDate)
        {
            state = RamStepState.ToDo;
            title = tr.T("Update the BIOS (needed: AMD's SoC voltage limit)");
            what = tr.F("{0} is from {1}. BIOSes from May 2023 on cap the SoC voltage at 1.30 V, which protects the processor when EXPO is on.", version, date);
        }
        else if (BiosRules.IsOld(facts.BiosDate, today))
        {
            state = RamStepState.ToDo;
            what = tr.F("{0} is from {1}. Newer versions train memory better, support more kits and fix stability problems.", version, date);
        }
        else
        {
            state = RamStepState.Check;
            what = date is null
                ? tr.T("Newer BIOS versions train memory better, support more kits and fix stability problems.")
                : tr.F("{0} is from {1}. Check the maker's page for a newer one: memory support improves with almost every release.", version, date);
        }

        if (platform.NeedsIntelMicrocodeFix && state == RamStepState.Check)
        {
            what += " " + tr.T("Make sure it has Intel's 0x12B or newer microcode (the release notes say so).");
        }

        steps.Add(new RamPlanStep(RamStage.UpdateBios, "bios-update", title, what,
            tr.T("Only your board maker's file for this exact board. Plug in, close everything and never switch off while it flashes."),
            tr.T("Low when done right. Updating resets every BIOS setting, so do it before anything else."), state)
        {
            MenuPath = BiosMenuPaths.For(BiosSetting.BiosUpdate, facts.BoardMaker, facts.BoardName, platform.Kind, tr),
            LinkUrl = facts.SupportPage?.Url,
            LinkText = facts.SupportPage is null ? null : tr.T("Open the BIOS download page"),
        });

        if (platform.NeedsIntelMicrocodeFix)
        {
            steps.Add(new RamPlanStep(RamStage.UpdateBios, "intel-defaults", tr.T("Use Intel Default Settings for the processor"),
                tr.T("Intel's recommended power and current limits for 13th and 14th gen. They protect the processor; memory settings are separate and stay yours."),
                tr.T("Pick the profile your board offers (Performance or Baseline)."),
                tr.T("None; at most a few percent less speed in long all-core loads."), RamStepState.Check)
            {
                MenuPath = BiosMenuPaths.For(BiosSetting.IntelDefaults, facts.BoardMaker, facts.BoardName, platform.Kind, tr),
            });
        }

        steps.Add(new RamPlanStep(RamStage.UpdateBios, "save-profile", tr.T("Save your settings as a BIOS profile"),
            tr.T("Most BIOSes can store settings in a profile slot or on a USB stick, so you can go back to a working setup in one step."),
            tr.T("Save one now and one after every change that passes the tests."), tr.T("None."), RamStepState.Check)
        {
            MenuPath = BiosMenuPaths.EnterBios(facts.BoardMaker, tr),
        });
    }

    private static void AddStage2(List<RamPlanStep> steps, RamFacts facts, string profile, Translator tr)
    {
        var platform = facts.Platform;
        if (!facts.Layout.RecommendedSlots && facts.Layout.SlotAdvice is { } advice)
        {
            steps.Add(new RamPlanStep(RamStage.EnableProfile, "slots",
                facts.Modules.Count == 1 ? tr.T("Add a second module") : tr.T("Use the right slots"), advice,
                tr.T("Switch off and unplug the PC before you touch the memory."), tr.T("None when the PC is off and unplugged."), RamStepState.ToDo));
        }

        if (facts.Layout.MixedKits)
        {
            steps.Add(new RamPlanStep(RamStage.EnableProfile, "mixed", tr.T("Mixed kits: expect a lower speed"),
                tr.T("The modules come from different kits. They often will not run the profile's speed together; the slowest kit's profile, or one step lower, usually works."),
                tr.T("Use the profile of the slower kit; one matched kit is best."), tr.T("Mixed kits are the most common reason the profile does not boot."), RamStepState.Check));
        }

        var timings = facts.Modules.Select(m => RamIcCatalog.ParseTimings(m.PartNumber)).FirstOrDefault(t => t.Cl is not null);
        bool jedecKit = facts.RatedMts is { } r && MemorySpeedRules.IsJedecSpeed(r, facts.MemoryType) && timings.Cl is null && facts.Xmp.State != XmpEvaluation.Disabled;
        string kitText = facts.RatedMts is { } rated
            ? timings.Cl is { } cl ? tr.F("{0} MT/s CL{1} on yours", rated, cl) : tr.F("{0} MT/s on yours", rated)
            : tr.T("the speed on the kit's label");

        var profileState = jedecKit ? RamStepState.NotApplicable
            : facts.Xmp.State switch
            {
                XmpEvaluation.Enabled => RamStepState.Done,
                XmpEvaluation.Disabled => RamStepState.ToDo,
                _ => RamStepState.Check,
            };
        string what = jedecKit
            ? tr.T("Your kit looks like a standard (JEDEC) kit without a profile, so it already runs at its rated speed. Faster speeds need manual overclocking (stage 3).")
            : tr.F("One switch sets the speed, timings and voltages the kit was tested at ({0}). Everything else can stay on Auto.", kitText);
        steps.Add(new RamPlanStep(RamStage.EnableProfile, "profile", tr.F("Turn on {0}", profile), what,
            platform.Kind == RamPlatformKind.Am5
                ? tr.T("Choose EXPO I (or XMP I if the kit only has XMP). Leave the rest on Auto.")
                : tr.F("Choose profile 1 ({0} I). Leave the rest on Auto.", profile),
            tr.T("Low. Strictly it is overclocking; a few kits need a BIOS update or one speed step lower to be fully stable."), profileState)
        {
            MenuPath = BiosMenuPaths.For(BiosSetting.MemoryProfile, facts.BoardMaker, facts.BoardName, platform.Kind, tr),
        });

        steps.Add(new RamPlanStep(RamStage.EnableProfile, "first-start", tr.T("Let the first start finish"),
            tr.T("After a memory change the board trains the memory. The screen can stay black for 1–3 minutes (longer with four or large modules) and the PC may restart a few times."),
            tr.T("Wait up to 5 minutes before you do anything."), tr.T("Switching off during training only makes it start over."), RamStepState.Check));

        steps.Add(new RamPlanStep(RamStage.EnableProfile, "test-profile", tr.T("Test before you trust it"),
            tr.T("Press \"Check my change\" below, run the quick memory check for 15 minutes, then one of the stability tools for an hour."),
            tr.T("No errors in any test."), tr.T("Errors mean step back: lower the speed one step or load the last saved profile."),
            profileState == RamStepState.Done ? RamStepState.Check : RamStepState.ToDo));
    }
}
