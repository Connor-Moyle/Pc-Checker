using PcChecker.Core.Localization;

namespace PcChecker.Core.GameReady;

/// <summary>The graphics card, memory, background app, mouse, storage and update checks.</summary>
public static partial class GameReadyRules
{
    /// <summary>A graphics driver older than this many days is worth updating.</summary>
    public const int OldDriverDays = 180;

    /// <summary>A game drive with less free space than this (or <see cref="LowFreePercent"/>) is nearly full.</summary>
    public const long LowFreeBytes = 20L * 1000 * 1000 * 1000;

    public const double LowFreePercent = 10;

    /// <summary>Mouse polling below this is worth raising in the mouse's software.</summary>
    public const int LowPollingHz = 500;

    // ----- graphics card ----------------------------------------------------------------------------------------------

    private static void Graphics(GameReadyFacts facts, Translator tr, List<GameReadyItem> items)
    {
        var discrete = facts.Gpus.Where(g => !g.IsIntegrated).ToList();
        if (facts.Gpus.Count == 0)
        {
            items.Add(new("gpu", GameReadyArea.GraphicsCard, GameReadyStatus.NotChecked, GameReadyImpact.None,
                tr.T("Graphics card could not be checked"), tr.T("The graphics card does most of the work in games."),
                tr.T("Windows did not report a graphics card. See the Hardware page."),
                new GameReadyFix(GameReadyFixKind.OpenPage, tr.T("Open Hardware"), "hardware")));
            return;
        }

        foreach (var gpu in discrete)
        {
            string slug = discrete.Count > 1 ? "-" + Slug(gpu.Name) : string.Empty;
            items.Add(LinkItem("gpu-link" + slug, gpu, tr));
            items.Add(ResizableBarItem("gpu-rebar" + slug, gpu, tr));
        }

        foreach (var gpu in discrete.Count > 0 ? discrete : facts.Gpus.Take(1).ToList())
        {
            if (DriverItem(discrete.Count > 1 ? "gpu-driver-" + Slug(gpu.Name) : "gpu-driver", gpu, facts.Now, tr) is { } driver)
            {
                items.Add(driver);
            }
        }

        if (discrete.Count > 0 && facts.Gpus.Any(g => g.IsIntegrated) && GpuPreferenceItem(facts, tr) is { } preference)
        {
            items.Add(preference);
        }
    }

    private static GameReadyItem LinkItem(string id, GameReadyGpu gpu, Translator tr)
    {
        string why = tr.T("The graphics card talks to the processor over PCI Express. With fewer lanes it gets less bandwidth, which costs frame rate, most of all on cards with 8 GB of video memory or less.");
        var link = gpu.Link;
        string now = PcieLinkRules.Describe(link?.CurrentSpeed, link?.CurrentWidth) ?? "?";
        string max = PcieLinkRules.Describe(link?.MaxSpeed, link?.MaxWidth) ?? "?";
        return PcieLinkRules.Evaluate(link) switch
        {
            PcieLinkVerdict.ReducedWidth => new(id, GameReadyArea.GraphicsCard, GameReadyStatus.Problem, GameReadyImpact.High,
                tr.F("{0} uses x{1} of its x{2} lanes", gpu.Name, link!.CurrentWidth, link.MaxWidth), why,
                tr.F("Now {0}; the card supports {1}. Usually the card is in a lower slot that is wired for fewer lanes, the slot shares its lanes with an M.2 drive, or the card is not pushed fully in. Use the top long slot and check the motherboard manual.", now, max)),
            PcieLinkVerdict.SlowerSpeed => new(id, GameReadyArea.GraphicsCard, GameReadyStatus.Info, GameReadyImpact.None,
                tr.F("{0}: all lanes in use; check the speed under load", gpu.Name), why,
                tr.F("Now {0}; the card supports {1}. Graphics cards slow the link down at the desktop to save power, so this is normal now. Run this check again while a game runs: if it still shows a lower speed, check the PCIe setting in the BIOS.", now, max)),
            PcieLinkVerdict.Full => new(id, GameReadyArea.GraphicsCard, GameReadyStatus.Good, GameReadyImpact.None,
                tr.F("{0} uses all of its PCI Express lanes", gpu.Name), why, now),
            _ => new(id, GameReadyArea.GraphicsCard, GameReadyStatus.NotChecked, GameReadyImpact.None,
                tr.F("PCI Express link of {0} could not be checked", gpu.Name), why,
                tr.T("Windows did not report the link. Tools such as GPU-Z show it as \"Bus interface\".")),
        };
    }

