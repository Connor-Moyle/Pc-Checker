using PcChecker.Core.Hardware;
using PcChecker.Core.Localization;

namespace PcChecker.Core.RamTuning;

/// <summary>
/// Speed targets and daily voltage ceilings per platform. Where sources disagree the lower, safer value is used, and
/// faster settings are offered as "if stable". Every value has its source next to it.
/// </summary>
public static class RamTuningRules
{
    // ---- Voltage ceilings (volts). Never recommend anything above these. ----

    /// <summary>
    /// AM5 SoC voltage: AMD's April 2023 statement after the burnt 7000X3D reports: "new EFI updates ... limit the maximum
    /// available SoC voltage to 1.3V" (reported by Tom's Hardware, TechPowerUp, Hardware Cooling).
    /// </summary>
    public const double Am5VsocMax = 1.30;

    /// <summary>
    /// DDR5 VDD/VDDQ daily ceiling: EXPO/XMP kits ship at 1.25–1.45 V; 1.40 V is the common daily ceiling, SK hynix tolerates
    /// up to about 1.45 V with a fan over the memory (overclock.net AMD DDR5 OC thread; ocinside AM5 DDR5 guide).
    /// </summary>
    public const double Ddr5VddMax = 1.40;

    /// <summary>The SK hynix exception: only with good airflow over the modules.</summary>
    public const double Ddr5VddMaxHynixAirflow = 1.45;

    /// <summary>AM4 SoC voltage: 1.05–1.10 V is typical for DDR4-3600; community ceiling 1.15–1.20 V (Buildzoid, 1usmus' DRAM Calculator).</summary>
    public const double Am4VsocMax = 1.20;

    /// <summary>DDR4 VDIMM daily: 1.45 V for most chips; Samsung B-die tolerates 1.50 V with airflow (same sources).</summary>
    public const double Ddr4VdimmMax = 1.45;

    public const double Ddr4VdimmMaxBDie = 1.50;

    /// <summary>
    /// LGA1700 System Agent: Intel publishes no overclocking limit; DDR5 profiles usually set 1.15–1.25 V and 1.25 V is
    /// the conservative daily ceiling used by board vendors' guides (ASUS ROG, MSI) and reviewers.
    /// </summary>
    public const double Lga1700VccsaMax = 1.25;

    /// <summary>LGA1700 VDD2 (the processor's memory controller I/O, "VDDQ TX"): 1.40 V daily ceiling, conservative.</summary>
    public const double Lga1700Vdd2Max = 1.40;

    /// <summary>Arrow Lake: Intel 200S Boost's own limits, which keep the warranty: VDD2 ≤ 1.4 V, VccSA ≤ 1.20 V, memory ≤ 1.4 V (game.intel.com, "200S Boost").</summary>
    public const double ArrowLakeVdd2Max = 1.40;

    public const double ArrowLakeVccsaMax = 1.20;

    // ---- DIMM temperatures (°C) ----

    /// <summary>Tight timings (a raised tREFI) are only for DIMMs below this: refresh errors climb with heat (ocinside; overclock.net).</summary>
    public const double DimmTightTimingsMaxC = 50;

    /// <summary>Above this a DDR5 module needs airflow, even at XMP/EXPO.</summary>
    public const double DimmWarnC = 55;

