using System.Text;
using static PcChecker.Core.Shell.SearchText;

namespace PcChecker.Core.Shell;

/// <summary>
/// The short help behind a page's "?" button and F1: what the page does, the safest first step, and the docs section
/// that explains it in full. <paramref name="DocsFile"/> is a file in docs/ and <paramref name="DocsHeading"/> the
/// heading inside it (the link jumps there).
/// </summary>
public sealed record PageHelp(string PageKey, string WhatItDoes, string FirstStep, string DocsFile, string? DocsHeading)
{
    /// <summary>The docs section on GitHub, for example .../blob/main/docs/FEATURES.md#keyboard--mouse.</summary>
    public string DocsUrl => HelpCatalog.DocsUrl(DocsFile, DocsHeading);
}

/// <summary>
/// One help entry per page, keyed by the page's file name in the navigation catalog (the same key quick search and the
/// screenshots use). Plain words, two to four sentences. A test checks that every page in the menu has an entry and
/// that every docs heading exists, so add an entry here when you add a page.
/// </summary>
public static class HelpCatalog
{
    /// <summary>The repository the docs links point at.</summary>
    public const string RepositoryUrl = "https://github.com/Connor-Moyle/Pc-Checker";

    private const string Features = "FEATURES.md";

    public static IReadOnlyList<PageHelp> All { get; } =
    [
        // Home
        new("dashboard",
            T("The Dashboard shows how the PC is doing at a glance: health score, live temperatures, updates, the fullest drive and what needs attention."),
            T("Start with the \"What needs attention\" card. Each item has one button that takes you to the right place."),
            Features, "Dashboard"),
        new("checkup",
            T("Check-up runs every check in one go and turns the results into one to-do list, most important first."),
            T("Press Run check-up, read the list, then tick only what you understand and press Fix selected. Nothing changes until you confirm."),
            Features, "Check-up"),

        // Monitor
        new("temperatures",
            T("Temperatures shows every sensor PC Checker can read, with live graphs and colours for warm and hot parts."),
            T("Just watch: this page never changes anything. If the CPU or motherboard is missing, install PawnIO from the banner."),
            Features, "Temperatures"),
        new("power",
            T("Power shows how much electricity the PC uses now and what that costs, and lets you pick a Windows power plan."),
            T("Read the tiles first. Changing the power plan is safe and you can switch back at any time."),
            Features, "Power"),
        new("history",
            T("History shows what the PC did over the last hour, day, week or month: temperatures, load, power and free space."),
            T("Pick a time range at the top. Recording runs while PC Checker is open or in the tray; turn it off in Settings."),
            Features, "History"),
        new("crashes",
            T("Crashes lists blue screens, sudden shutdowns, graphics driver resets and app crashes from the last 90 days, with what each one means."),
            T("Click a crash to read the likely cause and the first steps. This page only reads; it never changes anything."),
            Features, "Crashes"),

        // Task manager
        new("processes",
            T("Processes lists every running program with its processor, memory, disk and network use, like Task Manager."),
            T("Sort by a column to find what is busy. Ending a task can lose unsaved work, so close the program normally first."),
            Features, "Processes"),
        new("live-usage",
            T("Live usage shows graphs of processor, memory, disk, network and graphics use, updated every second."),
            T("Pick a part on the left to see its details. This page only reads; it never changes anything."),
            Features, "Live usage"),
        new("app-history",
            T("App history shows how much processor time, network data and disk each app used over the last days."),
            T("Pick a date range and sort by a column. This page only reads; Delete usage history in More removes PC Checker's own recording."),
            Features, "App history"),
        new("services",
            T("Services lists the Windows services: small programs that run in the background without a window."),
            T("Leave Windows services alone unless you know what they do. Stopping one is undone by a restart; changing how it starts is not."),
            Features, "Services"),
        new("users",
            T("Users shows everyone signed in to this PC and how much each person is running."),
            T("Read only by default. Signing someone else out closes their programs, so ask them first."),
            Features, "Users"),

        // Hardware
        new("hardware",
            T("Overview lists the parts inside this PC: processor, graphics, memory, drives, motherboard and Windows version."),
            T("Use Copy or Export when someone asks what is in your PC. This page only reads."),
            Features, "Hardware"),
        new("health",
            T("Health scores drives, battery, devices and recent errors, and explains anything that needs attention."),
            T("Press Run health check and read the items marked orange or red. Each one says what to do next."),
            Features, "Health"),
        new("storage",
            T("Storage has tools for each drive: a read-only surface scan, a speed test, and Windows' own check and repair tools."),
            T("Start with the surface scan or the speed test; both only read. The repair tools ask first and may need a restart."),
            Features, "Storage"),
        new("memory",
            T("Memory shows how much RAM is used, which modules are fitted and whether they run at their rated speed."),
            T("Read the warnings card first. Memory tests and page file changes ask before they do anything."),
            Features, "Memory"),
        new("battery",
            T("Battery shows how worn the battery is, how long it lasts and how it was used, from Windows' battery report."),
            T("Read the health tile first. Save the full report if a shop or the maker asks for it."),
            Features, "Battery"),
        new("upgrade-advice",
            T("Upgrade advice says what to upgrade next and what in your current setup costs performance, from your hardware, drives, games and History."),
            T("Do the free fixes first. Then read the top upgrade and why it is there; no prices, so compare them when you buy."),
            Features, "Upgrade advice"),

        // Devices
        new("displays",
            T("Displays shows every screen with its resolution and refresh rate, and lets you change them or save a layout."),
            T("Check the refresh rate first: many screens can run faster than Windows picked. A change you do not keep goes back on its own."),
            Features, "Displays"),
        new("audio",
            T("Audio lists speakers and microphones, sets the default device and controls each program's volume."),
            T("Pick the right default output and input. If sound stops, try Restart audio service."),
            Features, "Audio"),
        new("input",
            T("Keyboard & Mouse shows the mouse speed, pointer precision, polling rate and keyboard repeat settings."),
            T("Measure the polling rate first. Settings you change here can be set back with one click."),
            Features, "Keyboard & Mouse"),

        // Cooling & RGB
        new("fans",
            T("Fans shows every fan PC Checker can read and lets you set curves or fixed speeds where the hardware allows it."),
            T("Leave fans on automatic unless you have a reason. Revert all to automatic hands control back to the BIOS at any time."),
            Features, "Fans"),
        new("lighting",
            T("Lighting controls RGB on motherboards, memory, graphics cards and accessories through the free OpenRGB program."),
            T("Install or start OpenRGB from the status card, then pick a colour. Nothing else on the PC changes."),
            Features, "Lighting"),

        // Apps & Drivers
        new("software",
            T("Apps lists every installed program and Store app, with size, maker and install date, and uninstalls them."),
            T("Sort by size or date to find what you no longer use. Uninstall asks first and uses the program's own uninstaller."),
            Features, "Software"),
        new("leftovers",
            T("Leftovers finds folders, registry keys and tasks that uninstalled programs left behind."),
            T("Run the scan; it only reads. Remove only items marked safe; everything removed is backed up first."),
            Features, "Leftovers"),
        new("drivers",
            T("Drivers checks every device's driver, finds problems and looks for newer drivers from Windows Update and the makers."),
            T("Press Check for updates and install the recommended ones. A restore point is made first."),
            Features, "Drivers"),
        new("old-drivers",
            T("Old drivers finds driver packages that a newer version replaced and drivers for devices that are no longer plugged in."),
            T("Scan first; it only reads. Remove only the packages marked safe; a restore point is made first."),
            Features, "Drivers"),
        new("windows-update",
            T("Windows Update shows waiting updates, update history, pause and active hours, and can uninstall a bad update."),
            T("Check for updates and install the recommended ones. Uninstalling an update asks first and makes a restore point."),
            Features, "Windows Update"),
        new("startup",
            T("Startup lists the programs that start with Windows and estimates how much each one slows sign-in."),
            T("Turn off apps marked High that you do not need straight away. You can turn them back on at any time."),
            Features, "Startup"),
        new("scheduled-tasks",
            T("Scheduled tasks lists everything Windows runs on a timer or at sign-in: updaters, PC maker tools and sometimes unwanted programs."),
            T("Start with the tasks marked with a warning. Turn a task off before deleting it; deleted tasks can be put back from Deleted tasks."),
            Features, "Scheduled tasks"),
        new("windows-features",
            T("Windows Features turns optional parts of Windows on or off, such as Hyper-V, the Linux subsystem or old .NET versions."),
            T("Only change a feature you know you need. Most changes ask for a restart."),
            Features, "Windows Features"),

        // Clean up
        new("cleanup",
            T("Cleanup finds temporary files, caches and other junk that is safe to delete, grouped by category."),
            T("Press Scan, keep the categories marked Safe ticked, then press Clean selected. Files go to the Recycle Bin where possible."),
            Features, "Cleanup"),
        new("disk-space",
            T("Disk space shows what fills each drive: the biggest folders, file types, large and old files, and duplicates."),
            T("Pick a drive and press Scan; it only reads. Anything you delete goes to the Recycle Bin."),
            Features, "Disk space"),
        new("debloat",
            T("Debloat turns off ads, data collection and clutter in Windows and removes pre-installed apps you do not use."),
            T("Pick the Recommended preset, read the list, then press Apply. A restore point is made first and each tweak can be undone."),
            "DEBLOAT.md", null),
        new("registry",
            T("Registry removes leftover entries that point at programs and files that no longer exist. It rarely makes a PC faster."),
            T("Scan, then fix only the categories marked Safe. A backup is saved first and can be restored from this page."),
            "REGISTRY.md", null),

        // Security
        new("security",
            T("Security shows how well this PC is protected: antivirus, firewall, Windows version, encryption and more, each with a traffic light and a score."),
            T("Start with \"Fix these first\". Checking only reads; each fix asks first, and other items open the Windows setting."),
            Features, "Security"),
        new("privacy",
            T("Privacy shows which apps recently used your camera, microphone and location, and how Windows' main privacy settings are set."),
            T("Look for apps marked In use now. Turn off asks first and Undo puts it back."),
            Features, "Privacy"),
        new("backups",
            T("Backups shows your restore points, whether System Protection is on for each drive, Windows' own backups and the copies PC Checker keeps."),
            T("Make sure System Protection is on for the Windows drive and create a restore point. Deleting asks first, and old copies go to the Recycle Bin."),
            Features, "Backups"),

        // Performance
        new("performance",
            T("Speed check looks for what slows this PC down: startup apps, busy programs, low memory, full drives and settings."),
            T("Press Run speed check and work through the findings from the top. Each one explains the fix."),
            Features, "Performance"),
        new("context-menu",
            T("Right-click menu lists the entries programs added to File Explorer's right-click menu, so you can turn off the ones you never use."),
            T("Turn entries off rather than removing anything; Turn on brings them back. Restart File Explorer to see the change."),
            Features, "Right-click menu"),
        new("optimize",
            T("Optimize lists safe, reversible tweaks that make Windows faster or quieter, with a risk label on each."),
            T("Apply the tweaks marked Safe first. Every tweak can be reverted from this page."),
            Features, "Optimize"),
        new("benchmark",
            T("Benchmark runs short tests of the processor, memory, drive and graphics and compares the scores with a reference PC."),
            T("Close games and big programs, then press Run benchmark. Nothing on the PC is changed."),
            Features, "Benchmark"),
        new("overclock",
            T("Overclock reads and tunes graphics card and processor clocks, voltages and power limits, with a stress test."),
            T("This is for experienced users. Start with the stress test and the advisor; Reset to defaults always takes you back."),
            Features, "Overclock"),
        new("ram-tuning",
            T("RAM tuning writes a step-by-step BIOS plan for faster, stable memory, made for your memory, processor and board."),
            T("Do stage 1 and 2 first (BIOS update, XMP or EXPO), then press Check my change. PC Checker never changes BIOS settings itself."),
            Features, "RAM tuning"),

        // Gaming
        new("games",
            T("Games lists every installed game from Steam, Epic, GOG, the EA app, Ubisoft, Battle.net, Riot and Xbox, with size, drive and last played."),
            T("Press Play on a card, or right-click it to verify files or find missing DLLs. Uninstalling is always done by the game's own launcher."),
            Features, "Game library"),
        new("game-ready",
            T("Game ready checks everything that commonly costs gamers performance: refresh rate, memory profile, graphics card slot, Windows gaming settings, power, background apps, mouse and game drives."),
            T("Press Check now and start at the top: the biggest wins come first. Fix all safe items only makes reversible changes, after one confirmation."),
            Features, "Game ready"),
        new("overlay-settings",
            T("Overlay sets up the small in-game display of temperatures, load and frame rate."),
            T("Turn the overlay on and press its hotkey (Ctrl+Shift+O) in a game. It never changes the game."),
            "OVERLAY.md", null),
        new("latency",
            T("Latency measures delays that cause stutter and input lag: driver delays, timer resolution, mouse polling and network ping."),
            T("Press Run latency check and read the score. Fixes are offered one at a time and can be undone."),
            Features, "Latency"),
        new("missing-dlls",
            T("Missing DLLs shows which runtime DLLs old games need, which are missing, and installs the official package that contains them."),
            T("Press Scan a game and pick the game's folder. Install what it lists; every install asks first and comes from the vendor, never from a DLL website."),
            Features, "Missing DLLs"),

        // Network
        new("network",
            T("Network tests your connection: speed, ping, packet loss and Wi-Fi signal, and offers safe Wi-Fi and network tweaks."),
            T("Run the speed test first. Network tweaks ask before they change anything and can be reverted."),
            "NETWORK.md", null),

        // Ask Claude
        new("assistant",
            T("Ask Claude answers questions about this PC in plain words, using the results PC Checker has already read."),
            T("Add your Claude API key in Settings, then ask a question. Claude can suggest changes but never makes them itself."),
            "CLAUDE-INTEGRATION.md", null),

        // Footer
        new("logs",
            T("Logs shows what PC Checker did, with errors and the Action Journal: every change it made and how to undo it."),
            T("Open the Action Journal to see recent changes. Export diagnostics when you report a problem."),
            "LOGGING.md", null),
        new("settings",
            T("Settings sets the theme, language, Advanced mode, restore points, tray, schedules, updates and more."),
            T("Leave \"Create a restore point before making changes\" on. Advanced mode shows riskier tools inside pages."),
            Features, "Settings"),
        new("connections",
            T("Connections shows which programs are talking to the internet or your network right now, and which ones accept connections from other devices."),
            T("Just look first: this page changes nothing unless you close a connection or end a program, and both ask before they act."),
            Features, "Connections"),
        new("hosts",
            T("Hosts file shows the entries Windows checks before looking up a website's address. Malware sometimes adds entries here to redirect or block sites."),
            T("Look for entries marked as possibly unwanted. Every save keeps a backup, and Undo last save puts the previous file back."),
            Features, "Hosts file"),
    ];

