using PcChecker.Core.Drivers.Platform;
using PcChecker.Core.Localization;

namespace PcChecker.Core.RamTuning;

/// <summary>A BIOS setting the plan refers to.</summary>
public enum BiosSetting
{
    MemoryProfile,
    MemoryFrequency,
    Timings,
    MemoryVoltage,
    SocVoltage,
    Fclk,
    UclkMode,
    MemoryContextRestore,
    GearMode,
    IntelDefaults,
    BiosUpdate,
}

/// <summary>
/// Where the memory settings live in each board maker's BIOS. The XMP/EXPO path and the BIOS flash tool are the
/// makers' documented names (ASUS "Ai Tweaker > Ai Overclock Tuner", "Extreme Tweaker" on ROG boards, EZ Flash; MSI "OC >
/// Extreme Memory Profile (XMP)", M-Flash; GIGABYTE "Tweaker > Extreme Memory Profile (X.M.P.)", Q-Flash; ASRock "OC
/// Tweaker > DRAM Configuration > Load XMP/EXPO Setting", Instant Flash). Other settings move between BIOS versions, so
/// the page names the overclocking tab and the setting's standard AMD/Intel name instead of a full path. Pure.
/// </summary>
public static class BiosMenuPaths
{
    /// <summary>The board maker's overclocking tab ("Ai Tweaker", "OC"…), or null for an unknown maker.</summary>
    public static string? OverclockTab(PcMaker maker, string? boardName) => maker switch
    {
        PcMaker.Asus => IsRog(boardName) ? "Extreme Tweaker" : "Ai Tweaker",
        PcMaker.Msi => "OC",
        PcMaker.Gigabyte => "Tweaker",
        PcMaker.AsRock => "OC Tweaker",
        _ => null,
    };

    /// <summary>How to get into the BIOS's full menus on this board.</summary>
    public static string EnterBios(PcMaker maker, Translator? tr = null)
    {
        tr ??= Translator.English;
        return maker switch
        {
            PcMaker.Asus => tr.T("Restart and press Delete (or F2) while the ASUS logo shows, then F7 for Advanced Mode."),
            PcMaker.Msi => tr.T("Restart and press Delete while the MSI logo shows, then F7 for Advanced mode."),
            PcMaker.Gigabyte => tr.T("Restart and press Delete while the GIGABYTE logo shows, then F2 for Advanced Mode."),
            PcMaker.AsRock => tr.T("Restart and press Delete or F2 while the ASRock logo shows, then F6 for Advanced Mode."),
            _ => tr.T("Restart and press Delete or F2 while the maker's logo shows (Windows can also do it: Settings > System > Recovery > Advanced startup > UEFI Firmware Settings)."),
        };
    }

    /// <summary>The path to a setting on this board, or a plain hint when the maker is unknown.</summary>
    public static string For(BiosSetting setting, PcMaker maker, string? boardName, RamPlatformKind platform, Translator? tr = null)
    {
        tr ??= Translator.English;
        string? tab = OverclockTab(maker, boardName);
        bool am5 = platform == RamPlatformKind.Am5;
        bool am4 = platform is RamPlatformKind.Am4 or RamPlatformKind.Am4Legacy;

        if (setting == BiosSetting.MemoryProfile)
        {
            return maker switch
            {
                PcMaker.Asus => $"{tab} > Ai Overclock Tuner > " + (am5 ? "EXPO I" : am4 ? "D.O.C.P." : "XMP I"),
                PcMaker.Msi => am4 ? "OC > A-XMP" : am5 ? "OC > Extreme Memory Profile (XMP/EXPO)" : "OC > Extreme Memory Profile (XMP)",
                PcMaker.Gigabyte => "Tweaker > Extreme Memory Profile (X.M.P.)" + (am5 ? " / EXPO" : string.Empty),
                PcMaker.AsRock => "OC Tweaker > DRAM Configuration > " + (am5 ? "Load EXPO Setting" : "Load XMP Setting"),
                _ => tr.T("Look for XMP, EXPO, DOCP or \"Memory Profile\" on the overclocking or memory page."),
            };
        }

        if (setting == BiosSetting.BiosUpdate)
        {
            return maker switch
            {
                PcMaker.Asus => "Tool > ASUS EZ Flash 3 Utility",
                PcMaker.Msi => "M-Flash",
                PcMaker.Gigabyte => "Q-Flash",
                PcMaker.AsRock => "Tool > Instant Flash",
                _ => tr.T("Your maker's BIOS flash tool (see the board's manual)."),
            };
        }

        if (setting == BiosSetting.MemoryContextRestore)
        {
            // AMD's own menu (AGESA) is the same on every AM5 board; some boards repeat the option on their OC page.
            return "Advanced > AMD CBS > UMC Common Options > DDR Options > DDR Memory Features > Memory Context Restore";
        }

        if (setting == BiosSetting.IntelDefaults)
        {
            return maker == PcMaker.Asus
                ? $"{tab} > Performance Preferences > Intel Default Settings"
                : tr.T("Look for \"Intel Default Settings\" (or \"Intel Baseline Profile\") on the first BIOS screen or the overclocking page.");
        }

        string name = setting switch
        {
            BiosSetting.MemoryFrequency => am5 || am4 ? "Memory Frequency" : "DRAM Frequency",
            BiosSetting.Timings => maker == PcMaker.Asus ? "DRAM Timing Control" : maker == PcMaker.Gigabyte ? "Advanced Memory Settings" : maker == PcMaker.Msi ? "Advanced DRAM Configuration" : "DRAM Timing Configuration",
            BiosSetting.MemoryVoltage => platform == RamPlatformKind.Am4 || platform == RamPlatformKind.Am4Legacy || platform == RamPlatformKind.IntelLga1700Ddr4 ? "DRAM Voltage" : "DRAM VDD / VDDQ Voltage",
            BiosSetting.SocVoltage => am5 || am4 ? "VDDSOC (CPU SoC) Voltage" : "CPU System Agent (VCCSA) Voltage",
            BiosSetting.Fclk => "FCLK Frequency",
            BiosSetting.UclkMode => "UCLK DIV1 MODE",
            BiosSetting.GearMode => "Gear Mode",
            _ => setting.ToString(),
        };

        return tab is null ? tr.F("The overclocking page (often called OC, Tweaker or Overclocking): \"{0}\"", name) : $"{tab} > {name}";
    }

    private static bool IsRog(string? boardName) =>
        boardName?.Contains("ROG", StringComparison.OrdinalIgnoreCase) == true && !boardName.Contains("STRIX", StringComparison.OrdinalIgnoreCase);
}