    /// <summary>The speed plan for <paramref name="platform"/> with <paramref name="moduleCount"/> modules (dual rank or not).</summary>
    public static RamTarget? TargetFor(RamPlatform platform, int moduleCount, bool dualRank, int slotCount)
    {
        ArgumentNullException.ThrowIfNull(platform);
        bool four = moduleCount >= 4;
        switch (platform.Kind)
        {
            case RamPlatformKind.Am5:
            {
                // AMD's own numbers: Ryzen 7000 supports DDR5-5200, Ryzen 9000 DDR5-5600; AMD recommends EXPO DDR5-6000 as
                // the sweet spot, where UCLK runs 1:1 with MEMCLK (AMD Ryzen 7000/9000 product pages, ocinside AM5 guide).
                // 6200–6400 1:1 trains on many but not all chips (ocinside: 6200 stable, 6400 1:1 refused on a 7800X3D).
                // FCLK 2000 MHz; 2100 if stable (2133–2167 needs up to the 1.30 V VSOC ceiling, ocinside).
                int official = platform.Zen == ZenGeneration.Zen5 ? 5600 : 5200;
                return new RamTarget(four ? 5200 : 6000, four ? 5600 : 6400, "UCLK = MEMCLK (1:1)", 2000, 2100, 5600, official);
            }

            case RamPlatformKind.Am4:
            {
                // Ryzen 3000/5000 officially support DDR4-3200. DDR4-3600 with FCLK 1800 MHz (1:1) is the well-known sweet
                // spot; Zen 3 often reaches FCLK 1866–1900 (DDR4-3733–3800), Zen 2 rarely above 1800–1866 (AMD's Ryzen 5000
                // reviewer material named FCLK 2000 "on some samples"; Buildzoid, Gamers Nexus).
                int stretch = platform.Zen == ZenGeneration.Zen3 ? 3800 : 3733;
                int fclkStretch = platform.Zen == ZenGeneration.Zen3 ? 1900 : 1866;
                return new RamTarget(four && dualRank ? 3200 : 3600, four ? 3600 : stretch, "FCLK = MEMCLK = UCLK (1:1:1)", 1800, fclkStretch, dualRank ? 3200 : 3600, 3200);
            }

            case RamPlatformKind.Am4Legacy:
                // Ryzen 1000/2000: officially DDR4-2666/2933; 3200 is the realistic target, 3466 on good Zen+ samples.
                return new RamTarget(3200, 3466, "MEMCLK = UCLK", null, null, 3000, 2933);

            case RamPlatformKind.IntelLga1700Ddr5:
            {
                // Intel 13th/14th gen support DDR5-5600 with one module per channel and less with two (Intel ARK and the
                // 14th gen quick reference guide); 12th gen DDR5-4800. Gear 2 is the normal DDR5 mode. Two-module boards
                // reach 6400–7200 with a good memory controller; four-slot boards usually 6000–6800; four modules
                // 5200–5600 (board vendors' QVLs, reviewers).
                bool gen12 = platform.IntelGeneration == 12;
                int official = gen12 ? 4800 : 5600;
                int safe = four ? 5200 : gen12 ? 5600 : 6000;
                int stretch = four ? 5600 : gen12 ? 6400 : slotCount == 2 ? 7200 : 6800;
                return new RamTarget(safe, stretch, "Gear 2", null, null, 5600, official);
            }

            case RamPlatformKind.IntelLga1700Ddr4:
                // DDR4 on LGA1700 runs Gear 1 (memory controller at full memory clock) up to about DDR4-3600–4000;
                // above that the board switches to Gear 2, which is usually slower (reviewers, board guides).
                return new RamTarget(3600, platform.IntelGeneration == 12 ? 3733 : 4000, "Gear 1", null, null, 3600, 3200);

            case RamPlatformKind.IntelArrowLake:
                // Core Ultra 200S: DDR5-6400 official (CUDIMM), Intel 200S Boost allows up to DDR5-8000 with one kit, one module
                // per channel, within its voltage limits (game.intel.com "200S Boost").
                return new RamTarget(four ? 5600 : 6400, four ? 6000 : 8000, "Gear 2", null, null, 5600, 6400);

            default:
                return null;
        }
    }

