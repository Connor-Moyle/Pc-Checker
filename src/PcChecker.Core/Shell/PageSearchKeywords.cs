using static PcChecker.Core.Shell.SearchText;

namespace PcChecker.Core.Shell;

/// <summary>
/// Extra words that find a page in quick search ("uninstall" finds Apps). Keyed by the page key from the navigation
/// catalog; a page without an entry is still found by its title and section.
/// </summary>
public static class PageSearchKeywords
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.Ordinal)
    {
        ["dashboard"] = T("home overview score summary"),
        ["checkup"] = T("check up checkup to do fix everything maintenance tune up"),
        ["temperatures"] = T("sensors heat cpu gpu temp hot thermal"),
        ["power"] = T("watts energy battery power plan"),
        ["hardware"] = T("devices specs system information cpu gpu motherboard"),
        ["health"] = T("smart errors problems crashes score"),
        ["crashes"] = T("blue screen bsod stop code bugcheck minidump crash dump freeze sudden shutdown restart app crash hang driver reset tdr"),
        ["storage"] = T("disk drive ssd hdd benchmark chkdsk trim"),
        ["memory"] = T("ram xmp expo page file"),
        ["battery"] = T("laptop wear cycles capacity battery report charge"),
        ["displays"] = T("monitor screen resolution refresh rate hdr brightness"),
        ["audio"] = T("sound speakers microphone volume"),
        ["input"] = T("mouse keyboard pointer polling rate dpi"),
        ["fans"] = T("fan curve cooling speed rpm"),
        ["lighting"] = T("rgb leds openrgb colours colors"),
        ["software"] = T("programs uninstall installed apps"),
        ["leftovers"] = T("uninstall leftovers traces remnants orphaned folders"),
        ["drivers"] = T("driver updates device manager"),
        ["old-drivers"] = T("unused drivers driver store leftovers"),
        ["startup"] = T("boot autostart services fast startup"),
        ["scheduled-tasks"] = T("task scheduler schtasks updater autostart jobs triggers taskschd"),
        ["windows-features"] = T("optional features capabilities inbox apps"),
        ["cleanup"] = T("junk temp files cache free space"),
        ["debloat"] = T("bloatware privacy telemetry ads copilot tweaks"),
        ["context-menu"] = T("context menu right click shell extensions send to show more options classic menu"),
        ["registry"] = T("leftover entries regedit"),
        ["backups"] = T("restore point system restore system protection shadow copies vss file history onedrive backup system image"),
        ["performance"] = T("slow slowdown speed scan processes"),
        ["live-usage"] = T("task manager performance graphs cpu usage ram disk activity network gpu utilization"),
        ["app-history"] = T("task manager app history cpu time data usage network metered bandwidth srum per app"),
        ["optimize"] = T("tweaks faster game mode visual effects"),
        ["overclock"] = T("oc gpu cpu clocks stress test"),
        ["games"] = T("game library steam epic gog ea origin ubisoft uplay battle.net blizzard riot xbox game pass installed games play verify shader cache"),
        ["overlay-settings"] = T("fps counter in game osd"),
        ["latency"] = T("dpc input lag ping stutter"),
        ["missing-dlls"] = T("dll missing dll d3dx9 msvcp140 vcruntime xinput directx runtime redistributable visual c++ physx openal xna dotnet"),
        ["network"] = T("wifi wi fi internet speed test packet loss dns"),
        ["connections"] = T("tcpview netstat ports open ports listening tcp udp ip address firewall"),
        ["hosts"] = T("hosts file block sites redirect dns etc"),
        ["security"] = T("antivirus defender virus malware firewall bitlocker encryption tpm secure boot uac smartscreen activation scan threats"),
        ["privacy"] = T("camera webcam microphone mic location tracking spying permissions activity history advertising id"),
        ["assistant"] = T("claude ai chat help questions"),
        ["logs"] = T("log journal history diagnostics"),
        ["settings"] = T("options preferences configuration"),
    };

    /// <summary>The page keys that have extra words.</summary>
    public static IEnumerable<string> Keys => Map.Keys;

    /// <summary>The extra words for <paramref name="pageKey"/>, or an empty string.</summary>
    public static string For(string pageKey) => Map.TryGetValue(pageKey, out var words) ? words : string.Empty;
}
