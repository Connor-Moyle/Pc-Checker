namespace PcChecker.Core.Cleanup;

/// <summary>The "app must be closed" guard: which program each category belongs to and how to recognise it running.</summary>
public static partial class CleanupRules
{
    private static readonly string[] OfficeApps = ["WINWORD", "EXCEL", "POWERPNT", "OUTLOOK", "ONENOTE", "MSACCESS", "MSPUB", "VISIO"];

    private static readonly ProcessMatch[] SteamProcesses = Names("steam", "steamwebhelper");
    private static readonly ProcessMatch[] NvidiaInstallers = [new("setup", @"\NVIDIA"), new("*NVIDIA*Install*"), new("NVIDIA app*Update*")];

    private static readonly Dictionary<string, AppRule> BrowserRules = new(StringComparer.OrdinalIgnoreCase)
    {
        ["browser.chrome.cache"] = new("Google Chrome", [new("chrome", @"\Google\Chrome")]),
        ["browser.edge.cache"] = new("Microsoft Edge", [new("msedge", @"\Microsoft\Edge")]),
        ["browser.brave.cache"] = new("Brave", Names("brave")),
        ["browser.firefox.cache"] = new("Mozilla Firefox", Names("firefox")),
        // opera.exe runs both Opera and Opera GX; the install folder tells them apart.
        ["browser.opera.cache"] = new("Opera", [new("opera", @"\Opera\"), new("opera", @"\Opera beta\"), new("opera", @"\Opera developer\"), new("opera", @"\Opera Air\")]),
        ["browser.operagx.cache"] = new("Opera GX", [new("opera", @"\Opera GX\")]),
        ["browser.vivaldi.cache"] = new("Vivaldi", Names("vivaldi")),
        // Chromium also runs as chrome.exe.
        ["browser.chromium.cache"] = new("Chromium", [new("chrome", @"\Chromium\")]),
        ["browser.waterfox.cache"] = new("Waterfox", Names("waterfox")),
        ["browser.librewolf.cache"] = new("LibreWolf", Names("librewolf")),
        ["browser.floorp.cache"] = new("Floorp", Names("floorp")),
        ["browser.zen.cache"] = new("Zen Browser", Names("zen")),
        ["browser.arc.cache"] = new("Arc", Names("Arc")),
        // Yandex runs as browser.exe, a name other programs use too.
        ["browser.yandex.cache"] = new("Yandex Browser", [new("browser", @"\Yandex\")]),
    };

    /// <summary>
    /// The app (or apps) that must be closed before each category is scanned or cleaned. Matched on process name plus,
    /// where several programs share a name (chrome.exe, opera.exe, browser.exe, Agent.exe, setup.exe), a piece of the
    /// image path. A scheduled cleanup skips such a category instead of failing.
    /// </summary>
    public static IReadOnlyDictionary<string, AppRule> AppProcesses { get; } = BuildAppRules();

    /// <summary>The browser part of <see cref="AppProcesses"/>.</summary>
    public static IReadOnlyDictionary<string, AppRule> BrowserProcesses => BrowserRules;

    /// <summary>Every process name pattern any app rule cares about (so the process list is read once).</summary>
    public static IReadOnlyList<string> AllWatchedProcessNames { get; } =
        AppProcesses.Values.SelectMany(r => r.Processes).Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>True when a process name (with or without ".exe") matches any watched pattern.</summary>
    public static bool IsWatchedProcess(string processName)
    {
        var name = StripExe(processName ?? string.Empty);
        return name.Length > 0 && AllWatchedProcessNames.Any(p => CleanupSafety.MatchesPattern(name, p));
    }

    /// <summary>
    /// True when <paramref name="process"/> matches: the name matches (wildcards allowed) and, when the rule names a
    /// piece of the path, the image path contains it. A process whose path could not be read counts as a match.
    /// </summary>
    public static bool ProcessMatches(ProcessMatch match, RunningProcess process)
    {
        ArgumentNullException.ThrowIfNull(match);
        ArgumentNullException.ThrowIfNull(process);
        if (!CleanupSafety.MatchesPattern(StripExe(process.Name), match.Name))
        {
            return false;
        }

        if (string.IsNullOrEmpty(match.PathContains) || string.IsNullOrWhiteSpace(process.ImagePath))
        {
            return true;
        }

        return process.ImagePath.Replace('/', '\\').Contains(match.PathContains, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Returns a skip reason when the app a category belongs to is running, otherwise null.</summary>
    public static string? AppRunningReason(string categoryId, IEnumerable<RunningProcess> running)
    {
        if (string.IsNullOrEmpty(categoryId) || !AppProcesses.TryGetValue(categoryId, out var rule))
        {
            return null;
        }

        var list = (running ?? []).ToList();
        if (!rule.Processes.Any(m => list.Any(p => ProcessMatches(m, p))))
        {
            return null;
        }

        var close = rule.CloseHint ?? "Close it";
        return $"Skipped: {rule.App} is running. {close} and scan again to clean this.";
    }

    /// <summary>Same as <see cref="AppRunningReason(string, IEnumerable{RunningProcess})"/> for bare process names (paths unknown).</summary>
    public static string? AppRunningReason(string categoryId, IEnumerable<string> runningProcessNames) =>
        AppRunningReason(categoryId, (runningProcessNames ?? []).Select(n => new RunningProcess(n)));

    /// <summary>Same as <see cref="AppRunningReason(string, IEnumerable{string})"/>; kept for the browser cards.</summary>
    public static string? BrowserRunningReason(string categoryId, IEnumerable<string> runningProcessNames) =>
        AppRunningReason(categoryId, runningProcessNames);

    private static ProcessMatch[] Names(params string[] names) => names.Select(n => new ProcessMatch(n)).ToArray();

    private static string StripExe(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    private static Dictionary<string, AppRule> BuildAppRules()
    {
        var anyBrowser = new AppRule("A web browser", BrowserRules.Values.SelectMany(r => r.Processes).Distinct().ToArray(), "Close every browser");
        var office = new AppRule("Microsoft Office", Names(OfficeApps));
        const string Tray = "Exit it from the system tray";
        return new Dictionary<string, AppRule>(BrowserRules, StringComparer.OrdinalIgnoreCase)
        {
            ["browser.cookies"] = anyBrowser,
            ["browser.history"] = anyBrowser,
            ["browser.session"] = anyBrowser,
            ["app.thunderbird.cache"] = new("Thunderbird", Names("thunderbird")),
            ["app.discord.cache"] = new("Discord", Names("Discord", "DiscordPTB", "DiscordCanary"), "Quit it from the system tray"),
            ["app.teams.cache"] = new("Microsoft Teams", Names("ms-teams", "Teams"), "Quit it from the system tray"),
            ["app.spotify.cache"] = new("Spotify", Names("Spotify")),
            ["app.office.cache"] = office,
            ["app.office.filecache"] = office,
            ["privacy.office.recent"] = office,
            ["app.vscode.cache"] = new("Visual Studio Code", [new("Code", @"\Microsoft VS Code")]),
            ["app.slack.cache"] = new("Slack", Names("slack"), "Quit it from the system tray"),
            ["app.zoom.logs"] = new("Zoom", Names("Zoom")),
            ["app.obs.logs"] = new("OBS Studio", Names("obs64", "obs32")),
            ["app.adobe.mediacache"] = new("An Adobe video app", Names("Adobe Premiere Pro", "AfterFX", "Adobe Media Encoder")),
            ["game.steam.cache"] = new("Steam", SteamProcesses, Tray),
            ["game.steam.depotcache"] = new("Steam", SteamProcesses, Tray),
            ["game.steam.downloading"] = new("Steam", SteamProcesses, Tray),
            ["game.steam.shadercache"] = new("Steam", SteamProcesses, Tray),
            ["game.epic.cache"] = new("Epic Games Launcher", Names("EpicGamesLauncher", "EpicWebHelper"), Tray),
            ["game.battlenet.cache"] = new("Battle.net", [new("Battle.net"), new("Agent", @"\Battle.net\")], Tray),
            ["game.ea.cache"] = new("The EA app", Names("EADesktop", "EABackgroundService"), Tray),
            ["game.ubisoft.cache"] = new("Ubisoft Connect", Names("UbisoftConnect", "upc"), Tray),
            ["game.ubisoft.installers"] = new("Ubisoft Connect", Names("UbisoftConnect", "upc"), Tray),
            ["game.gog.cache"] = new("GOG Galaxy", Names("GalaxyClient"), Tray),
            ["amd.software.cache"] = new("AMD Software", Names("RadeonSoftware", "AMDRSServ"), Tray),
            ["nvidia.app.cache"] = new("The NVIDIA app", Names("NVIDIA app", "NVIDIA Overlay")),
            ["driver.leftovers.amd"] = new("An AMD installer", [new("AMDInstallManager"), new("RadeonInstaller"), new("AMD_Chipset*"), new("setup", @"\AMD\")], "Wait for it to finish"),
            ["driver.leftovers.nvidia"] = new("An NVIDIA installer", NvidiaInstallers, "Wait for it to finish"),
            ["driver.downloads.nvidia"] = new("An NVIDIA installer", NvidiaInstallers, "Wait for it to finish"),
            ["driver.leftovers.intel"] = new("An Intel installer", [new("setup", @"\Intel"), new("Installer", @"\Intel"), new("*Intel*Install*")], "Wait for it to finish"),
            ["rdp.bitmap.cache"] = new("Remote Desktop", Names("mstsc")),
            ["windows.installer.temp"] = new("Windows Installer", Names("msiexec"), "Wait for the install to finish"),
            ["windows.installer.patchcache"] = new("Windows Installer", Names("msiexec"), "Wait for the install to finish"),
            ["dev.npm.cache"] = new("Node.js", Names("node", "yarn")),
            ["dev.pip.cache"] = new("Python", Names("pip", "python", "pythonw")),
            ["dev.nuget.http"] = new("Visual Studio or the .NET SDK", Names("devenv", "dotnet", "MSBuild")),
            ["dev.nuget.packages"] = new("Visual Studio or the .NET SDK", Names("devenv", "dotnet", "MSBuild")),
            ["dev.java.cache"] = new("Java", Names("java", "javaw", "javaws", "jp2launcher")),
            ["dev.dotnet.temp"] = new("IIS", Names("w3wp", "iisexpress"), "Stop it"),
            ["dev.gradle.cache"] = new("Java (Gradle)", Names("java", "javaw", "gradle")),
            ["dev.maven.repository"] = new("Java (Maven)", Names("java", "javaw", "mvn")),
            ["dev.go.cache"] = new("Go", Names("go")),
            ["dev.cargo.cache"] = new("Rust (Cargo)", Names("cargo", "rustc")),
        };
    }
}