    private static GameReadyItem ResizableBarItem(string id, GameReadyGpu gpu, Translator tr)
    {
        string why = tr.T("Resizable BAR (AMD: Smart Access Memory) lets the processor reach all of the video memory at once instead of through a small window. Supported games gain a few percent.");
        string bios = tr.T("In the BIOS, turn on Above 4G Decoding and Re-Size BAR Support (CSM must be off). NVIDIA only uses it in games its driver has tested.");
        return gpu.ResizableBar switch
        {
            ResizableBarState.On => new(id, GameReadyArea.GraphicsCard, GameReadyStatus.Good, GameReadyImpact.None,
                tr.T("Resizable BAR is on"), why, tr.T("The whole video memory is visible to the processor.")),
            ResizableBarState.Off => new(id, GameReadyArea.GraphicsCard, GameReadyStatus.Suggestion, GameReadyImpact.Medium,
                tr.T("Resizable BAR is off"), why, bios),
            _ => new(id, GameReadyArea.GraphicsCard, GameReadyStatus.NotChecked, GameReadyImpact.None,
                tr.T("Resizable BAR could not be checked"), why, tr.T("See your BIOS: Above 4G Decoding and Re-Size BAR Support.")),
        };
    }

    private static GameReadyItem? DriverItem(string id, GameReadyGpu gpu, DateTimeOffset now, Translator tr)
    {
        if (gpu.DriverDate is not { } date)
        {
            return null;
        }

        int days = (int)Math.Floor((now.Date - date.Date).TotalDays);
        string why = tr.T("New graphics drivers fix crashes and often add performance for recent games.");
        var fix = new GameReadyFix(GameReadyFixKind.OpenPage, tr.T("Check for driver updates"), "drivers");
        return days > OldDriverDays
            ? new(id, GameReadyArea.Updates, GameReadyStatus.Suggestion, GameReadyImpact.Medium,
                tr.F("The graphics driver is {0} months old", Math.Max(1, days / 30)), why,
                tr.F("{0}, driver from {1:d}.", gpu.Name, date), fix)
            : new(id, GameReadyArea.Updates, GameReadyStatus.Good, GameReadyImpact.None,
                tr.T("The graphics driver is recent"), why, tr.F("{0}, driver from {1:d}.", gpu.Name, date));
    }

