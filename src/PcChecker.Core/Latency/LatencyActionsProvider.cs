using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using PcChecker.Core.Common;
using PcChecker.Core.Optimization;
using PcChecker.Core.Restore;

namespace PcChecker.Core.Latency;

/// <summary>
/// Applies and reverts the Latency module's optimization actions on Windows. Every change runs through
/// <see cref="IChangeGuard"/> (restore point per settings, action journal, log). Reads are shared with
/// <see cref="LatencyProbes"/> so the Optimize page and the Latency page agree on the current state.
/// </summary>
public sealed class LatencyActionsProvider : IOptimizationActionProvider
{
    private const string Module = "Latency";
    private const int CoreParkingDefaultMinCores = 10;

    private readonly IChangeGuard _guard;
    private readonly ILogger<LatencyActionsProvider> _logger;

    public LatencyActionsProvider(IChangeGuard guard, ILogger<LatencyActionsProvider> logger)
    {
        _guard = guard;
        _logger = logger;
    }

    public IReadOnlyList<OptimizationAction> Actions => LatencyActionCatalog.All;

    public bool Handles(string actionId) => LatencyActionCatalog.Handles(actionId);

    public async Task<ActionState> GetStateAsync(string id, CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return new ActionState(id, null, "Windows only");
        }

        try
        {
            switch (Normalize(id))
            {
                case LatencyActionIds.HagsOn:
                case LatencyActionIds.HagsOff:
                {
                    bool? on = LatencyProbes.ReadHags();
                    bool wantOn = Normalize(id) == LatencyActionIds.HagsOn;
                    return new ActionState(id, on is null ? null : on == wantOn, on switch { true => "On", false => "Off", _ => "Not supported by this GPU/driver" });
                }

                case LatencyActionIds.UsbSelectiveSuspendOff:
                {
                    var (ac, _) = await LatencyProbes.ReadPowerSettingAsync(LatencyProbes.UsbSubgroup, LatencyProbes.UsbSelectiveSuspendSetting, cancellationToken).ConfigureAwait(false);
                    return new ActionState(id, ac is null ? null : ac == 0, ac switch { 0 => "Disabled", null => "Unknown", _ => "Enabled" });
                }

                case LatencyActionIds.PcieAspmOff:
                {
                    var (ac, _) = await LatencyProbes.ReadPowerSettingAsync(LatencyProbes.PciSubgroup, LatencyProbes.PciAspmSetting, cancellationToken).ConfigureAwait(false);
                    return new ActionState(id, ac is null ? null : ac == 0, ac switch { 0 => "Off", 1 => "Moderate power savings", 2 => "Maximum power savings", _ => "Unknown" });
                }

                case LatencyActionIds.CoreParkingOff:
                {
                    var (ac, _) = await LatencyProbes.ReadPowerSettingAsync(LatencyProbes.ProcessorSubgroup, LatencyProbes.CoreParkingMinCoresSetting, cancellationToken).ConfigureAwait(false);
                    return new ActionState(id, ac is null ? null : ac >= 100, ac is null ? "Unknown" : $"Minimum unparked cores {ac}%");
                }

                case LatencyActionIds.NicPowerSavingOff:
                {
                    var adapters = LatencyProbes.ReadNetworkAdapters();
                    if (adapters.Count == 0)
                    {
                        return new ActionState(id, null, "No physical network adapters found");
                    }

                    bool saving = NicPowerRules.AnyPowerSaving(adapters.Select(a => a.State));
                    return new ActionState(id, !saving, saving ? "Power saving on" : "Power saving off");
                }

                case LatencyActionIds.PointerPrecisionOff:
                {
                    bool? on = LatencyProbes.ReadPointerPrecision();
                    return new ActionState(id, on is null ? null : on == false, on switch { true => "On", false => "Off", _ => "Unknown" });
                }

                case LatencyActionIds.GameModeOn:
                {
                    bool? on = LatencyProbes.ReadGameMode();
                    return new ActionState(id, on, on switch { true => "On", false => "Off", _ => "Unknown" });
                }

                case LatencyActionIds.HpetOff:
                case LatencyActionIds.DynamicTickOff:
                {
                    var boot = await LatencyProbes.ReadBootConfigAsync(cancellationToken).ConfigureAwait(false);
                    if (boot is null)
                    {
                        return new ActionState(id, null, "bcdedit could not be read");
                    }

                    if (Normalize(id) == LatencyActionIds.HpetOff)
                    {
                        bool forced = BcdEditParser.GetBool(boot, "useplatformclock") == true;
                        return new ActionState(id, !forced, forced ? "useplatformclock is set" : "Default timer");
                    }

                    bool disabled = BcdEditParser.GetBool(boot, "disabledynamictick") == true;
                    return new ActionState(id, disabled, disabled ? "Dynamic tick off" : "Dynamic tick on (default)");
                }

                case LatencyActionIds.NagleOff:
                {
                    var interfaces = ActiveTcpInterfaces();
                    if (interfaces.Count == 0)
                    {
                        return new ActionState(id, null, "No active network interfaces");
                    }

                    bool all = interfaces.All(i => LatencyProbes.ReadDword(RegistryHive.LocalMachine, i, "TcpAckFrequency") == 1 && LatencyProbes.ReadDword(RegistryHive.LocalMachine, i, "TCPNoDelay") == 1);
                    return new ActionState(id, all, all ? "Nagle off on every active interface" : "Nagle on (default)");
                }

                case LatencyActionIds.MsiGpuOn:
                {
                    var gpus = LatencyProbes.FindGpuPnpPaths();
                    if (gpus.Count == 0)
                    {
                        return new ActionState(id, null, "No PCI display adapter found");
                    }

                    bool all = gpus.All(g => LatencyProbes.ReadDword(RegistryHive.LocalMachine, MsiKey(g), "MSISupported") == 1);
                    return new ActionState(id, all, all ? "MSI mode on" : "Line-based interrupts");
                }

                case LatencyActionIds.TimerResolutionHint:
                {
                    var timer = LatencyProbes.QueryTimerResolution();
                    return new ActionState(id, null, timer is null ? "Unknown" : $"Current {timer.Value.CurrentMs:0.00} ms (finest {timer.Value.MinMs:0.00} ms)");
                }

                default:
                    return new ActionState(id, null, "Unknown action");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the state of {Action}.", id);
            return new ActionState(id, null, "Could not read: " + ex.Message);
        }
    }

