namespace PcChecker.Core.Processes;

/// <summary>One process of the demo list; the service wobbles its values on every refresh.</summary>
internal sealed class DemoProcessTemplate(int pid, int parentPid, string name, string? path, string user, DateTimeOffset start,
    double cpu, double memoryMb, double readKbps = 0, double writeKbps = 0, double gpu = 0,
    string? publisher = "Microsoft Windows", SignatureState signature = SignatureState.Signed, ProcessPriority priority = ProcessPriority.Normal)
{
    public int Pid { get; } = pid;
    public int ParentPid { get; } = parentPid;
    public string Name { get; } = name;
    public string? Path { get; } = path;
    public string User { get; } = user;
    public DateTimeOffset Start { get; } = start;
    public double Cpu { get; } = cpu;
    public double MemoryMb { get; } = memoryMb;
    public double ReadKbps { get; } = readKbps;
    public double WriteKbps { get; } = writeKbps;
    public double Gpu { get; } = gpu;
    public string? Publisher { get; } = publisher;
    public SignatureState Signature { get; } = signature;
    public ProcessPriority Priority { get; set; } = priority;
    public ProcessRunState State { get; set; }
    public bool Efficiency { get; set; }
    public ulong? Affinity { get; set; }
    public string? Window { get; init; }
    public string? Description { get; init; }
    public string? CommandLine { get; init; }
    public string? GpuEngine { get; init; }
    public int? Connections { get; init; }
    public ProcessArchitecture Architecture { get; init; } = ProcessArchitecture.X64;

    public int Session => User is DemoProcessData.User or "DWM-1" or "UMFD-1" || Name is "winlogon.exe" or "csrss.exe" ? 1 : 0;

    public bool Elevated => User is not DemoProcessData.User || Pid == DemoProcessData.OwnPid;

    // Plausible counts: bigger programs keep more threads and handles open.
    public int Threads => 4 + (Pid % 23) + (int)Math.Min(120, MemoryMb / 12);

    public int Handles => 90 + (Pid % 640) + (int)Math.Min(4000, MemoryMb * 2);
}

/// <summary>The demo PC's running programs: the sample gaming desktop with a game, launchers, a browser and Windows.</summary>
internal static class DemoProcessData
{
    public const string User = "Alex";
    public const int OwnPid = 9120;

    private const string SystemUser = "SYSTEM";
    private const string LocalService = "LOCAL SERVICE";
    private const string NetworkService = "NETWORK SERVICE";
    private const string Win = @"C:\Windows\System32\";