    /// <summary>The voltage table for the platform, with daily ceilings that never exceed the vendors' limits.</summary>
    public static IReadOnlyList<VoltageLimit> VoltageLimits(RamPlatform platform, DramIc ic, Translator? tr = null)
    {
        ArgumentNullException.ThrowIfNull(platform);
        tr ??= Translator.English;
        bool hynix = ic is DramIc.HynixDdr5Gen16 or DramIc.HynixDdr5Gen24;
        string ddr5Note = hynix
            ? tr.F("SK hynix chips are fine up to {0:0.00} V with a fan blowing over the memory.", Ddr5VddMaxHynixAirflow)
            : tr.T("Use the XMP/EXPO value; raise it only in small steps and only if needed.");
        switch (platform.Kind)
        {
            case RamPlatformKind.Am5:
                return
                [
                    new("VSOC (CPU SoC)", tr.T("The processor's memory controller and Infinity Fabric"), "1.20–1.25 V", Am5VsocMax,
                        tr.T("AMD's hard limit. Never go above it: higher values have damaged Ryzen 7000 chips. Current BIOSes refuse more.")),
                    new("VDD / VDDQ (DRAM)", tr.T("The memory chips"), tr.T("EXPO value (1.25–1.40 V)"), Ddr5VddMax, ddr5Note),
                    new("VDDIO (MEM)", tr.T("The processor's memory I/O"), tr.T("EXPO value or Auto"), Ddr5VddMax,
                        tr.T("Keep it at or below VDDQ unless a guide for your exact kit says otherwise.")),
                    new("VDDG / VDD MISC", tr.T("Internal fabric rails"), "Auto", 1.30, tr.T("Leave on Auto for everything on this page.")),
                ];
            case RamPlatformKind.Am4:
            case RamPlatformKind.Am4Legacy:
                return
                [
                    new("VSOC (CPU SoC)", tr.T("The processor's memory controller and Infinity Fabric"), "1.05–1.10 V", Am4VsocMax,
                        tr.T("1.10–1.15 V is enough for FCLK 1800–1900 on most chips.")),
                    new("VDIMM (DRAM)", tr.T("The memory chips"), "1.35 V (XMP)", ic == DramIc.SamsungBDie ? Ddr4VdimmMaxBDie : Ddr4VdimmMax,
                        ic == DramIc.SamsungBDie
                            ? tr.T("Samsung B-die takes 1.50 V for daily use with airflow over the modules.")
                            : tr.F("{0:0.00} V for daily use on most DDR4 chips.", Ddr4VdimmMax)),
                    new("VDDG CCD / IOD", tr.T("The links between the processor's parts"), "0.90–0.95 V / 1.00–1.05 V", 1.10,
                        tr.T("Leave on Auto unless FCLK is above 1800 MHz. Both must stay at least 0.04 V below VSOC.")),
                ];
            case RamPlatformKind.IntelLga1700Ddr5:
                return
                [
                    new("VDD / VDDQ (DRAM)", tr.T("The memory chips"), tr.T("XMP value (1.25–1.40 V)"), Ddr5VddMax, ddr5Note),
                    new("VDD2 (CPU memory controller)", tr.T("The processor's memory I/O (also called VDDQ TX)"), "1.20–1.35 V", Lga1700Vdd2Max,
                        tr.T("Raise in 0.02 V steps only when a faster speed will not train.")),
                    new("VCCSA (System Agent)", tr.T("The processor's memory controller"), "1.15–1.25 V", Lga1700VccsaMax,
                        tr.T("More is not always better: too much System Agent voltage can also stop the memory training.")),
                ];
            case RamPlatformKind.IntelLga1700Ddr4:
                return
                [
                    new("VDIMM (DRAM)", tr.T("The memory chips"), "1.35 V (XMP)", ic == DramIc.SamsungBDie ? Ddr4VdimmMaxBDie : Ddr4VdimmMax,
                        ic == DramIc.SamsungBDie
                            ? tr.T("Samsung B-die takes 1.50 V for daily use with airflow over the modules.")
                            : tr.F("{0:0.00} V for daily use on most DDR4 chips.", Ddr4VdimmMax)),
                    new("VCCSA (System Agent)", tr.T("The processor's memory controller"), "1.10–1.25 V", Lga1700VccsaMax,
                        tr.T("Gear 1 above DDR4-3600 may need a little more, never above the ceiling.")),
                ];
            case RamPlatformKind.IntelArrowLake:
                return
                [
                    new("VDD / VDDQ (DRAM)", tr.T("The memory chips"), tr.T("XMP value (1.25–1.40 V)"), Ddr5VddMax, ddr5Note),
                    new("VDD2 (CPU memory controller)", tr.T("The processor's memory I/O"), "1.20–1.35 V", ArrowLakeVdd2Max,
                        tr.T("Intel 200S Boost's limit, which keeps your warranty.")),
                    new("VCCSA (System Agent)", tr.T("The processor's memory controller"), "1.10–1.20 V", ArrowLakeVccsaMax,
                        tr.T("Intel 200S Boost's limit, which keeps your warranty.")),
                ];
            default:
                return [];
        }
    }
}