    public Task<OperationResult> ApplyAsync(string id, CancellationToken cancellationToken) => RunAsync(id, apply: true, cancellationToken);

    public Task<OperationResult> RevertAsync(string id, CancellationToken cancellationToken) => RunAsync(id, apply: false, cancellationToken);

    private Task<OperationResult> RunAsync(string id, bool apply, CancellationToken cancellationToken)
    {
        var action = LatencyActionCatalog.Find(id);
        if (action is null)
        {
            return Task.FromResult(OperationResult.Fail($"The Latency module does not know the action '{id}'."));
        }

        if (!PlatformGuard.IsWindows)
        {
            return Task.FromResult(OperationResult.Fail("This change is only possible on Windows."));
        }

        if (action.Id == LatencyActionIds.TimerResolutionHint)
        {
            // Information only: there is nothing to change, so say so instead of silently succeeding.
            var timer = LatencyProbes.QueryTimerResolution();
            string current = timer is null ? "unknown" : $"{timer.Value.CurrentMs:0.00} ms";
            return Task.FromResult(OperationResult.Ok($"Nothing was changed. The timer is currently at {current}; Windows 11 raises it automatically for games and media apps that ask for it. Tools that force 0.5 ms all the time mostly cost battery and add heat."));
        }

        string description = (apply ? string.Empty : "Undo: ") + action.Title;
        var options = ChangeOptions.For(Module, action.Id, action.Reversible, apply ? UndoHintFor(action.Id) : null);
        return _guard.RunGuardedAsync(description, options, ct => ExecuteAsync(action, apply, ct), null, cancellationToken);
    }

