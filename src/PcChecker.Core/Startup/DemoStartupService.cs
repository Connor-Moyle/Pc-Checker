using PcChecker.Core.Common;
using PcChecker.Core.Performance;

namespace PcChecker.Core.Startup;

/// <summary>Demo startup data: a typical gaming PC with a few too many launchers at logon.</summary>
public sealed class DemoStartupService : IStartupService
{
    private readonly object _gate = new();
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
    ];

    private readonly List<ServiceInfo> _services =
    [
        new("Audiosrv", "Windows Audio", "Auto", "Running", @"C:\Windows\System32\svchost.exe -k LocalServiceNetworkRestricted", true, "Manages audio for Windows-based programs."),
        new("Steam Client Service", "Steam Client Service", "Manual", "Stopped", @"C:\Program Files (x86)\Common Files\Steam\steamservice.exe", false, "Steam Client Service monitors and updates Steam content."),
        new("NVDisplay.ContainerLocalSystem", "NVIDIA Display Container LS", "Auto", "Running", @"C:\Windows\System32\DriverStore\FileRepository\nvdisplay.inf_amd64\Display.NvContainer\NVDisplay.Container.exe", false, "Container service for NVIDIA root features."),
        new("SysMain", "SysMain", "Auto", "Running", @"C:\Windows\System32\svchost.exe -k LocalSystemNetworkRestricted", true, "Maintains and improves system performance over time."),
        new("WSearch", "Windows Search", "Auto (Delayed)", "Running", @"C:\Windows\System32\SearchIndexer.exe /Embedding", true, "Provides content indexing and search results."),
        new("ArmouryCrateService", "ARMOURY CRATE Service", "Auto", "Running", @"C:\Program Files\ASUS\ARMOURY CRATE Service\ArmouryCrate.Service.exe", false, "ASUS Armoury Crate background service."),
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
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _items.RemoveAll(x => x.Name == item.Name && x.Location == item.Location);
        }

        return OperationResult.Ok($"{item.Name} removed from startup (demo).");
    }

    public async Task<OperationResult> AddStartupItemAsync(string name, string command, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _items.Add(new StartupItem(name, command, @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", true, null, "Registry"));
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
        lock (_gate)
        {
            int i = _services.FindIndex(s => s.Name == serviceName);
            if (i >= 0)
            {
                _services[i] = _services[i] with { StartMode = startMode };
            }
        }

        return OperationResult.Ok($"{serviceName} start mode set to {startMode} (demo).");
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

    public async Task<OperationResult> RestartToUefiAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok("Would restart into the UEFI firmware settings (demo).");
    }

    public async Task<OperationResult> RestartToAdvancedStartupAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok("Would restart into Advanced startup (demo).");
    }

    public OperationResult OpenStartupAppsSettings() => OperationResult.Ok("Would open Startup Apps settings (demo).");
    public OperationResult OpenTaskScheduler() => OperationResult.Ok("Would open Task Scheduler (demo).");
    public OperationResult OpenSystemConfiguration() => OperationResult.Ok("Would open System Configuration (demo).");
}