    private static GameReadyItem? GpuPreferenceItem(GameReadyFacts facts, Translator tr)
    {
        var games = facts.Games.Where(g => !string.IsNullOrWhiteSpace(g.ExePath)).ToList();
        if (games.Count == 0)
        {
            return null;
        }

        string why = tr.T("This PC has two graphics processors. A game that runs on the built-in one instead of the graphics card is several times slower.");
        var powerSaving = games.Where(g => DirectXSettingsText.GpuPreference(g.GpuPreferenceRaw) == DirectXSettingsText.GpuPowerSaving).ToList();
        if (powerSaving.Count > 0)
        {
            return new("gpu-preference", GameReadyArea.GraphicsCard, GameReadyStatus.Problem, GameReadyImpact.High,
                tr.F("{0} games are set to use the built-in graphics", powerSaving.Count), why, NameList(powerSaving.Select(g => g.Name)),
                new GameReadyFix(GameReadyFixKind.GpuPreference, tr.T("Use the graphics card"), Safe: true) { Paths = powerSaving.Select(g => g.ExePath!).ToList() });
        }

        var unset = games.Where(g => DirectXSettingsText.GpuPreference(g.GpuPreferenceRaw) is null or DirectXSettingsText.GpuDefault).ToList();
        if (facts.HasBattery && unset.Count > 0)
        {
            return new("gpu-preference", GameReadyArea.GraphicsCard, GameReadyStatus.Suggestion, GameReadyImpact.Low,
                tr.F("Make sure {0} games use the graphics card", unset.Count), why,
                tr.F("Windows decides for these games. Setting High performance in Settings → Display → Graphics makes sure: {0}", NameList(unset.Select(g => g.Name))),
                new GameReadyFix(GameReadyFixKind.GpuPreference, tr.T("Use the graphics card"), Safe: true) { Paths = unset.Select(g => g.ExePath!).ToList() });
        }

        return new("gpu-preference", GameReadyArea.GraphicsCard, GameReadyStatus.Good, GameReadyImpact.None,
            tr.T("Games use the graphics card"), why, tr.T("No game is set to the built-in graphics."));
    }

    // ----- memory -----------------------------------------------------------------------------------------------------

    private static void Memory(GameReadyFacts facts, Translator tr, List<GameReadyItem> items)
    {
        var fix = new GameReadyFix(GameReadyFixKind.OpenPage, tr.T("Open RAM tuning"), RamTuningPage);
        if (facts.Memory is not { } memory)
        {
            items.Add(new("memory-xmp", GameReadyArea.Memory, GameReadyStatus.NotChecked, GameReadyImpact.None,
                tr.T("Memory speed could not be checked"), XmpWhy(tr), tr.T("See the Memory page."), fix));
            return;
        }

        string profile = memory.MemoryType?.Contains("DDR5", StringComparison.OrdinalIgnoreCase) == true ? "XMP / EXPO" : "XMP / DOCP";
        items.Add(memory.XmpState switch
        {
            Overclocking.XmpEvaluation.Disabled => new("memory-xmp", GameReadyArea.Memory, GameReadyStatus.Problem, GameReadyImpact.High,
                tr.F("Memory runs below its rated speed ({0} is off)", profile), XmpWhy(tr),
                tr.F("Runs at {0} MT/s; the kit is rated for {1} MT/s. Turning the profile on is a one-time BIOS setting; RAM tuning walks you through it.",
                    memory.ConfiguredMhz ?? 0, memory.RatedMhz ?? 0), fix),
            Overclocking.XmpEvaluation.Enabled => new("memory-xmp", GameReadyArea.Memory, GameReadyStatus.Good, GameReadyImpact.None,
                tr.T("Memory runs at its rated speed"), XmpWhy(tr), tr.F("{0} MT/s", memory.ConfiguredMhz ?? 0)),
            _ => new("memory-xmp", GameReadyArea.Memory, GameReadyStatus.NotChecked, GameReadyImpact.None,
                tr.F("Could not tell whether {0} is on", profile), XmpWhy(tr),
                memory.ConfiguredMhz is { } mhz ? tr.F("Runs at {0} MT/s. Compare it with the speed on the memory's label.", mhz) : tr.T("See the Memory page."), fix),
        });

        string channelWhy = tr.T("Two memory sticks work side by side (dual channel) and nearly double the memory bandwidth. Many games lose 10 to 30 percent of their frame rate on one channel.");
        if (memory.ModuleCount == 1)
        {
            items.Add(new("memory-channels", GameReadyArea.Memory, GameReadyStatus.Problem, GameReadyImpact.High,
                tr.T("Only one memory stick: single channel"), channelWhy,
                tr.T("Add a second, identical stick (or replace it with a kit of two)."), fix));
        }
        else if (memory.ModuleCount > 1 && memory.DualChannel == false)
        {
            items.Add(new("memory-channels", GameReadyArea.Memory, GameReadyStatus.Suggestion, GameReadyImpact.Medium,
                tr.T("Memory may not run in dual channel"), channelWhy,
                tr.T("The sticks may be in the wrong slots. Most boards want two sticks in the second and fourth slot (A2 and B2); see the motherboard manual."), fix));
        }
        else if (memory.ModuleCount > 1)
        {
            items.Add(new("memory-channels", GameReadyArea.Memory, GameReadyStatus.Good, GameReadyImpact.None,
                tr.T("Memory runs in dual channel"), channelWhy, tr.F("{0} sticks", memory.ModuleCount)));
        }

        if (memory.MixedKits)
        {
            items.Add(new("memory-mixed", GameReadyArea.Memory, GameReadyStatus.Suggestion, GameReadyImpact.Medium,
                tr.T("Memory sticks come from different kits"), tr.T("Mixed sticks often refuse to run at their rated speed together, or crash when the profile is on."),
                tr.T("They differ in part number, size or rated speed. A matched kit is the safe choice."), fix));
        }
    }