    private async Task<OperationResult> ExecuteAsync(OptimizationAction action, bool apply, CancellationToken ct)
    {
        var result = action.Id switch
        {
            LatencyActionIds.HagsOn => LatencyProbes.WriteDword(RegistryHive.LocalMachine, LatencyProbes.GraphicsDriversKey, "HwSchMode", apply ? 2 : 1),
            LatencyActionIds.HagsOff => LatencyProbes.WriteDword(RegistryHive.LocalMachine, LatencyProbes.GraphicsDriversKey, "HwSchMode", apply ? 1 : 2),
            LatencyActionIds.UsbSelectiveSuspendOff => await LatencyProbes.SetPowerSettingAsync(LatencyProbes.UsbSubgroup, LatencyProbes.UsbSelectiveSuspendSetting, apply ? 0 : 1, ct).ConfigureAwait(false),
            LatencyActionIds.PcieAspmOff => await LatencyProbes.SetPowerSettingAsync(LatencyProbes.PciSubgroup, LatencyProbes.PciAspmSetting, apply ? 0 : 1, ct).ConfigureAwait(false),
            LatencyActionIds.CoreParkingOff => await LatencyProbes.SetPowerSettingAsync(LatencyProbes.ProcessorSubgroup, LatencyProbes.CoreParkingMinCoresSetting, apply ? 100 : CoreParkingDefaultMinCores, ct).ConfigureAwait(false),
            LatencyActionIds.NicPowerSavingOff => await SetNicPowerSavingAsync(!apply, ct).ConfigureAwait(false),
            LatencyActionIds.PointerPrecisionOff => LatencyProbes.SetPointerPrecision(!apply),
            LatencyActionIds.GameModeOn => SetGameMode(apply),
            LatencyActionIds.HpetOff => await LatencyProbes.BcdEditAsync(apply ? "/deletevalue useplatformclock" : "/set useplatformclock true", ct).ConfigureAwait(false),
            LatencyActionIds.DynamicTickOff => await LatencyProbes.BcdEditAsync(apply ? "/set disabledynamictick yes" : "/deletevalue disabledynamictick", ct).ConfigureAwait(false),
            LatencyActionIds.NagleOff => SetNagle(!apply),
            LatencyActionIds.MsiGpuOn => SetGpuMsi(apply),
            _ => OperationResult.Fail("Unknown action."),
        };

        if (!result.Success)
        {
            return result;
        }

        string message = apply ? $"{action.Title}: done." : $"{action.Title}: reverted.";
        if (action.RequiresRestart)
        {
            message += " Restart the PC for it to take effect.";
        }

        return OperationResult.Ok(message, action.RequiresRestart);
    }

    private static OperationResult SetGameMode(bool enabled)
    {
        var first = LatencyProbes.WriteDword(RegistryHive.CurrentUser, LatencyProbes.GameBarKey, "AutoGameModeEnabled", enabled ? 1 : 0);
        return first.Success ? LatencyProbes.WriteDword(RegistryHive.CurrentUser, LatencyProbes.GameBarKey, "AllowAutoGameMode", enabled ? 1 : 0) : first;
    }

