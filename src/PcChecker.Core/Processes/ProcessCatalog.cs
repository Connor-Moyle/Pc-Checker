namespace PcChecker.Core.Processes;

/// <summary>
/// Plain-English "What is this?" for well-known Windows, driver and app processes. Lookups use the normalized file
/// name (<see cref="Normalize"/>), so "C:\Windows\System32\svchost.exe", "svchost.exe" and "SvcHost" all match.
/// </summary>
public static class ProcessCatalog
{
    public const string Windows = "Windows";
    public const string Security = "Windows security";
    public const string Microsoft = "Microsoft app";
    public const string Graphics = "Graphics driver";
    public const string Audio = "Audio driver";
    public const string Chipset = "Chipset driver";
    public const string Launcher = "Game launcher";
    public const string Browser = "Browser";
    public const string Chat = "Chat and calls";
    public const string Hardware = "Hardware utility";
    public const string Media = "Media";
    public const string Tools = "Tools";

    private static readonly ProcessCatalogEntry[] Entries =
    [
        // Core Windows processes
        new("system", "Windows kernel", "The heart of Windows. It runs drivers and handles hardware and memory. Always running.", Windows, true),
        new("registry", "Registry", "Holds the Windows registry in memory so settings load quickly. Part of Windows.", Windows, true),
        new("memcompression", "Memory compression", "Windows squeezes rarely used memory here so less goes to the page file. High memory use here is normal.", Windows, true),
        new("secure system", "Secure System", "Runs Windows' virtualisation-based security (Core isolation, Credential Guard). Part of Windows.", Windows, true),
        new("smss", "Session Manager", "Starts the Windows sessions when the PC boots. Part of Windows.", Windows, true),
        new("csrss", "Client Server Runtime", "Handles console windows and parts of the desktop for each session. Ending it crashes Windows.", Windows, true),
        new("wininit", "Windows Start-Up", "Starts the core services when Windows boots. Ending it crashes Windows.", Windows, true),
        new("winlogon", "Windows Logon", "Handles signing in and out, the lock screen and Ctrl+Alt+Del. Part of Windows.", Windows, true),
        new("services", "Services and Controller app", "Starts, stops and watches Windows services. Ending it crashes Windows.", Windows, true),
        new("lsass", "Local Security Authority", "Checks passwords and sign-ins and enforces security policy. Ending it restarts the PC.", Windows, true),
        new("lsaiso", "Credential Guard", "Keeps sign-in secrets isolated from the rest of Windows when Credential Guard is on.", Windows, true),
        new("svchost", "Service Host", "Runs Windows services. Many copies are normal; each hosts one or more services.", Windows, true),
        new("dwm", "Desktop Window Manager", "Draws the desktop, windows, transparency and animations. Some GPU use is normal.", Windows, true),
        new("explorer", "Windows Explorer", "The taskbar, Start button, desktop and File Explorer windows.", Windows, true),
        new("fontdrvhost", "Font Driver Host", "Loads fonts in a sandbox so a broken font cannot harm Windows.", Windows, true),
        new("sihost", "Shell Infrastructure Host", "Handles parts of the desktop such as notifications and the action centre.", Windows, true),
        new("taskhostw", "Host Process for Windows Tasks", "Runs scheduled tasks and background jobs for Windows and apps.", Windows, true),
        new("ctfmon", "Text input (CTF Loader)", "Supports keyboard layouts, the touch keyboard, handwriting and speech input.", Windows, true),
        new("runtimebroker", "Runtime Broker", "Checks that Store apps only use the permissions you gave them (camera, location…).", Windows, true),
        new("searchhost", "Windows Search", "The search box and search window on the taskbar.", Windows, true),
        new("searchindexer", "Windows Search Indexer", "Builds the index that makes searching files and email fast. Busy for a while after big file changes.", Windows, true),
        new("searchprotocolhost", "Windows Search helper", "Reads files for the search index. Comes and goes.", Windows, true),
        new("searchfilterhost", "Windows Search filter", "Extracts text from files for the search index. Comes and goes.", Windows, true),
        new("startmenuexperiencehost", "Start menu", "Shows the Start menu.", Windows, true),
        new("shellexperiencehost", "Windows Shell Experience", "Shows taskbar pop-ups such as the clock, volume and network panels.", Windows, true),
        new("textinputhost", "Text input panel", "The touch keyboard, emoji panel and clipboard history.", Windows, true),
        new("applicationframehost", "Application Frame Host", "Draws the window frames of Store apps such as Settings and Calculator.", Windows, true),
        new("systemsettings", "Settings", "The Windows Settings app.", Windows, true),
        new("lockapp", "Lock screen", "Shows the lock screen. Sleeps in the background while you are signed in.", Windows, true),
        new("useroobebroker", "Out-of-box experience broker", "Finishes Windows setup steps for your account (the 'Let's finish setting up' screens).", Windows, true),
        new("spoolsv", "Print Spooler", "Queues print jobs and talks to printers.", Windows, true),
        new("audiodg", "Windows audio engine", "Mixes and processes sound, including enhancements from the audio driver. Some CPU use while sound plays is normal.", Windows, true),
        new("conhost", "Console Window Host", "Draws the window of command-line programs. One per console program.", Windows, true),
        new("dllhost", "COM Surrogate", "Runs add-ons such as thumbnail makers in a separate process so they cannot crash Explorer.", Windows, true),
        new("wmiprvse", "WMI Provider Host", "Answers questions about hardware and settings for Windows and apps (PC Checker uses it too). Busy while something asks a lot.", Windows, true),
        new("wudfhost", "User-mode driver host", "Runs drivers for devices such as sensors, fingerprint readers and phones outside the kernel.", Windows, true),
        new("dashost", "Device Association host", "Pairs and connects devices such as Bluetooth and network devices.", Windows, true),
        new("backgroundtaskhost", "Background Task Host", "Runs short background jobs for Store apps and widgets.", Windows, true),
        new("tiworker", "Windows Modules Installer Worker", "Installs Windows updates and optional features. Heavy CPU and disk use while updating is normal.", Windows, true),
        new("trustedinstaller", "Windows Modules Installer", "Manages Windows updates and system files.", Windows, true),
        new("usocoreworker", "Update Orchestrator worker", "Checks for and prepares Windows updates.", Windows, true),
        new("mousocoreworker", "Update Orchestrator worker", "Checks for and prepares Windows updates.", Windows, true),
        new("compattelrunner", "Compatibility Telemetry", "Collects compatibility data for Windows updates. Runs now and then, sometimes with high disk use.", Windows, true),
        new("werfault", "Windows Error Reporting", "Collects details after a program crashed.", Windows, true),
        new("msiexec", "Windows Installer", "Installs, repairs or removes .msi programs.", Windows, true),
        new("cmd", "Command Prompt", "A command line window, or a script that a program started.", Windows, true),
        new("powershell", "Windows PowerShell", "A command line or a script. Apps (and PC Checker) use it to run Windows commands.", Windows, true),
        new("taskmgr", "Task Manager", "The Windows Task Manager.", Windows, true),
        new("sgrmbroker", "System Guard Runtime Monitor", "Checks that Windows has not been tampered with while it runs.", Windows, true),
        new("vmmem", "Virtual machine memory", "Memory and CPU used by a virtual machine, WSL (Linux) or Windows Sandbox.", Windows, true),
        new("vmmemwsl", "WSL virtual machine", "Memory and CPU used by the Windows Subsystem for Linux.", Windows, true),
        new("widgets", "Windows Widgets", "The widgets board (news, weather) on the taskbar.", Microsoft),
        new("widgetservice", "Widgets service", "Keeps Windows widgets up to date.", Microsoft),
        new("phoneexperiencehost", "Phone Link", "Connects your Android phone or iPhone to Windows.", Microsoft),
        new("gamebar", "Xbox Game Bar", "The Win+G overlay for recording, performance and chat in games.", Microsoft),
        new("gamebarftserver", "Xbox Game Bar helper", "Background helper for Xbox Game Bar.", Microsoft),

        // Windows security
        new("msmpeng", "Microsoft Defender Antivirus", "Scans files for malware. High CPU during a scan or after installing software is normal.", Security),
        new("nissrv", "Defender Network Inspection", "Microsoft Defender's check of network traffic for known attacks.", Security),
        new("mpdefendercoreservice", "Defender Core Service", "Keeps Microsoft Defender running and up to date.", Security),
        new("securityhealthservice", "Windows Security Health", "Reports the state of antivirus, firewall and updates to the Windows Security app.", Security, true),
        new("securityhealthsystray", "Windows Security icon", "The shield icon in the notification area.", Security, true),
        new("smartscreen", "Microsoft Defender SmartScreen", "Warns before you run unknown downloads or open unsafe sites.", Security, true),

        // Microsoft apps
        new("msedge", "Microsoft Edge", "The Edge browser. Several processes (tabs, extensions, GPU) are normal.", Browser),
        new("msedgewebview2", "Microsoft Edge WebView2", "Shows web content inside apps such as Widgets, Teams and Outlook.", Microsoft),
        new("microsoftedgeupdate", "Microsoft Edge Update", "Keeps Microsoft Edge and WebView2 up to date.", Microsoft),
        new("onedrive", "Microsoft OneDrive", "Syncs your files with OneDrive cloud storage.", Microsoft),
        new("teams", "Microsoft Teams (classic)", "Chat, calls and meetings.", Chat),
        new("ms-teams", "Microsoft Teams", "Chat, calls and meetings.", Chat),
        new("windowsterminal", "Windows Terminal", "Tabs for Command Prompt, PowerShell and Linux shells.", Tools),
        new("openconsole", "Windows Terminal console", "Draws console programs inside Windows Terminal.", Tools),
        new("code", "Visual Studio Code", "A code editor. Several helper processes are normal.", Tools),
        new("pwsh", "PowerShell 7", "A command line or a script running in PowerShell 7.", Tools),

        // Graphics drivers
        new("nvcontainer", "NVIDIA Container", "Runs NVIDIA driver features such as the overlay, telemetry and the NVIDIA app.", Graphics),
        new("nvdisplay.container", "NVIDIA Display Container", "Hosts the NVIDIA Control Panel and display features of the driver.", Graphics),
        new("nvidia app", "NVIDIA app", "NVIDIA's app for driver updates, game settings and recording.", Graphics),
        new("nvidia overlay", "NVIDIA Overlay", "The in-game overlay of the NVIDIA app (Alt+Z) for recording and performance stats.", Graphics),
        new("nvidia share", "NVIDIA Share", "The in-game overlay of GeForce Experience for recording and screenshots.", Graphics),
        new("nvsphelper64", "NVIDIA ShadowPlay helper", "Captures gameplay for instant replay and recording.", Graphics),
        new("nvidia web helper", "NVIDIA Web Helper", "Background helper of GeForce Experience.", Graphics),
        new("radeonsoftware", "AMD Software: Adrenalin Edition", "AMD's app for driver settings, game profiles and recording.", Graphics),
        new("amdrsserv", "AMD Radeon Settings service", "Background service for AMD Software (recording, overlay, hotkeys).", Graphics),
        new("atieclxx", "AMD External Events client", "Handles display hotkeys and events for the AMD graphics driver.", Graphics),
        new("atiesrxx", "AMD External Events service", "Handles display events for the AMD graphics driver.", Graphics),
        new("amdfendrsr", "AMD Crash Defender", "Collects details when the AMD graphics driver crashes.", Graphics),
        new("amdrssrcext", "AMD Software tray", "The AMD Software icon in the notification area and its hotkeys.", Graphics),
        new("amdow", "AMD Software overlay", "Draws the AMD in-game overlay (Alt+R) and the performance metrics overlay.", Graphics),
        new("cncmd", "AMD Software helper", "A small helper AMD Software uses to apply settings in the background.", Graphics),
        new("amdnoisesuppression", "AMD Noise Suppression", "Removes background noise from your microphone and speakers. Safe to stop if you don't use it.", Audio),
        new("auepmaster", "AMD User Experience Program", "Collects anonymous usage data for AMD. Turn it off in AMD Software → Settings or on the Debloat page.", Graphics),
        new("aueplauncher", "AMD User Experience Program launcher", "Starts the AMD User Experience Program. Turn it off in AMD Software → Settings or on the Debloat page.", Graphics),
        new("amd3dvcachesvc", "AMD 3D V-Cache Performance Optimizer", "Needed for X3D CPUs: keeps games on the cores with the extra cache. Leave it running.", Chipset),
        new("amd3dvcacheuser", "AMD 3D V-Cache Performance Optimizer (user part)", "Needed for X3D CPUs: tells AMD's driver when a game is in front. Leave it running.", Chipset),
        new("amdryzenmaster", "AMD Ryzen Master", "AMD's CPU tuning app (Precision Boost Overdrive, Curve Optimizer, memory).", Hardware),
        new("igfxem", "Intel Graphics module", "Handles display hotkeys and events for Intel graphics.", Graphics),
        new("igfxcuiservice", "Intel Graphics service", "Background service of the Intel graphics control panel.", Graphics),
        new("igcc", "Intel Graphics Command Center", "Intel's app for display and graphics settings.", Graphics),

        // Chipset and audio drivers
        new("lms", "Intel Management Engine (LMS)", "Part of the Intel Management Engine driver used for remote management.", Chipset),
        new("jhi_service", "Intel Dynamic Application Loader", "Part of the Intel Management Engine driver.", Chipset),
        new("rtkauduservice64", "Realtek audio service", "Background service of the Realtek audio driver (jack detection, audio console).", Audio),
        new("nahimicsvc64", "Nahimic audio service", "Sound effects service that ships with many gaming motherboards and laptops.", Audio),

        // Game launchers
        new("steam", "Steam", "Valve's game launcher and store.", Launcher),
        new("steamwebhelper", "Steam web helper", "Draws Steam's store, library and overlay. Several copies are normal.", Launcher),
        new("steamservice", "Steam Client Service", "Installs and updates Steam games that need administrator rights.", Launcher),
        new("epicgameslauncher", "Epic Games Launcher", "Epic's game launcher and store.", Launcher),
        new("epicwebhelper", "Epic Games web helper", "Draws the Epic Games Launcher's store and library.", Launcher),
        new("battle.net", "Battle.net", "Blizzard's game launcher.", Launcher),
        new("eadesktop", "EA app", "Electronic Arts' game launcher.", Launcher),
        new("eabackgroundservice", "EA background service", "Installs and updates games for the EA app.", Launcher),
        new("upc", "Ubisoft Connect", "Ubisoft's game launcher.", Launcher),
        new("galaxyclient", "GOG Galaxy", "GOG's game launcher.", Launcher),
        new("xboxpcapp", "Xbox app", "The Xbox app for PC Game Pass and Microsoft Store games.", Launcher),

        // Browsers
        new("chrome", "Google Chrome", "The Chrome browser. One process per tab and extension is normal.", Browser),
        new("firefox", "Mozilla Firefox", "The Firefox browser. Several processes are normal.", Browser),
        new("brave", "Brave", "The Brave browser. Several processes are normal.", Browser),
        new("opera", "Opera", "The Opera or Opera GX browser. Several processes are normal.", Browser),
        new("vivaldi", "Vivaldi", "The Vivaldi browser. Several processes are normal.", Browser),

        // Chat and media
        new("discord", "Discord", "Voice and text chat. Several processes are normal.", Chat),
        new("slack", "Slack", "Team chat.", Chat),
        new("zoom", "Zoom", "Video meetings.", Chat),
        new("spotify", "Spotify", "Music streaming.", Media),
        new("obs64", "OBS Studio", "Screen recording and live streaming.", Media),
        new("wallpaper64", "Wallpaper Engine", "Animated desktop wallpapers.", Media),
        new("wallpaper32", "Wallpaper Engine", "Animated desktop wallpapers.", Media),

        // Hardware utilities
        new("lghub", "Logitech G HUB", "Settings and lighting for Logitech mice, keyboards and headsets.", Hardware),
        new("lghub_agent", "Logitech G HUB agent", "Background part of G HUB that applies profiles.", Hardware),
        new("lghub_updater", "Logitech G HUB updater", "Keeps G HUB up to date.", Hardware),
        new("icue", "Corsair iCUE", "Lighting, fans and settings for Corsair devices.", Hardware),
        new("corsair.service", "Corsair service", "Background service of Corsair iCUE.", Hardware),
        new("razer synapse service", "Razer Synapse", "Settings and lighting for Razer devices.", Hardware),
        new("razercentralservice", "Razer Central", "Background service of Razer Synapse.", Hardware),
        new("armourycrate.service", "ASUS Armoury Crate service", "Background service for ASUS lighting, fans and drivers.", Hardware),
        new("armourycrate.usersessionhelper", "ASUS Armoury Crate helper", "Applies Armoury Crate settings for your account.", Hardware),
        new("msiafterburner", "MSI Afterburner", "Graphics card overclocking and monitoring.", Hardware),
        new("rtss", "RivaTuner Statistics Server", "Shows frame rate overlays and limits frame rate (often with MSI Afterburner).", Hardware),
        new("steelseriesgg", "SteelSeries GG", "Settings for SteelSeries devices.", Hardware),

        // Other common apps
        new("dropbox", "Dropbox", "Syncs your files with Dropbox cloud storage.", Tools),
        new("acrobat", "Adobe Acrobat", "Views and edits PDF files.", Tools),
        new("acrord32", "Adobe Acrobat Reader", "Views PDF files.", Tools),
        new("adobeipcbroker", "Adobe IPC Broker", "Lets Adobe apps talk to Creative Cloud.", Tools),
        new("pcchecker", "PC Checker", "This app.", Tools),
    ];

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["memory compression"] = "memcompression",
        ["ntoskrnl"] = "system",
        ["opera_gx"] = "opera",
        ["rtkaudioservice64"] = "rtkauduservice64",
    };

    private static readonly Dictionary<string, ProcessCatalogEntry> ByName =
        Entries.ToDictionary(e => e.ExeName, StringComparer.Ordinal);

    /// <summary>Every entry, in catalogue order.</summary>
    public static IReadOnlyList<ProcessCatalogEntry> All => Entries;

    /// <summary>
    /// Lower-case file name without folder, quotes or ".exe": <c>"C:\Program Files\Steam\Steam.EXE"</c> → <c>steam</c>.
    /// Returns an empty string for blank input.
    /// </summary>
    public static string Normalize(string? nameOrPath)
    {
        if (string.IsNullOrWhiteSpace(nameOrPath))
        {
            return string.Empty;
        }

        string text = nameOrPath.Trim().Trim('"').Trim();
        int slash = text.LastIndexOfAny(['\\', '/']);
        if (slash >= 0)
        {
            text = text[(slash + 1)..];
        }

        if (text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            text = text[..^4];
        }

        return text.Trim().ToLowerInvariant();
    }

    /// <summary>The entry for a process name or path, or null when the program is not in the catalogue.</summary>
    public static ProcessCatalogEntry? Lookup(string? nameOrPath)
    {
        string key = Normalize(nameOrPath);
        if (key.Length == 0)
        {
            return null;
        }

        if (Aliases.TryGetValue(key, out var alias))
        {
            key = alias;
        }

        return ByName.GetValueOrDefault(key);
    }

    /// <summary>"What is this?" text: the catalogue description, or a hint built from the publisher when unknown.</summary>
    public static string Describe(ProcessCatalogEntry? entry, string? publisher)
    {
        if (entry is not null)
        {
            return entry.Description;
        }

        return string.IsNullOrWhiteSpace(publisher)
            ? "Not a well-known program. Check the publisher and file location if you do not recognise it."
            : $"A program from {publisher.Trim()}.";
    }
}
