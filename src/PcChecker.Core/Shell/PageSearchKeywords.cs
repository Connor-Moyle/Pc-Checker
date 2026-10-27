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
        ["performance"] = T("slow slowdown speed scan processes"),
        ["live-usage"] = T("task manager performance graphs cpu usage ram disk activity network gpu utilization"),
        ["optimize"] = T("tweaks faster game mode visual effects"),
        ["overclock"] = T("oc gpu cpu clocks stress test"),
        ["overlay-settings"] = T("fps counter in game osd"),
        ["latency"] = T("dpc input lag ping stutter"),
        ["network"] = T("wifi wi fi internet speed test packet loss dns"),
        ["connections"] = T("tcpview netstat ports open ports listening tcp udp ip address firewall"),
        ["hosts"] = T("hosts file block sites redirect dns etc"),
        ["assistant"] = T("claude ai chat help questions"),
        ["logs"] = T("log journal history diagnostics"),
        ["settings"] = T("options preferences configuration"),
    };

    /// <summary>The page keys that have extra words.</summary>
    public static IEnumerable<string> Keys => Map.Keys;

    /// <summary>The extra words for <paramref name="pageKey"/>, or an empty string.</summary>
    public static string For(string pageKey) => Map.TryGetValue(pageKey, out var words) ? words : string.Empty;
}