    private static async Task<OperationResult> SetNicPowerSavingAsync(bool enabled, CancellationToken ct)
    {
        var adapters = LatencyProbes.ReadNetworkAdapters();
        if (adapters.Count == 0)
        {
            return OperationResult.Fail("No physical network adapters were found in the registry.");
        }

        var errors = new List<string>();
        foreach (var (subKey, state) in adapters)
        {
            foreach (var write in new[]
                     {
                         LatencyProbes.WriteDword(RegistryHive.LocalMachine, subKey, "PnPCapabilities", enabled ? 0 : NicPowerRules.PnpCapabilitiesPowerOff),
                         LatencyProbes.WriteString(RegistryHive.LocalMachine, subKey, "*EEE", enabled ? "1" : "0"),
                         LatencyProbes.WriteString(RegistryHive.LocalMachine, subKey, "EnableGreenEthernet", enabled ? "1" : "0"),
                     })
            {
                if (!write.Success)
                {
                    errors.Add($"{state.Name}: {write.Message}");
                }
            }
        }

        // Also flip the "Allow the computer to turn off this device" checkbox through the NetAdapter cmdlets when available.
        string cmdlet = enabled ? "Enable-NetAdapterPowerManagement" : "Disable-NetAdapterPowerManagement";
        var ps = await PowerShellRunner.RunAsync($"Get-NetAdapter -Physical -ErrorAction SilentlyContinue | {cmdlet} -NoRestart -ErrorAction SilentlyContinue", TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        if (!ps.Success && !string.IsNullOrWhiteSpace(ps.StdErr))
        {
            errors.Add("PowerShell: " + ps.StdErr.Trim());
        }

        return errors.Count == adapters.Count * 3
            ? OperationResult.Fail("None of the adapters could be changed: " + string.Join("; ", errors))
            : OperationResult.Ok(errors.Count == 0 ? $"Updated {adapters.Count} adapter(s)." : $"Updated with warnings: {string.Join("; ", errors)}");
    }

    private static OperationResult SetNagle(bool enabled)
    {
        var interfaces = ActiveTcpInterfaces();
        if (interfaces.Count == 0)
        {
            return OperationResult.Fail("No active network interfaces were found under Tcpip\\Parameters\\Interfaces.");
        }

        foreach (var key in interfaces)
        {
            var result = enabled
                ? Combine(LatencyProbes.DeleteValue(RegistryHive.LocalMachine, key, "TcpAckFrequency"), LatencyProbes.DeleteValue(RegistryHive.LocalMachine, key, "TCPNoDelay"))
                : Combine(LatencyProbes.WriteDword(RegistryHive.LocalMachine, key, "TcpAckFrequency", 1), LatencyProbes.WriteDword(RegistryHive.LocalMachine, key, "TCPNoDelay", 1));
            if (!result.Success)
            {
                return result;
            }
        }

        return OperationResult.Ok($"Updated {interfaces.Count} interface(s).");
    }

    private static OperationResult SetGpuMsi(bool enabled)
    {
        var gpus = LatencyProbes.FindGpuPnpPaths();
        if (gpus.Count == 0)
        {
            return OperationResult.Fail("No PCI display adapter was found.");
        }

        foreach (var gpu in gpus)
        {
            var result = LatencyProbes.WriteDword(RegistryHive.LocalMachine, MsiKey(gpu), "MSISupported", enabled ? 1 : 0);
            if (!result.Success)
            {
                return result;
            }
        }

        return OperationResult.Ok($"Updated {gpus.Count} display adapter(s).");
    }

    private static string MsiKey(string gpuPath) => $@"{LatencyProbes.PciEnumKey}\{gpuPath}\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties";

    /// <summary>Interfaces that currently hold an IPv4 address (static or DHCP).</summary>
    private static IReadOnlyList<string> ActiveTcpInterfaces()
    {
        var list = new List<string>();
        foreach (var guid in LatencyProbes.SubKeyNames(RegistryHive.LocalMachine, LatencyProbes.TcpInterfacesKey))
        {
            var key = $@"{LatencyProbes.TcpInterfacesKey}\{guid}";
            var dhcp = LatencyProbes.ReadString(RegistryHive.LocalMachine, key, "DhcpIPAddress");
            var fixedIp = LatencyProbes.ReadString(RegistryHive.LocalMachine, key, "IPAddress");
            bool hasAddress = (!string.IsNullOrWhiteSpace(dhcp) && dhcp != "0.0.0.0") || (!string.IsNullOrWhiteSpace(fixedIp) && !fixedIp.StartsWith("0.0.0.0", StringComparison.Ordinal));
            if (hasAddress)
            {
                list.Add(key);
            }
        }

        return list;
    }

    private static OperationResult Combine(OperationResult first, OperationResult second) => first.Success ? second : first;

    private static string Normalize(string id) => LatencyActionCatalog.Find(id)?.Id ?? id;

    private static string? UndoHintFor(string id) => id switch
    {
        LatencyActionIds.HagsOn => "Set HKLM\\SYSTEM\\CurrentControlSet\\Control\\GraphicsDrivers\\HwSchMode back to 1, or use Undo on the Optimize page.",
        LatencyActionIds.HagsOff => "Set HwSchMode back to 2, or use Undo on the Optimize page.",
        LatencyActionIds.UsbSelectiveSuspendOff => "Power Options → USB settings → USB selective suspend → Enabled.",
        LatencyActionIds.NicPowerSavingOff => "Device Manager → network adapter → Power Management / Advanced tab.",
        LatencyActionIds.PcieAspmOff => "Power Options → PCI Express → Link State Power Management → Moderate.",
        LatencyActionIds.CoreParkingOff => "powercfg /setacvalueindex SCHEME_CURRENT SUB_PROCESSOR CPMINCORES 10",
        LatencyActionIds.PointerPrecisionOff => "Mouse settings → Additional mouse options → Pointer Options → Enhance pointer precision.",
        LatencyActionIds.GameModeOn => "Settings → Gaming → Game Mode.",
        LatencyActionIds.HpetOff => "bcdedit /set useplatformclock true",
        LatencyActionIds.DynamicTickOff => "bcdedit /deletevalue disabledynamictick",
        LatencyActionIds.NagleOff => "Delete TcpAckFrequency and TCPNoDelay under Tcpip\\Parameters\\Interfaces\\<guid>.",
        LatencyActionIds.MsiGpuOn => "Set MSISupported back to 0 under the GPU's Device Parameters\\Interrupt Management key.",
        _ => null,
    };
}