    private static readonly Dictionary<string, PageHelp> ByKey = All.ToDictionary(h => h.PageKey, StringComparer.OrdinalIgnoreCase);

    /// <summary>The help for <paramref name="pageKey"/>, or null when there is none.</summary>
    public static PageHelp? Find(string? pageKey) =>
        pageKey is not null && ByKey.TryGetValue(pageKey, out var help) ? help : null;

    /// <summary>A link to <paramref name="docsFile"/> on GitHub, jumping to <paramref name="heading"/> when given.</summary>
    public static string DocsUrl(string docsFile, string? heading)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(docsFile);
        var url = $"{RepositoryUrl}/blob/main/docs/{docsFile}";
        return string.IsNullOrWhiteSpace(heading) ? url : url + "#" + Anchor(heading);
    }

    /// <summary>
    /// The anchor GitHub gives a Markdown heading: lower case, spaces become hyphens, and everything except letters,
    /// digits, hyphens and underscores is dropped ("Keyboard &amp; Mouse" → "keyboard--mouse").
    /// </summary>
    public static string Anchor(string heading)
    {
        ArgumentNullException.ThrowIfNull(heading);
        var sb = new StringBuilder(heading.Length);
        foreach (var c in heading.Trim().ToLowerInvariant())
        {
            if (c == ' ')
            {
                sb.Append('-');
            }
            else if (char.IsLetterOrDigit(c) || c is '-' or '_')
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}