    private static string XmpWhy(Translator tr) =>
        tr.T("Memory runs at a slow, safe speed until its XMP or EXPO profile is turned on in the BIOS. The rated speed often adds 10 percent or more frame rate in processor-heavy games.");

    // ----- background apps --------------------------------------------------------------------------------------------

    private static void BackgroundApps(GameReadyFacts facts, Translator tr, List<GameReadyItem> items)
    {
        var apps = facts.Apps;
        int before = items.Count;
        var processes = new GameReadyFix(GameReadyFixKind.OpenPage, tr.T("Open Processes"), "processes");
        var overlays = apps.Overlays.Select(tr.T).ToList();
        if (overlays.Count >= 2)
        {
            items.Add(new("apps-overlays", GameReadyArea.BackgroundApps, GameReadyStatus.Suggestion, GameReadyImpact.Medium,
                tr.F("{0} programs with in-game overlays are running", overlays.Count),
                tr.T("Each overlay hooks into the game to draw on top of it. Several at once cost frame rate and are a common cause of stutter and crashes."),
                tr.F("Keep the one you use and turn the in-game overlay off in the others: {0}", NameList(overlays)), processes));
        }
        else if (overlays.Count == 1)
        {
            items.Add(new("apps-overlays", GameReadyArea.BackgroundApps, GameReadyStatus.Info, GameReadyImpact.None,
                tr.F("An overlay program is running: {0}", overlays[0]),
                tr.T("One overlay is usually fine. If a game stutters or crashes, turn its in-game overlay off as a test."), overlays[0]));
        }

        if (apps.RgbSuites.Count > 0)
        {
            items.Add(new("apps-rgb", GameReadyArea.BackgroundApps, GameReadyStatus.Suggestion, GameReadyImpact.Low,
                tr.T("RGB and motherboard software is running"),
                tr.T("These suites read sensors and talk to USB devices all the time. On some PCs that causes small, regular stutters."),
                tr.F("Set your lighting once, let the devices remember it, and close the suite while you play: {0}", NameList(apps.RgbSuites)), processes));
        }

        var heavy = apps.Browsers.Where(b => BackgroundAppCatalog.IsHeavyBrowser(b.Processes, b.MemoryBytes)).ToList();
        if (heavy.Count > 0)
        {
            items.Add(new("apps-browser", GameReadyArea.BackgroundApps, GameReadyStatus.Suggestion, GameReadyImpact.Low,
                tr.F("{0} has many tabs open", heavy[0].Browser),
                tr.T("Every open tab uses memory and some keep using the processor and graphics card (videos, live pages)."),
                string.Join(", ", heavy.Select(b => tr.F("{0}: {1} processes, {2:0.0} GB", b.Browser, b.Processes, b.MemoryBytes / 1073741824.0))), processes));
        }

        if (apps.BusyUpdaters.Count > 0)
        {
            items.Add(new("apps-updaters", GameReadyArea.BackgroundApps, GameReadyStatus.Suggestion, GameReadyImpact.Medium,
                tr.F("{0} is downloading or installing", NameList(apps.BusyUpdaters)),
                tr.T("Downloads and installs use the disk, the network and the processor, which shows up as stutter and lag spikes."),
                tr.T("Let it finish before you play, or pause it."), processes));
        }

        if (items.Count == before)
        {
            items.Add(new("apps", GameReadyArea.BackgroundApps, GameReadyStatus.Good, GameReadyImpact.None,
                tr.T("No programs known to cost frame rate are running"),
                tr.T("Overlays, RGB suites, busy browsers and updaters are the usual suspects."), tr.T("Nothing found.")));
        }
    }

