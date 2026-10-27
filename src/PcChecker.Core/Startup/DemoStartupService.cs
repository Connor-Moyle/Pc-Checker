using PcChecker.Core.Common;
using PcChecker.Core.Performance;

namespace PcChecker.Core.Startup;

/// <summary>Demo startup data: a typical gaming PC with a few too many launchers at logon. Pure .NET; keeps its own in-memory state.</summary>
public sealed class DemoStartupService : IStartupService, IRestartCanceller
{
    private readonly object _gate = new();
    private bool _restartPending;
    private readonly List<StartupItem> _items =
    [
        new("Steam", @"""C:\Program Files (x86)\Steam\steam.exe"" -silent", @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", true, "Valve Corporation", "Registry"),
        new("Discord", @"C:\Users\Alex\AppData\Local\Discord\Update.exe --processStart Discord.exe", @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", true, "Discord Inc.", "Registry"),
        new("Epic Games Launcher", @"""C:\Program Files (x86)\Epic Games\Launcher\Portal\Binaries\Win32\EpicGamesLauncher.exe"" -silent", @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", true, "Epic Games, Inc.", "Registry"),
        new("Spotify", @"""C:\Users\Alex\AppData\Roaming\Spotify\Spotify.exe"" --autostart --minimized", @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", false, "Spotify AB", "Registry"),
        new("NVIDIA App", @"""C:\Program Files\NVIDIA Corporation\NVIDIA app\CEF\NVIDIA app.exe"" --startup", @"HKLM\Software\Microsoft\Windows\CurrentVersion\Run", true, "NVIDIA Corporation", "Registry"),
        new("Armoury Crate", @"""C:\Program Files\ASUS\ARMOURY CRATE Service\ArmouryCrate.UserSessionHelper.exe""", @"Task Scheduler: \ASUS\ArmouryCrate", true, "ASUSTeK COMPUTER INC.", "Task"),
        new("OneDrive", @"""C:\Users\Alex\AppData\Local\Microsoft\OneDrive\OneDrive.exe"" /background", @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", true, "Microsoft Corporation", "Registry"),
        new("Logitech G HUB", @"""C:\Program Files\LGHUB\lghub.exe"" --background", @"HKLM\Software\Microsoft\Windows\CurrentVersion\Run", true, "Logitech", "Registry"),
        new("iCUE", @"""C:\Program Files\Corsair\CORSAIR iCUE5 Software\iCUE.exe"" --autorun", @"HKLM\Software\Microsoft\Windows\CurrentVersion\Run", true, "Corsair", "Registry"),
        new("Adobe Updater", @"""C:\Program Files (x86)\Common Files\Adobe\OOBE\PDApp\UWA\UpdaterStartupUtility.exe""", @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\StartUp", false, "Adobe Inc.", "Folder"),
        new("Microsoft Teams", @"""C:\Users\Alex\AppData\Local\Microsoft\Teams\Update.exe"" --processStart ""Teams.exe"" --process-start-args ""--system-initiated""", @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", false, "Microsoft Corporation", "Registry"),
        new("SecurityHealth", @"%windir%\system32\SecurityHealthSystray.exe", @"HKLM\Software\Microsoft\Windows\CurrentVersion\Run", true, "Microsoft Corporation", "Registry"),
        new("GoogleUpdateTaskMachineCore", @"""C:\Program Files (x86)\Google\Update\GoogleUpdate.exe"" /c", @"Task Scheduler: \GoogleUpdateTaskMachineCore", true, "Google LLC", "Task"),
        new("OneDrive Standalone Update Task", @"%localappdata%\Microsoft\OneDrive\OneDriveStandaloneUpdater.exe", @"Task Scheduler: \OneDrive Standalone Update Task-S-1-5-21-1234", true, "Microsoft Corporation", "Task"),
        new("MicrosoftEdgeUpdateTaskMachineCore", @"C:\Program Files (x86)\Microsoft\EdgeUpdate\MicrosoftEdgeUpdate.exe /c", @"Task Scheduler: \MicrosoftEdgeUpdateTaskMachineCore", true, "Microsoft Corporation", "Task"),
        new("CorsairGamingAudioConfig", @"""C:\Program Files\Corsair\CORSAIR iCUE5 Software\CorsairAudioConfig.exe""", @"Task Scheduler: \Corsair\CorsairGamingAudioConfig", false, "Corsair", "Task"),
    ];

    private readonly List<ServiceInfo> _services =
    [
        new("Audiosrv", "Windows Audio", "Automatic", "Running", @"C:\Windows\System32\svchost.exe -k LocalServiceNetworkRestricted", true, "Manages audio for Windows-based programs."),
        new("Steam Client Service", "Steam Client Service", "Manual", "Stopped", @"C:\Program Files (x86)\Common Files\Steam\steamservice.exe", false, "Steam Client Service monitors and updates Steam content."),
        new("NVDisplay.ContainerLocalSystem", "NVIDIA Display Container LS", "Automatic", "Running", @"C:\Windows\System32\DriverStore\FileRepository\nvdisplay.inf_amd64\Display.NvContainer\NVDisplay.Container.exe", false, "Container service for NVIDIA root features."),
        new("SysMain", "SysMain", "Automatic", "Running", @"C:\Windows\System32\svchost.exe -k LocalSystemNetworkRestricted", true, "Maintains and improves system performance over time."),
        new("WSearch", "Windows Search", "Automatic (Delayed)", "Running", @"C:\Windows\System32\SearchIndexer.exe /Embedding", true, "Provides content indexing and search results."),
        new("ArmouryCrateService", "ARMOURY CRATE Service", "Automatic", "Running", @"C:\Program Files\ASUS\ARMOURY CRATE Service\ArmouryCrate.Service.exe", false, "ASUS Armoury Crate background service."),
        new("RpcSs", "Remote Procedure Call (RPC)", "Automatic", "Running", @"C:\Windows\system32\svchost.exe -k rpcss -p", true, "The RPCSS service is the Service Control Manager for COM and DCOM servers."),
        new("WinDefend", "Microsoft Defender Antivirus Service", "Automatic", "Running", @"C:\ProgramData\Microsoft\Windows Defender\Platform\4.18.24090.11-0\MsMpEng.exe", true, "Helps protect users from malware and other potentially unwanted software."),
        new("Spooler", "Print Spooler", "Automatic", "Running", @"C:\Windows\System32\spoolsv.exe", true, "This service spools print jobs and handles interaction with the printer."),
        new("wuauserv", "Windows Update", "Manual", "Stopped", @"C:\Windows\system32\svchost.exe -k netsvcs -p", true, "Enables the detection, download, and installation of updates for Windows and other programs."),
        new("LGHUBUpdaterService", "LGHUB Updater Service", "Automatic", "Running", @"C:\Program Files\LGHUB\lghub_updater.exe --run-as-service", false, "Keeps Logitech G HUB up to date."),
        new("CorsairService", "Corsair Service", "Automatic", "Running", @"C:\Program Files\Corsair\CORSAIR iCUE5 Software\CorsairService.exe", false, "Corsair iCUE device service."),
        new("DiagTrack", "Connected User Experiences and Telemetry", "Automatic", "Running", @"C:\Windows\System32\svchost.exe -k utcsvc -p", true, "Collects diagnostic data used to improve Windows."),
    ];

    private BootConfig _boot = new(30, "Windows 11", true, true, false, false, DateTimeOffset.Now.AddHours(-6).AddMinutes(-12), 18.4, false);

    public async Task<IReadOnlyList<StartupItem>> GetStartupItemsAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            return _items.ToList();
        }
    }

    public async Task<OperationResult> SetStartupItemEnabledAsync(StartupItem item, bool enabled, CancellationToken cancellationToken)
    {
        if (StartupTaskRules.ToggleRefusal(item) is { } refusal)
        {
            return OperationResult.Fail(refusal);
        }

        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            int i = _items.FindIndex(x => x.Name == item.Name && x.Location == item.Location);
            if (i >= 0)
            {
                _items[i] = _items[i] with { Enabled = enabled };
            }
        }

        return OperationResult.Ok($"{item.Name} will {(enabled ? "start" : "no longer start")} with Windows (demo).");
    }

