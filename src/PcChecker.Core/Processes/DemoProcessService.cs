using PcChecker.Core.Common;

namespace PcChecker.Core.Processes;

/// <summary>
/// Demo process list: the sample gaming desktop with a game running, Steam, Discord and Chrome open and the usual
/// Windows and driver processes, plus one unsigned helper in a Temp folder so the "look closer" flag shows. Values
/// wobble on every refresh. Ending and re-prioritising change only the in-memory list.
/// </summary>
public sealed class DemoProcessService : IProcessService
{
    private const string Alex = "Alex";
    private const string SystemUser = "SYSTEM";
    private const string LocalService = "LOCAL SERVICE";
    private const string NetworkService = "NETWORK SERVICE";
    private const string Win = @"C:\Windows\System32\";

    private readonly object _gate = new();
    private readonly Random _random = DemoData.CreateRandom(4071);
    private readonly DateTimeOffset _boot = DateTimeOffset.Now.AddHours(-6).AddMinutes(-12);
    private readonly List<Template> _processes;

    public DemoProcessService()
    {
        _processes = BuildTemplates(_boot);
    }

    public int OwnProcessId => 9120;

    public async Task<ProcessSnapshot> SampleAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 30, 120).ConfigureAwait(false);
        lock (_gate)
        {
            var list = _processes.Select(Wobble).ToList();
            double total = Math.Min(100, list.Sum(p => p.CpuPercent) + DemoData.Jitter(_random, 1.5, 0.5));
            long used = (long)(DemoData.Jitter(_random, 14.6, 0.15) * ByteSize.Gigabyte);
            return new ProcessSnapshot(DateTimeOffset.Now, list, Math.Round(total, 1), used, DemoData.TotalMemoryBytes, true, []);
        }
    }

    /// <summary>Demo values are generated fresh on every sample, so there is no baseline to forget.</summary>
    public void ResetRates()
    {
    }

    public async Task<OperationResult> EndProcessAsync(ProcessInfo process, bool includeChildren, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (CriticalProcessRules.Reason(process.Name, process.Pid, OwnProcessId) is { } reason)
        {
            return OperationResult.Fail(reason);
        }

        await DemoData.DelayAsync(cancellationToken, 150, 400).ConfigureAwait(false);
        lock (_gate)
        {
            var root = _processes.FirstOrDefault(p => p.Pid == process.Pid);
            if (root is null)
            {
                return OperationResult.Ok($"{process.Name} had already closed (demo).");
            }

            var removed = new HashSet<int> { root.Pid };
            if (includeChildren)
            {
                var links = _processes.Select(p => new ProcessLink(p.Pid, p.ParentPid, p.Start, p.Name));
                foreach (var child in ProcessTree.Descendants(root.Pid, root.Start, links))
                {
                    if (!CriticalProcessRules.IsCritical(child.Name, child.Pid, OwnProcessId))
                    {
                        removed.Add(child.Pid);
                    }
                }
            }

            _processes.RemoveAll(p => removed.Contains(p.Pid));
            return OperationResult.Ok(removed.Count > 1
                ? $"{process.Name} and {removed.Count - 1} process(es) it started were ended (demo)."
                : $"{process.Name} was ended (demo).");
        }
    }

    public async Task<OperationResult> SetPriorityAsync(ProcessInfo process, ProcessPriority priority, bool allowRealtime, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (CriticalProcessRules.Reason(process.Name, process.Pid, OwnProcessId) is { } reason)
        {
            return OperationResult.Fail(reason);
        }

        if (!PriorityRules.IsAllowed(priority, allowRealtime))
        {
            return OperationResult.Fail("Realtime priority is only offered in Advanced mode.");
        }

        await DemoData.DelayAsync(cancellationToken, 100, 250).ConfigureAwait(false);
        lock (_gate)
        {
            var target = _processes.FirstOrDefault(p => p.Pid == process.Pid);
            if (target is null)
            {
                return OperationResult.Fail($"{process.Name} is no longer running (demo).");
            }

            string was = PriorityRules.Label(target.Priority);
            target.Priority = priority;
            return OperationResult.Ok($"{process.Name} now runs at {PriorityRules.Label(priority)} priority (was {was}) (demo).");
        }
    }

    public OperationResult OpenFileLocation(ProcessInfo process) =>
        OperationResult.Ok($"Would open the folder of {process?.Name} (demo).");

    public OperationResult SearchOnline(ProcessInfo process) =>
        OperationResult.Ok($"Would search the web for {ProcessCatalog.Normalize(process?.Name)}.exe (demo).");

    private ProcessInfo Wobble(Template t)
    {
        double cpu = t.Cpu <= 0 ? 0 : Math.Max(0, DemoData.Jitter(_random, t.Cpu, Math.Max(0.1, t.Cpu * 0.25)));
        long memory = (long)(Math.Max(0.1, DemoData.Jitter(_random, t.MemoryMb, t.MemoryMb * 0.02)) * ByteSize.Megabyte);
        double read = t.ReadKbps <= 0 ? 0 : Math.Max(0, DemoData.Jitter(_random, t.ReadKbps, t.ReadKbps * 0.5)) * 1024;
        double write = t.WriteKbps <= 0 ? 0 : Math.Max(0, DemoData.Jitter(_random, t.WriteKbps, t.WriteKbps * 0.5)) * 1024;
        double gpu = t.Gpu <= 0 ? 0 : Math.Clamp(DemoData.Jitter(_random, t.Gpu, Math.Max(0.5, t.Gpu * 0.08)), 0, 100);
        var signature = t.Path is null ? SignatureInfo.Unknown : new SignatureInfo(t.Signature, t.Signature == SignatureState.Signed ? t.Publisher : null);
        return new ProcessInfo(t.Pid, t.ParentPid, t.Name, t.Path, t.User, t.Start, Math.Round(cpu, 1), memory, read, write,
            Math.Round(gpu, 1), t.Priority, signature);
    }

    private sealed class Template(int pid, int parentPid, string name, string? path, string user, DateTimeOffset start,
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
    }

    private static List<Template> BuildTemplates(DateTimeOffset boot)
    {
        DateTimeOffset At(double minutesAfterBoot) => boot.AddMinutes(minutesAfterBoot);
        DateTimeOffset Ago(double minutes) => DateTimeOffset.Now.AddMinutes(-minutes);
        const string Steam = @"C:\Program Files (x86)\Steam\";
        const string Chrome = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
        const string Discord = @"C:\Users\Alex\AppData\Local\Discord\app-1.0.9163\Discord.exe";
        const string WebView = @"C:\Program Files (x86)\Microsoft\EdgeWebView\Application\130.0.2849.56\msedgewebview2.exe";
        const string Nvidia = @"C:\Program Files\NVIDIA Corporation\";
        const string Game = @"D:\SteamLibrary\steamapps\common\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe";

        return
        [
            new(4, 0, "System", null, SystemUser, At(0), 0.3, 0.1, 40, 120, publisher: null),
            new(140, 4, "Registry", null, SystemUser, At(0), 0, 62),
            new(612, 4, "smss.exe", Win + "smss.exe", SystemUser, At(0.1), 0, 1.1),
            new(908, 896, "csrss.exe", Win + "csrss.exe", SystemUser, At(0.2), 0.1, 2.3, priority: ProcessPriority.High),
            new(1004, 896, "wininit.exe", Win + "wininit.exe", SystemUser, At(0.2), 0, 1.6),
            new(1080, 1004, "services.exe", Win + "services.exe", SystemUser, At(0.2), 0.1, 6.4),
            new(1100, 1004, "lsass.exe", Win + "lsass.exe", SystemUser, At(0.2), 0.1, 9.8),
            new(1144, 1060, "winlogon.exe", Win + "winlogon.exe", SystemUser, At(0.3), 0, 3.2),
            new(1236, 1144, "fontdrvhost.exe", Win + "fontdrvhost.exe", "UMFD-1", At(0.3), 0, 2.9),
            new(1512, 1144, "dwm.exe", Win + "dwm.exe", "DWM-1", At(0.3), 1.6, 148, gpu: 6.5, priority: ProcessPriority.High),
            new(2412, 4, "MemCompression", null, SystemUser, At(0.4), 0, 486),
            new(1300, 1080, "svchost.exe", Win + "svchost.exe", SystemUser, At(0.3), 0.2, 24),
            new(1388, 1080, "svchost.exe", Win + "svchost.exe", NetworkService, At(0.3), 0.1, 11),
            new(1720, 1080, "svchost.exe", Win + "svchost.exe", LocalService, At(0.4), 0.3, 9.2, readKbps: 4),
            new(1904, 1080, "svchost.exe", Win + "svchost.exe", SystemUser, At(0.4), 0.1, 31, readKbps: 12, writeKbps: 30),
            new(2044, 1080, "svchost.exe", Win + "svchost.exe", LocalService, At(0.5), 0, 7.6),
            new(2280, 1080, "svchost.exe", Win + "svchost.exe", SystemUser, At(0.5), 0.1, 18),
            new(3364, 1080, "svchost.exe", Win + "svchost.exe", Alex, At(1.2), 0.1, 14),
            new(2816, 1080, "spoolsv.exe", Win + "spoolsv.exe", SystemUser, At(0.6), 0, 8.4),
            new(3120, 1080, "MsMpEng.exe", @"C:\ProgramData\Microsoft\Windows Defender\Platform\4.18.24090.11-0\MsMpEng.exe", SystemUser, At(0.6), 0.8, 212, readKbps: 260, writeKbps: 8),
            new(3188, 1080, "NVDisplay.Container.exe", @"C:\Windows\System32\DriverStore\FileRepository\nvdmi.inf_amd64_1d6c0b6f5b0b7a6e\Display.NvContainer\NVDisplay.Container.exe", SystemUser, At(0.6), 0.1, 21, publisher: "NVIDIA Corporation"),
            new(3240, 1080, "audiodg.exe", Win + "audiodg.exe", LocalService, Ago(42), 0.6, 13),
            new(3456, 1080, "ArmouryCrate.Service.exe", @"C:\Program Files\ASUS\ARMOURY CRATE Service\ArmouryCrate.Service.exe", SystemUser, At(0.8), 0.2, 38, publisher: "ASUSTeK COMPUTER INC."),
            new(3572, 1080, "CorsairService.exe", @"C:\Program Files\Corsair\CORSAIR iCUE5 Software\CorsairService.exe", SystemUser, At(0.8), 0.1, 26, publisher: "Corsair Memory, Inc."),
            new(3610, 1080, "SearchIndexer.exe", Win + "SearchIndexer.exe", SystemUser, At(0.9), 0.1, 36, readKbps: 18, writeKbps: 22),
            new(4012, 1144, "sihost.exe", Win + "sihost.exe", Alex, At(1.0), 0.1, 19),
            new(4136, 4012, "taskhostw.exe", Win + "taskhostw.exe", Alex, At(1.0), 0, 9.5),
            new(4380, 4300, "explorer.exe", @"C:\Windows\explorer.exe", Alex, At(1.1), 0.7, 168, readKbps: 6, gpu: 0.4),
            new(4520, 1080, "StartMenuExperienceHost.exe", @"C:\Windows\SystemApps\Microsoft.Windows.StartMenuExperienceHost_cw5n1h2txyewy\StartMenuExperienceHost.exe", Alex, At(1.2), 0, 58),
            new(4664, 1080, "SearchHost.exe", @"C:\Windows\SystemApps\MicrosoftWindows.Client.CBS_cw5n1h2txyewy\SearchHost.exe", Alex, At(1.2), 0, 94),
            new(4788, 1080, "RuntimeBroker.exe", Win + "RuntimeBroker.exe", Alex, At(1.3), 0, 12),
            new(4810, 1080, "ctfmon.exe", Win + "ctfmon.exe", Alex, At(1.3), 0.1, 16),
            new(5020, 1080, "TextInputHost.exe", @"C:\Windows\SystemApps\MicrosoftWindows.Client.CBS_cw5n1h2txyewy\TextInputHost.exe", Alex, At(1.3), 0, 41),
            new(5148, 4380, "SecurityHealthSystray.exe", Win + "SecurityHealthSystray.exe", Alex, At(1.4), 0, 3.4),
            new(5300, 4380, "OneDrive.exe", @"C:\Users\Alex\AppData\Local\Microsoft\OneDrive\OneDrive.exe", Alex, At(1.5), 0.1, 74, readKbps: 3, writeKbps: 2, publisher: "Microsoft Corporation"),
            new(5410, 3188, "NVIDIA app.exe", Nvidia + @"NVIDIA app\CEF\NVIDIA app.exe", Alex, At(1.5), 0.2, 132, publisher: "NVIDIA Corporation"),
            new(5480, 3188, "NVIDIA Overlay.exe", Nvidia + @"NVIDIA app\CEF\NVIDIA Overlay.exe", Alex, At(1.6), 0.4, 96, gpu: 0.8, publisher: "NVIDIA Corporation"),
            new(5620, 4380, "lghub.exe", @"C:\Program Files\LGHUB\lghub.exe", Alex, At(1.6), 0.3, 118, publisher: "Logitech Inc"),
            new(5690, 5620, "lghub_agent.exe", @"C:\Program Files\LGHUB\lghub_agent.exe", Alex, At(1.6), 0.2, 44, publisher: "Logitech Inc"),
            new(5740, 4380, "iCUE.exe", @"C:\Program Files\Corsair\CORSAIR iCUE5 Software\iCUE.exe", Alex, At(1.7), 0.9, 236, gpu: 0.3, publisher: "Corsair Memory, Inc."),
            new(6012, 4380, "steam.exe", Steam + "steam.exe", Alex, At(1.8), 0.4, 182, readKbps: 20, writeKbps: 6, publisher: "Valve Corp."),
            new(6104, 6012, "steamwebhelper.exe", Steam + @"bin\cef\cef.win7x64\steamwebhelper.exe", Alex, At(1.8), 0.2, 128, publisher: "Valve Corp."),
            new(6188, 6104, "steamwebhelper.exe", Steam + @"bin\cef\cef.win7x64\steamwebhelper.exe", Alex, At(1.9), 0.3, 214, gpu: 0.6, publisher: "Valve Corp."),
            new(6230, 6104, "steamwebhelper.exe", Steam + @"bin\cef\cef.win7x64\steamwebhelper.exe", Alex, At(1.9), 0.1, 46, publisher: "Valve Corp."),
            new(6420, 4380, "Discord.exe", Discord, Alex, At(2.0), 0.6, 168, publisher: "Discord Inc."),
            new(6488, 6420, "Discord.exe", Discord, Alex, At(2.0), 0.3, 92, gpu: 1.2, publisher: "Discord Inc."),
            new(6512, 6420, "Discord.exe", Discord, Alex, At(2.0), 0.2, 241, publisher: "Discord Inc."),
            new(6540, 6420, "Discord.exe", Discord, Alex, At(2.1), 0.1, 38, writeKbps: 15, publisher: "Discord Inc."),
            new(7020, 4380, "chrome.exe", Chrome, Alex, Ago(95), 0.8, 312, readKbps: 30, writeKbps: 60, publisher: "Google LLC"),
            new(7064, 7020, "chrome.exe", Chrome, Alex, Ago(95), 0.4, 168, gpu: 2.1, publisher: "Google LLC"),
            new(7101, 7020, "chrome.exe", Chrome, Alex, Ago(94), 0.1, 54, publisher: "Google LLC"),
            new(7188, 7020, "chrome.exe", Chrome, Alex, Ago(80), 1.1, 284, publisher: "Google LLC"),
            new(7240, 7020, "chrome.exe", Chrome, Alex, Ago(41), 0.3, 196, publisher: "Google LLC"),
            new(7266, 7020, "chrome.exe", Chrome, Alex, Ago(12), 0.2, 122, publisher: "Google LLC"),
            new(7512, 1080, "msedgewebview2.exe", WebView, Alex, At(2.4), 0.1, 88, publisher: "Microsoft Corporation"),
            new(7540, 7512, "msedgewebview2.exe", WebView, Alex, At(2.4), 0, 46, publisher: "Microsoft Corporation"),
            new(7580, 7512, "msedgewebview2.exe", WebView, Alex, At(2.5), 0, 64, publisher: "Microsoft Corporation"),
            new(8020, 1080, "Widgets.exe", @"C:\Program Files\WindowsApps\MicrosoftWindows.Client.WebExperience_524.24900.0.0_x64__cw5n1h2txyewy\Dashboard\Widgets.exe", Alex, At(2.6), 0, 33, publisher: "Microsoft Corporation"),
            new(8840, 6012, "Cyberpunk2077.exe", Game, Alex, Ago(37), 21.4, 7860, readKbps: 4200, writeKbps: 180, gpu: 92, publisher: "CD PROJEKT S.A."),
            new(8920, 4380, "dlhelper.exe", @"C:\Users\Alex\AppData\Local\Temp\dl_3f2a\dlhelper.exe", Alex, Ago(25), 0.4, 18, writeKbps: 40, publisher: null, signature: SignatureState.Unsigned),
            new(9120, 4380, "PcChecker.exe", @"C:\Tools\PcChecker\PcChecker.exe", Alex, Ago(3), 1.2, 182, gpu: 0.9, publisher: null, signature: SignatureState.Unsigned, priority: ProcessPriority.Normal),
        ];
    }
}
