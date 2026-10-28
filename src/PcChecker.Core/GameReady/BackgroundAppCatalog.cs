using PcChecker.Core.Crashes;
using PcChecker.Core.Fans;
using static PcChecker.Core.GameReady.GameReadyText;

namespace PcChecker.Core.GameReady;

/// <summary>One running process as the background-app check needs it.</summary>
public sealed record RunningProcess(string Name, long MemoryBytes, double DiskBytesPerSecond);

/// <summary>
/// Sorts running programs into the groups that are known to cost frame rate or cause stutter in games: in-game
/// overlays (names from the Crashes page's <see cref="OverlayHelp"/>), RGB and motherboard suites (from the Fans page's
/// <see cref="FanConflictCatalog"/>), browsers with many tabs and updaters that are downloading. Matching is on process
/// names, without case or ".exe". Pure.
/// </summary>
public static class BackgroundAppCatalog
{
    /// <summary>A browser with this many processes (roughly one per tab or extension) counts as "many tabs".</summary>
    public const int ManyBrowserProcesses = 15;

    /// <summary>A browser using this much memory counts as heavy.</summary>
    public const long HeavyBrowserBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>An updater reading or writing faster than this is treated as downloading or installing.</summary>
    public const double BusyDiskBytesPerSecond = 2 * 1024 * 1024;

    private static readonly (string Process, string Owner)[] Overlays =
    [
        ("discord", "Discord overlay"),
        ("rtss", "RivaTuner Statistics Server (MSI Afterburner overlay)"),
        ("obs64", "OBS Studio game capture"),
        ("obs32", "OBS Studio game capture"),
        ("nvidiaoverlay", "NVIDIA overlay / ShadowPlay capture"),
        ("overwolf", "Overwolf overlay"),
        ("gamebar", "Xbox Game Bar"),
        ("medal", T("Medal clip recorder")),
    ];

    /// <summary>The <see cref="FanConflictCatalog"/> products that are RGB or motherboard suites (not sensor tools).</summary>
    private static readonly HashSet<string> RgbProducts = new(StringComparer.Ordinal)
    {
        "ASUS Armoury Crate",
        "Corsair iCUE",
        "NZXT CAM",
        "MSI Center / Dragon Center",
        "Gigabyte Control Center / RGB Fusion / SIV",
        "ASRock Polychrome / Phantom Gaming Tuning",
        "SignalRGB",
    };

    private static readonly (string Process, string Name)[] BrowserNames =
    [
        ("chrome", "Google Chrome"),
        ("msedge", "Microsoft Edge"),
        ("firefox", "Firefox"),
        ("opera", "Opera"),
        ("operagx", "Opera GX"),
        ("brave", "Brave"),
        ("vivaldi", "Vivaldi"),
    ];

    private static readonly (string Process, string Name)[] Updaters =
    [
        ("tiworker", "Windows Update"),
        ("mousocoreworker", "Windows Update"),
        ("trustedinstaller", "Windows Update"),
        ("googleupdate", "Google Update"),
        ("microsoftedgeupdate", "Microsoft Edge Update"),
        ("onedrive", "OneDrive"),
        ("dropbox", "Dropbox"),
    ];

    public static GameReadyApps Classify(IEnumerable<RunningProcess> processes)
    {
        ArgumentNullException.ThrowIfNull(processes);
        var list = processes.Where(p => !string.IsNullOrWhiteSpace(p.Name)).Select(p => (Key: FanConflictCatalog.Normalize(p.Name), Process: p)).ToList();

        var overlays = Overlays.Where(o => list.Any(p => p.Key == o.Process)).Select(o => o.Owner).Distinct().ToList();
        var rgb = FanConflictCatalog.Match(list.Select(p => p.Process.Name)).Select(a => a.Product).Where(RgbProducts.Contains).ToList();

        var browsers = new List<(string, int, long)>();
        foreach (var (process, name) in BrowserNames)
        {
            var mine = list.Where(p => p.Key == process).ToList();
            if (mine.Count > 0)
            {
                browsers.Add((name, mine.Count, mine.Sum(p => p.Process.MemoryBytes)));
            }
        }

        var busy = Updaters.Where(u => list.Any(p => p.Key == u.Process && p.Process.DiskBytesPerSecond >= BusyDiskBytesPerSecond))
            .Select(u => u.Name).Distinct().ToList();
        return new GameReadyApps(overlays, rgb, browsers, busy);
    }

    /// <summary>True when a browser has many tabs open or uses a lot of memory.</summary>
    public static bool IsHeavyBrowser(int processes, long memoryBytes) => processes >= ManyBrowserProcesses || memoryBytes >= HeavyBrowserBytes;
}

/// <summary>Pure checks on the installed memory modules.</summary>
public static class MemoryKitRules
{
    /// <summary>
    /// True when the modules do not look like one kit: different part numbers, sizes or rated speeds. Mixed modules
    /// often refuse to run their XMP/EXPO speed together. Modules without a part number are compared on size and speed.
    /// </summary>
    public static bool IsMixed(IEnumerable<Hardware.MemoryModuleInfo> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        var list = modules.ToList();
        if (list.Count < 2)
        {
            return false;
        }

        var parts = list.Select(m => Storage.DriveSerial.Normalize(m.PartNumber)).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var sizes = list.Select(m => m.CapacityBytes).Where(c => c is > 0).Distinct().Count();
        var speeds = list.Select(m => m.SpeedMhz).Where(s => s is > 0).Distinct().Count();
        return parts > 1 || sizes > 1 || speeds > 1;
    }
}