    public static List<DemoProcessTemplate> Build(DateTimeOffset boot)
    {
        DateTimeOffset At(double minutesAfterBoot) => boot.AddMinutes(minutesAfterBoot);
        DateTimeOffset Ago(double minutes) => DateTimeOffset.Now.AddMinutes(-minutes);
        const string Steam = @"C:\Program Files (x86)\Steam\";
        const string Chrome = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
        const string Discord = @"C:\Users\Alex\AppData\Local\Discord\app-1.0.9163\Discord.exe";
        const string Spotify = @"C:\Users\Alex\AppData\Roaming\Spotify\Spotify.exe";
        const string Obs = @"C:\Program Files\obs-studio\bin\64bit\obs64.exe";
        const string WebView = @"C:\Program Files (x86)\Microsoft\EdgeWebView\Application\130.0.2849.56\msedgewebview2.exe";
        const string Nvidia = @"C:\Program Files\NVIDIA Corporation\";
        const string Game = @"D:\SteamLibrary\steamapps\common\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe";
        string Q(string path, string options = "") => ("\"" + path + "\" " + options).Trim();

        return
        [
            new(4, 0, "System", null, SystemUser, At(0), 0.3, 0.1, 40, 120, publisher: null),
            new(140, 4, "Registry", null, SystemUser, At(0), 0, 62),
            new(612, 4, "smss.exe", Win + "smss.exe", SystemUser, At(0.1), 0, 1.1),
            new(908, 896, "csrss.exe", Win + "csrss.exe", SystemUser, At(0.2), 0.1, 2.3, priority: ProcessPriority.High),
            new(1004, 896, "wininit.exe", Win + "wininit.exe", SystemUser, At(0.2), 0, 1.6),
            new(1080, 1004, "services.exe", Win + "services.exe", SystemUser, At(0.2), 0.1, 6.4),
            new(1100, 1004, "lsass.exe", Win + "lsass.exe", SystemUser, At(0.2), 0.1, 9.8) { Connections = 2 },
            new(1144, 1060, "winlogon.exe", Win + "winlogon.exe", SystemUser, At(0.3), 0, 3.2),
            new(1236, 1144, "fontdrvhost.exe", Win + "fontdrvhost.exe", "UMFD-1", At(0.3), 0, 2.9),
            new(1512, 1144, "dwm.exe", Win + "dwm.exe", "DWM-1", At(0.3), 1.6, 148, gpu: 6.5, priority: ProcessPriority.High),
            new(2412, 4, "MemCompression", null, SystemUser, At(0.4), 0, 486),
            new(1300, 1080, "svchost.exe", Win + "svchost.exe", SystemUser, At(0.3), 0.2, 24) { CommandLine = Win + "svchost.exe -k DcomLaunch -p" },
            new(1388, 1080, "svchost.exe", Win + "svchost.exe", NetworkService, At(0.3), 0.1, 11) { CommandLine = Win + "svchost.exe -k NetworkService -p", Connections = 9 },
            new(1720, 1080, "svchost.exe", Win + "svchost.exe", LocalService, At(0.4), 0.3, 9.2, readKbps: 4) { CommandLine = Win + "svchost.exe -k LocalServiceNetworkRestricted -p" },
            new(1904, 1080, "svchost.exe", Win + "svchost.exe", SystemUser, At(0.4), 0.1, 31, readKbps: 12, writeKbps: 30) { CommandLine = Win + "svchost.exe -k netsvcs -p", Connections = 4 },
            new(2044, 1080, "svchost.exe", Win + "svchost.exe", LocalService, At(0.5), 0, 7.6),
            new(2280, 1080, "svchost.exe", Win + "svchost.exe", SystemUser, At(0.5), 0.1, 18),
            new(3364, 1080, "svchost.exe", Win + "svchost.exe", User, At(1.2), 0.1, 14),
            new(2816, 1080, "spoolsv.exe", Win + "spoolsv.exe", SystemUser, At(0.6), 0, 8.4),
            new(3120, 1080, "MsMpEng.exe", @"C:\ProgramData\Microsoft\Windows Defender\Platform\4.18.24090.11-0\MsMpEng.exe", SystemUser, At(0.6), 0.8, 212, readKbps: 260, writeKbps: 8) { Description = "Antimalware Service Executable", Connections = 2 },
            new(3188, 1080, "NVDisplay.Container.exe", @"C:\Windows\System32\DriverStore\FileRepository\nvdmi.inf_amd64_1d6c0b6f5b0b7a6e\Display.NvContainer\NVDisplay.Container.exe", SystemUser, At(0.6), 0.1, 21, publisher: "NVIDIA Corporation") { Description = "NVIDIA Container" },
            new(3240, 1080, "audiodg.exe", Win + "audiodg.exe", LocalService, Ago(42), 0.6, 13),
            new(3456, 1080, "ArmouryCrate.Service.exe", @"C:\Program Files\ASUS\ARMOURY CRATE Service\ArmouryCrate.Service.exe", SystemUser, At(0.8), 0.2, 38, publisher: "ASUSTeK COMPUTER INC.") { Description = "Armoury Crate Service", Connections = 1 },
            new(3572, 1080, "CorsairService.exe", @"C:\Program Files\Corsair\CORSAIR iCUE5 Software\CorsairService.exe", SystemUser, At(0.8), 0.1, 26, publisher: "Corsair Memory, Inc.") { Description = "Corsair Service" },
            new(3610, 1080, "SearchIndexer.exe", Win + "SearchIndexer.exe", SystemUser, At(0.9), 0.1, 36, readKbps: 18, writeKbps: 22),
            new(4012, 1144, "sihost.exe", Win + "sihost.exe", User, At(1.0), 0.1, 19),
            new(4136, 4012, "taskhostw.exe", Win + "taskhostw.exe", User, At(1.0), 0, 9.5),
            new(4380, 4300, "explorer.exe", @"C:\Windows\explorer.exe", User, At(1.1), 0.7, 168, readKbps: 6, gpu: 0.4) { Window = "Downloads - File Explorer", Description = "Windows Explorer" },
            new(4520, 1080, "StartMenuExperienceHost.exe", @"C:\Windows\SystemApps\Microsoft.Windows.StartMenuExperienceHost_cw5n1h2txyewy\StartMenuExperienceHost.exe", User, At(1.2), 0, 58) { Efficiency = true },
            new(4664, 1080, "SearchHost.exe", @"C:\Windows\SystemApps\MicrosoftWindows.Client.CBS_cw5n1h2txyewy\SearchHost.exe", User, At(1.2), 0, 94) { Efficiency = true, State = ProcessRunState.Suspended },
            new(4788, 1080, "RuntimeBroker.exe", Win + "RuntimeBroker.exe", User, At(1.3), 0, 12),
            new(4810, 1080, "ctfmon.exe", Win + "ctfmon.exe", User, At(1.3), 0.1, 16),
            new(5020, 1080, "TextInputHost.exe", @"C:\Windows\SystemApps\MicrosoftWindows.Client.CBS_cw5n1h2txyewy\TextInputHost.exe", User, At(1.3), 0, 41),
            new(5148, 4380, "SecurityHealthSystray.exe", Win + "SecurityHealthSystray.exe", User, At(1.4), 0, 3.4),
            new(5300, 4380, "OneDrive.exe", @"C:\Users\Alex\AppData\Local\Microsoft\OneDrive\OneDrive.exe", User, At(1.5), 0.1, 74, readKbps: 3, writeKbps: 2, publisher: "Microsoft Corporation") { Description = "Microsoft OneDrive", Connections = 3, Efficiency = true },
            new(5410, 3188, "NVIDIA app.exe", Nvidia + @"NVIDIA app\CEF\NVIDIA app.exe", User, At(1.5), 0.2, 132, publisher: "NVIDIA Corporation") { Description = "NVIDIA app", Connections = 2 },
            new(5480, 3188, "NVIDIA Overlay.exe", Nvidia + @"NVIDIA app\CEF\NVIDIA Overlay.exe", User, At(1.6), 0.4, 96, gpu: 0.8, publisher: "NVIDIA Corporation") { Description = "NVIDIA Overlay" },
            new(5620, 4380, "lghub.exe", @"C:\Program Files\LGHUB\lghub.exe", User, At(1.6), 0.3, 118, publisher: "Logitech Inc") { Description = "Logitech G HUB", Connections = 2 },
            new(5690, 5620, "lghub_agent.exe", @"C:\Program Files\LGHUB\lghub_agent.exe", User, At(1.6), 0.2, 44, publisher: "Logitech Inc") { Description = "Logitech G HUB Agent" },
            new(5740, 4380, "iCUE.exe", @"C:\Program Files\Corsair\CORSAIR iCUE5 Software\iCUE.exe", User, At(1.7), 0.9, 236, gpu: 0.3, publisher: "Corsair Memory, Inc.") { Description = "iCUE" },

            // Steam with its web helpers; the game it started is an app of its own.
            new(6012, 4380, "steam.exe", Steam + "steam.exe", User, At(1.8), 0.4, 182, readKbps: 20, writeKbps: 6, publisher: "Valve Corp.") { Window = "Steam", Description = "Steam", Connections = 6, Architecture = ProcessArchitecture.X86, CommandLine = Q(Steam + "steam.exe", "-silent") },
            new(6104, 6012, "steamwebhelper.exe", Steam + @"bin\cef\cef.win7x64\steamwebhelper.exe", User, At(1.8), 0.2, 128, publisher: "Valve Corp.") { Description = "Steam Client WebHelper", CommandLine = Q(Steam + @"bin\cef\cef.win7x64\steamwebhelper.exe", "-lang=en_US") },
            new(6188, 6104, "steamwebhelper.exe", Steam + @"bin\cef\cef.win7x64\steamwebhelper.exe", User, At(1.9), 0.3, 214, gpu: 0.6, publisher: "Valve Corp.") { Description = "Steam Client WebHelper", CommandLine = Q(Steam + @"bin\cef\cef.win7x64\steamwebhelper.exe", "--type=gpu-process"), GpuEngine = "GPU 0 - 3D" },
            new(6230, 6104, "steamwebhelper.exe", Steam + @"bin\cef\cef.win7x64\steamwebhelper.exe", User, At(1.9), 0.1, 46, publisher: "Valve Corp.") { Description = "Steam Client WebHelper", CommandLine = Q(Steam + @"bin\cef\cef.win7x64\steamwebhelper.exe", "--type=renderer"), Connections = 4 },

            // Discord: the main window process and its helpers.
            new(6420, 4380, "Discord.exe", Discord, User, At(2.0), 0.6, 168, publisher: "Discord Inc.") { Window = "#general | Gaming Friends - Discord", Description = "Discord", CommandLine = Q(Discord) },
            new(6488, 6420, "Discord.exe", Discord, User, At(2.0), 0.3, 92, gpu: 1.2, publisher: "Discord Inc.") { Description = "Discord", CommandLine = Q(Discord, "--type=gpu-process"), GpuEngine = "GPU 0 - 3D" },
            new(6512, 6420, "Discord.exe", Discord, User, At(2.0), 0.2, 241, publisher: "Discord Inc.") { Description = "Discord", CommandLine = Q(Discord, "--type=renderer"), Connections = 5 },
            new(6540, 6420, "Discord.exe", Discord, User, At(2.1), 0.1, 38, writeKbps: 15, publisher: "Discord Inc.") { Description = "Discord", CommandLine = Q(Discord, "--type=utility --utility-sub-type=audio.mojom.AudioService"), Connections = 2 },

            // Chrome with four tabs open.
            new(7020, 4380, "chrome.exe", Chrome, User, Ago(95), 0.8, 312, readKbps: 30, writeKbps: 60, publisher: "Google LLC") { Window = "YouTube - Google Chrome", Description = "Google Chrome", CommandLine = Q(Chrome) },
            new(7064, 7020, "chrome.exe", Chrome, User, Ago(95), 0.4, 168, gpu: 2.1, publisher: "Google LLC") { Description = "Google Chrome", CommandLine = Q(Chrome, "--type=gpu-process --gpu-preferences=WAAAAAAAAAA"), GpuEngine = "GPU 0 - Video Decode" },
            new(7101, 7020, "chrome.exe", Chrome, User, Ago(94), 0.1, 54, publisher: "Google LLC") { Description = "Google Chrome", CommandLine = Q(Chrome, "--type=utility --utility-sub-type=network.mojom.NetworkService"), Connections = 31 },
            new(7188, 7020, "chrome.exe", Chrome, User, Ago(80), 1.1, 284, publisher: "Google LLC") { Description = "Google Chrome", CommandLine = Q(Chrome, "--type=renderer --renderer-client-id=7") },
            new(7240, 7020, "chrome.exe", Chrome, User, Ago(41), 0.3, 196, publisher: "Google LLC") { Description = "Google Chrome", CommandLine = Q(Chrome, "--type=renderer --renderer-client-id=12") },
            new(7266, 7020, "chrome.exe", Chrome, User, Ago(12), 0.2, 122, publisher: "Google LLC") { Description = "Google Chrome", CommandLine = Q(Chrome, "--type=renderer --renderer-client-id=19") },
            new(7290, 7020, "chrome.exe", Chrome, User, Ago(90), 0, 38, publisher: "Google LLC") { Description = "Google Chrome", CommandLine = Q(Chrome, "--type=renderer --extension-process --renderer-client-id=5"), Efficiency = true },

            new(7512, 1080, "msedgewebview2.exe", WebView, User, At(2.4), 0.1, 88, publisher: "Microsoft Corporation") { Description = "Microsoft Edge WebView2", Efficiency = true },
            new(7540, 7512, "msedgewebview2.exe", WebView, User, At(2.4), 0, 46, publisher: "Microsoft Corporation") { Description = "Microsoft Edge WebView2", Efficiency = true },
            new(7580, 7512, "msedgewebview2.exe", WebView, User, At(2.5), 0, 64, publisher: "Microsoft Corporation") { Description = "Microsoft Edge WebView2", Efficiency = true },
            new(8020, 1080, "Widgets.exe", @"C:\Program Files\WindowsApps\MicrosoftWindows.Client.WebExperience_524.24900.0.0_x64__cw5n1h2txyewy\Dashboard\Widgets.exe", User, At(2.6), 0, 33, publisher: "Microsoft Corporation") { Description = "Widgets", Efficiency = true },

            // Spotify, paused by the user from this page.
            new(8410, 4380, "Spotify.exe", Spotify, User, Ago(70), 0.4, 182, publisher: "Spotify AB") { Window = "Spotify Premium", Description = "Spotify", CommandLine = Q(Spotify), State = ProcessRunState.Suspended, Connections = 4 },
            new(8432, 8410, "Spotify.exe", Spotify, User, Ago(70), 0.2, 96, publisher: "Spotify AB") { Description = "Spotify", CommandLine = Q(Spotify, "--type=gpu-process"), State = ProcessRunState.Suspended },
            new(8455, 8410, "Spotify.exe", Spotify, User, Ago(70), 0.3, 141, publisher: "Spotify AB") { Description = "Spotify", CommandLine = Q(Spotify, "--type=renderer"), State = ProcessRunState.Suspended },

            // OBS Studio, frozen while it writes a recording.
            new(8600, 4380, "obs64.exe", Obs, User, Ago(52), 9.6, 612, writeKbps: 9800, gpu: 7.5, publisher: "OBS Project") { Window = "OBS 30.2.3 - Profile: Untitled - Scenes: Untitled", Description = "OBS Studio", CommandLine = Q(Obs, "--startreplaybuffer"), State = ProcessRunState.NotResponding, GpuEngine = "GPU 0 - Video Encode" },

            new(8840, 6012, "Cyberpunk2077.exe", Game, User, Ago(37), 21.4, 7860, readKbps: 4200, writeKbps: 180, gpu: 92, publisher: "CD PROJEKT S.A.") { Window = "Cyberpunk 2077 (C) 2020 by CD Projekt RED", Description = "Cyberpunk 2077", CommandLine = Q(Game, "-launcher-skip"), Connections = 3, GpuEngine = "GPU 0 - 3D", Priority = ProcessPriority.AboveNormal },
            new(8866, 8840, "CrashReporter.exe", @"D:\SteamLibrary\steamapps\common\Cyberpunk 2077\bin\x64\CrashReporter\CrashReporter.exe", User, Ago(37), 0, 9, publisher: "CD PROJEKT S.A.") { Description = "REDEngine crash reporter" },
            new(8920, 4380, "dlhelper.exe", @"C:\Users\Alex\AppData\Local\Temp\dl_3f2a\dlhelper.exe", User, Ago(25), 0.4, 18, writeKbps: 40, publisher: null, signature: SignatureState.Unsigned) { Connections = 1 },
            new(OwnPid, 4380, "PcChecker.exe", @"C:\Tools\PcChecker\PcChecker.exe", User, Ago(3), 1.2, 182, gpu: 0.9, publisher: null, signature: SignatureState.Unsigned) { Window = "PC Checker", Description = "PC Checker" },
        ];
    }
}