    // ----- mouse ------------------------------------------------------------------------------------------------------

    private static void Mouse(GameReadyFacts facts, Translator tr, List<GameReadyItem> items)
    {
        string accelWhy = tr.T("Enhance pointer precision speeds the pointer up when you move the mouse fast, so the same hand movement turns your aim by different amounts. Most gamers turn it off.");
        if (facts.PointerPrecisionOn == true)
        {
            items.Add(new("mouse-acceleration", GameReadyArea.Mouse, GameReadyStatus.Suggestion, GameReadyImpact.Low,
                tr.T("Mouse acceleration is on"), accelWhy, tr.T("Enhance pointer precision is on in the mouse settings."),
                new GameReadyFix(GameReadyFixKind.OptimizationAction, tr.T("Turn off"), PointerPrecisionAction, Safe: true)));
        }
        else if (facts.PointerPrecisionOn == false)
        {
            items.Add(new("mouse-acceleration", GameReadyArea.Mouse, GameReadyStatus.Good, GameReadyImpact.None,
                tr.T("Mouse acceleration is off"), accelWhy, tr.T("Off")));
        }

        string pollWhy = tr.T("The polling rate is how often the mouse reports its position. 1000 Hz means every millisecond; lower rates add a little delay.");
        var test = new GameReadyFix(GameReadyFixKind.MousePollingTest, tr.T("Test my mouse"));
        items.Add(facts.MousePollingHz switch
        {
            null or <= 0 => new("mouse-polling", GameReadyArea.Mouse, GameReadyStatus.Info, GameReadyImpact.None,
                tr.T("Mouse polling rate not measured yet"), pollWhy, tr.T("Press the button and move the mouse in circles for five seconds."), test),
            < LowPollingHz => new("mouse-polling", GameReadyArea.Mouse, GameReadyStatus.Suggestion, GameReadyImpact.Low,
                tr.F("Your mouse reports {0} times a second", facts.MousePollingHz), pollWhy,
                tr.T("Set 1000 Hz (or higher) in the mouse's own software if it offers it."), test with { Label = tr.T("Test again") }),
            _ => new("mouse-polling", GameReadyArea.Mouse, GameReadyStatus.Good, GameReadyImpact.None,
                tr.F("Mouse polling rate: about {0} Hz", facts.MousePollingHz), pollWhy, tr.T("Fast enough for games."), test with { Label = tr.T("Test again") }),
        });

        items.Add(new("latency", GameReadyArea.Mouse, GameReadyStatus.Info, GameReadyImpact.None,
            tr.T("Driver delays, timers and input lag"),
            tr.T("Timer settings (HPET, dynamic tick) and slow drivers can add input lag. Measure them before changing anything."),
            tr.T("The Latency page measures them and offers its own fixes."),
            new GameReadyFix(GameReadyFixKind.OpenPage, tr.T("Open Latency"), "latency")));
    }

    // ----- storage, updates, security ---------------------------------------------------------------------------------