    public async Task<OperationResult> RemoveStartupItemAsync(StartupItem item, CancellationToken cancellationToken)
    {
        if (StartupTaskRules.RemoveRefusal(item) is { } refusal)
        {
            return OperationResult.Fail(refusal);
        }

        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _items.RemoveAll(x => x.Name == item.Name && x.Location == item.Location);
        }

        return OperationResult.Ok($"{item.Name} removed from startup (demo).");
    }

    public async Task<OperationResult> AddStartupItemAsync(string name, string command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(command))
        {
            return OperationResult.Fail("Give the startup entry a name and the program to run.");
        }

        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            string? exe = StartupCommandParser.ExtractExecutable(command);
            _items.Add(new StartupItem(name.Trim(), command.Trim(), @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", true, StartupPublisherRules.Resolve(null, name, exe), "Registry"));
        }

        return OperationResult.Ok($"{name} added to startup (demo).");
    }

    public async Task<IReadOnlyList<ServiceInfo>> GetAutoStartServicesAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            return _services.ToList();
        }
    }

    public async Task<OperationResult> SetServiceStartModeAsync(string serviceName, string startMode, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        var (allowed, message) = ProtectedServices.CanSetStartMode(serviceName, startMode);
        if (!allowed)
        {
            return OperationResult.Fail(message ?? "Not allowed.");
        }

        string canonical = ServiceStartModes.Normalize(startMode) ?? startMode;
        lock (_gate)
        {
            int i = _services.FindIndex(s => s.Name == serviceName);
            if (i >= 0)
            {
                _services[i] = _services[i] with { StartMode = canonical };
            }
        }

        return OperationResult.Ok($"{serviceName} start mode set to {canonical} (demo)." + (message is null ? string.Empty : " " + message));
    }

    public async Task<BootConfig> GetBootConfigAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        return _boot;
    }

    public async Task<OperationResult> SetBootTimeoutAsync(int seconds, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        _boot = _boot with { TimeoutSeconds = Math.Clamp(seconds, 0, 999) };
        return OperationResult.Ok($"Boot menu timeout set to {_boot.TimeoutSeconds} s (demo).");
    }

    public async Task<OperationResult> SetSafeModeNextBootAsync(bool enabled, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        _boot = _boot with { SafeModeNextBoot = enabled };
        return OperationResult.Ok(enabled ? "Windows will start in Safe Mode next time (demo)." : "Safe Mode next boot cancelled (demo).", rebootRequired: enabled);
    }

    public async Task<OperationResult> SetFastStartupAsync(bool enabled, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        _boot = _boot with { FastStartup = enabled, HibernationEnabled = enabled || _boot.HibernationEnabled };
        return OperationResult.Ok($"Fast startup {(enabled ? "enabled" : "disabled")} (demo).");
    }

    public int RestartDelaySeconds => RestartPrompt.CountdownSeconds;

    public async Task<OperationResult> RestartToUefiAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        _restartPending = true;
        return OperationResult.Ok($"Would restart into the UEFI firmware settings in {RestartDelaySeconds} seconds (demo).", rebootRequired: true);
    }

    public async Task<OperationResult> RestartToAdvancedStartupAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        _restartPending = true;
        return OperationResult.Ok($"Would restart into Advanced startup in {RestartDelaySeconds} seconds (demo).", rebootRequired: true);
    }

    public async Task<OperationResult> CancelPendingRestartAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 50, 150).ConfigureAwait(false);
        bool had = _restartPending;
        _restartPending = false;
        return OperationResult.Ok(had ? "The restart was cancelled (demo)." : "There was no restart to cancel (demo).");
    }

    public OperationResult OpenStartupAppsSettings() => OperationResult.Ok("Would open Startup Apps settings (demo).");
    public OperationResult OpenTaskScheduler() => OperationResult.Ok("Would open Task Scheduler (demo).");
    public OperationResult OpenSystemConfiguration() => OperationResult.Ok("Would open System Configuration (demo).");
}
