using PcChecker.Core.Localization;

namespace PcChecker.Core.RamTuning;

/// <summary>
/// The memory test tools the page recommends after a BIOS change. PC Checker only links to them and never downloads or
/// runs them. Durations follow the usual community practice (overclock.net and r/overclocking memory guides; the tools'
/// own pages): TestMem5 with anta777's "Extreme" config for 3 cycles, Karhu to 6400–10000 % coverage, an hour of
/// OCCT memory or y-cruncher VT3, and MemTest86 overnight (4 passes).
/// </summary>
public static class RamStabilityTools
{
    public static IReadOnlyList<StabilityTool> All(Translator? tr = null)
    {
        tr ??= Translator.English;
        return
        [
            new("TestMem5 (anta777 Extreme config)",
                tr.T("Free, thorough memory error test. Load the anta777 \"Extreme\" config that comes with it."),
                tr.T("3 cycles, about 1–2 hours"), "https://github.com/CoolCmd/TestMem5", false),
            new("Karhu RAM Test",
                tr.T("Paid, fast and very good at finding memory errors."),
                tr.T("6400 % coverage; 10000 % to be thorough"), "https://www.karhusoftware.com/ramtest/", false),
            new("OCCT (Memory test)",
                tr.T("Free for personal use; also tests the processor's memory controller."),
                tr.T("1 hour"), "https://www.ocbase.com/", false),
            new("y-cruncher (stress test, VT3)",
                tr.T("Free; its VT3 test is good at catching an unstable Infinity Fabric or memory controller."),
                tr.T("1 hour"), "https://www.numberworld.org/y-cruncher/", true),
            new("MemTest86",
                tr.T("Free; starts from a USB stick and tests all memory without Windows running."),
                tr.T("4 passes, best overnight"), "https://www.memtest86.com/", false),
        ];
    }
}