    private static void Storage(GameReadyFacts facts, Translator tr, List<GameReadyItem> items)
    {
        int before = items.Count;
        var hdd = facts.GameDrives.Where(d => d.Media == Core.Storage.DriveMedia.Hdd && d.GameCount > 0).ToList();
        if (hdd.Count > 0)
        {
            items.Add(new("storage-hdd", GameReadyArea.Storage, GameReadyStatus.Suggestion, GameReadyImpact.Medium,
                tr.F("{0} games are on a hard disk", hdd.Sum(d => d.GameCount)),
                tr.T("Hard disks load games several times slower than SSDs, and open-world games stutter while they stream new areas."),
                tr.F("On {0}. Move the games you play most to an SSD with the launcher's own move option.", string.Join(", ", hdd.Select(d => d.Drive))),
                new GameReadyFix(GameReadyFixKind.OpenPage, tr.T("Open Games"), "games")));
        }

        foreach (var drive in facts.GameDrives.Where(d => d.GameCount > 0 && IsLowOnSpace(d.FreeBytes, d.SizeBytes)))
        {
            items.Add(new("storage-space-" + Slug(drive.Drive), GameReadyArea.Storage, GameReadyStatus.Suggestion, GameReadyImpact.Medium,
                tr.F("Game drive {0} is almost full", drive.Drive),
                tr.T("Games need free space for updates and shader caches, and a full SSD writes more slowly."),
                tr.F("{0:0.0} GB free.", (drive.FreeBytes ?? 0) / 1e9),
                new GameReadyFix(GameReadyFixKind.OpenPage, tr.T("Free up space"), "disk-space")));
        }

        if (items.Count == before && facts.GameDrives.Count > 0)
        {
            items.Add(new("storage", GameReadyArea.Storage, GameReadyStatus.Good, GameReadyImpact.None,
                tr.T("Games are on fast drives with free space"),
                tr.T("Games load fastest from an SSD with some free space."),
                string.Join(", ", facts.GameDrives.Select(d => d.Drive))));
        }
    }

    /// <summary>True when a drive has under 20 GB or under 10 % free. Unknown sizes are never "low".</summary>
    public static bool IsLowOnSpace(long? freeBytes, long? sizeBytes)
    {
        if (freeBytes is not { } free || free < 0)
        {
            return false;
        }

        return free < LowFreeBytes || (sizeBytes is > 0 and { } size && free * 100.0 / size < LowFreePercent);
    }

    private static void Updates(GameReadyFacts facts, Translator tr, List<GameReadyItem> items)
    {
        if (!string.IsNullOrWhiteSpace(facts.PendingRestartReason))
        {
            items.Add(new("restart-pending", GameReadyArea.Updates, GameReadyStatus.Suggestion, GameReadyImpact.Medium,
                tr.T("Windows is waiting for a restart"),
                tr.T("Until you restart, half-installed updates keep working in the background and drivers may not be fully loaded."),
                facts.PendingRestartReason!,
                new GameReadyFix(GameReadyFixKind.OpenPage, tr.T("Open Windows Update"), "windows-update")));
        }

        if (facts.MemoryIntegrityOn == true)
        {
            items.Add(new("memory-integrity", GameReadyArea.Security, GameReadyStatus.Info, GameReadyImpact.None,
                tr.T("Memory integrity is on"),
                tr.T("Memory integrity blocks malicious drivers. In some games it costs a few percent of frame rate."),
                tr.T("PC Checker recommends leaving it on: the protection is worth more than a few frames. Some anti-cheat systems even require it."),
                new GameReadyFix(GameReadyFixKind.OpenPage, tr.T("Open Security"), "security")));
        }
    }

    private static string NameList(IEnumerable<string> names)
    {
        var list = names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return list.Count <= 6 ? string.Join(", ", list) : string.Join(", ", list.Take(6)) + ", …";
    }
}
