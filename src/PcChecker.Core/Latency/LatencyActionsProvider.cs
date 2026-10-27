using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using PcChecker.Core.Common;
using PcChecker.Core.Optimization;
using PcChecker.Core.Restore;
using PcChecker.Core.Settings;

namespace PcChecker.Core.Latency;

/// <summary>
/// Applies and reverts the Latency module's optimization actions on Windows. Every change runs through
/// <see cref="IChangeGuard"/> (restore point per settings, action journal, log). Reads are shared with
/// <see cref="LatencyProbes"/> so the Optimize page and the Latency page agree on the current state.
/// Before a change is applied the values it is about to overwrite are captured and saved next to the settings file
/// (<see cref="LatencyCaptureStore"/>), so a revert, even after the app was restarted, restores exactly those values
/// (the captured values are also written into the journal's undo hint). When nothing was ever captured the revert
/// falls back to the true Windows default, or refuses when the default is not known to be safe.
/// </summary>
public sealed class LatencyActionsProvider : IOptimizationActionProvider
{
    private const string Module = "Latency";
    private const int CoreParkingDefaultMinCores = 10;
    private const string Absent = "<not set>";

    private readonly IChangeGuard _guard;
    private readonly ILogger<LatencyActionsProvider> _logger;
    private readonly string? _capturePath;
    private readonly object _captureLock = new();
    private readonly Dictionary<string, IReadOnlyDictionary<string, string?>> _captured = new(StringComparer.OrdinalIgnoreCase);
    private bool _capturesLoaded;

    public LatencyActionsProvider(IChangeGuard guard, ISettingsService settings, ILogger<LatencyActionsProvider> logger)
    {
        _guard = guard;
        _logger = logger;
        try
        {
            _capturePath = LatencyCaptureStore.DefaultPath(settings.SettingsDirectory);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Latency captures will not be persisted: the settings directory is unknown.");
        }
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

    private async Task<OperationResult> RunAsync(string id, bool apply, CancellationToken cancellationToken)
    {
        var action = LatencyActionCatalog.Find(id);
        if (action is null)
        {
            return OperationResult.Fail($"The Latency module does not know the action '{id}'.");
        }

        if (!PlatformGuard.IsWindows)
        {
            return OperationResult.Fail("This change is only possible on Windows.");
        }

        if (action.Id == LatencyActionIds.TimerResolutionHint)
        {
            // Information only: there is nothing to change, so say so instead of silently succeeding.
            var timer = LatencyProbes.QueryTimerResolution();
            string current = timer is null ? "unknown" : $"{timer.Value.CurrentMs:0.00} ms";
            return OperationResult.Ok($"Nothing was changed. The timer is currently at {current}; Windows 11 raises it automatically for games and media apps that ask for it. Tools that force 0.5 ms all the time mostly cost battery and add heat.");
        }

        IReadOnlyDictionary<string, string?>? previous;
        string? undoHint = null;
        if (apply)
        {
            // Read everything the change is about to overwrite so the revert can put it back exactly.
            previous = await CaptureAsync(action.Id, cancellationToken).ConfigureAwait(false);
            lock (_captureLock)
            {
                EnsureCapturesLoaded();
                // Keep the first capture when the action is applied twice: that is what the machine looked like before PC Checker touched it.
                if (!_captured.ContainsKey(action.Id))
                {
                    _captured[action.Id] = previous;
                    SaveCaptures();
                }
                else
                {
                    // Targets that appeared since (a dock, a VPN, a second adapter) were not changed before, so their
                    // current values are the originals: add them, or the revert would never put them back.
                    var stored = _captured[action.Id];
                    var added = previous.Where(p => p.Key.Contains('|') && !stored.ContainsKey(p.Key)).ToList();
                    if (added.Count > 0)
                    {
                        var merged = new Dictionary<string, string?>(stored, StringComparer.OrdinalIgnoreCase);
                        foreach (var (key, value) in added)
                        {
                            merged[key] = value;
                        }

                        _captured[action.Id] = merged;
                        SaveCaptures();
                    }

                    previous = _captured[action.Id];
                }
            }

            undoHint = UndoHintFor(action.Id);
            if (previous.Count > 0)
            {
                undoHint = $"{undoHint} Previous value(s): {DescribeCapture(previous)}";
            }
        }
        else
        {
            lock (_captureLock)
            {
                EnsureCapturesLoaded();
                previous = _captured.GetValueOrDefault(action.Id);
            }

            if (previous is null && action.Id == LatencyActionIds.MsiGpuOn)
            {
                // Modern GPU drivers ship with MSI mode on; writing 0 "back" would downgrade the card to line-based interrupts.
                return OperationResult.Fail("Nothing was recorded before MSI mode was turned on (it was already on, or it was set outside PC Checker), so there is no previous value to put back. "
                    + "MSI mode is the driver default for current NVIDIA, AMD and Intel GPUs; leave it on unless a device-specific problem points at interrupts.");
            }
        }

        string description = (apply ? string.Empty : "Undo: ") + action.Title;
        var options = ChangeOptions.For(Module, action.Id, action.Reversible, undoHint);
        var result = await _guard.RunGuardedAsync(description, options, ct => ExecuteAsync(action, apply, previous, ct), null, cancellationToken).ConfigureAwait(false);
        if (!apply && result.Success)
        {
            lock (_captureLock)
            {
                if (_captured.Remove(action.Id))
                {
                    SaveCaptures();
                }
            }
        }

        return result;
    }

    /// <summary>Loads the captures saved by earlier runs. Called under <see cref="_captureLock"/>.</summary>
    private void EnsureCapturesLoaded()
    {
        if (_capturesLoaded)
        {
            return;
        }

        _capturesLoaded = true;
        if (_capturePath is null)
        {
            return;
        }

        try
        {
            foreach (var (id, values) in LatencyCaptureStore.TryRead(_capturePath))
            {
                _captured.TryAdd(id, values);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the saved latency captures from {Path}.", _capturePath);
        }
    }

    /// <summary>Writes the captures to disk so Undo still knows the previous values after a restart. Called under <see cref="_captureLock"/>.</summary>
    private void SaveCaptures()
    {
        if (_capturePath is null)
        {
            return;
        }

        try
        {
            LatencyCaptureStore.Write(_capturePath, _captured);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save the latency captures to {Path}; Undo will fall back to defaults after a restart.", _capturePath);
        }
    }

    private async Task<OperationResult> ExecuteAsync(OptimizationAction action, bool apply, IReadOnlyDictionary<string, string?>? previous, CancellationToken ct)
    {
        var result = action.Id switch
        {
            LatencyActionIds.HagsOn => apply
                ? LatencyProbes.WriteDword(RegistryHive.LocalMachine, LatencyProbes.GraphicsDriversKey, "HwSchMode", 2)
                : RestoreDword(RegistryHive.LocalMachine, LatencyProbes.GraphicsDriversKey, "HwSchMode", previous, "HwSchMode", 1),
            LatencyActionIds.HagsOff => apply
                ? LatencyProbes.WriteDword(RegistryHive.LocalMachine, LatencyProbes.GraphicsDriversKey, "HwSchMode", 1)
                : RestoreDword(RegistryHive.LocalMachine, LatencyProbes.GraphicsDriversKey, "HwSchMode", previous, "HwSchMode", 2),
            LatencyActionIds.UsbSelectiveSuspendOff => await SetOrRestorePowerSettingAsync(LatencyProbes.UsbSubgroup, LatencyProbes.UsbSelectiveSuspendSetting, apply ? 0 : null, previous, 1, ct).ConfigureAwait(false),
            LatencyActionIds.PcieAspmOff => await SetOrRestorePowerSettingAsync(LatencyProbes.PciSubgroup, LatencyProbes.PciAspmSetting, apply ? 0 : null, previous, 1, ct).ConfigureAwait(false),
            LatencyActionIds.CoreParkingOff => await SetOrRestorePowerSettingAsync(LatencyProbes.ProcessorSubgroup, LatencyProbes.CoreParkingMinCoresSetting, apply ? 100 : null, previous, CoreParkingDefaultMinCores, ct).ConfigureAwait(false),
            LatencyActionIds.NicPowerSavingOff => await SetNicPowerSavingAsync(apply, previous, ct).ConfigureAwait(false),
            LatencyActionIds.PointerPrecisionOff => LatencyProbes.SetPointerPrecision(!apply && Previous(previous, "EnhancePointerPrecision") != "0"),
            LatencyActionIds.GameModeOn => SetGameMode(apply, previous),
            // Nothing captured: the Windows default is the value being absent, so the revert deletes it rather than forcing HPET on.
            LatencyActionIds.HpetOff => await SetBootValueAsync("useplatformclock", apply ? null : Previous(previous, "useplatformclock"), null, ct).ConfigureAwait(false),
            LatencyActionIds.DynamicTickOff => await SetBootValueAsync("disabledynamictick", apply ? "yes" : Previous(previous, "disabledynamictick"), null, ct).ConfigureAwait(false),
            LatencyActionIds.NagleOff => SetNagle(apply, previous),
            LatencyActionIds.MsiGpuOn => SetGpuMsi(apply, previous),
            _ => OperationResult.Fail("Unknown action."),
        };

        if (!result.Success)
        {
            return result;
        }

        string message = apply ? $"{action.Title}: done." : $"{action.Title}: reverted.";
        if (!string.IsNullOrWhiteSpace(result.Message) && result.Message.StartsWith("Updated", StringComparison.Ordinal))
        {
            message += " " + result.Message;
        }

        if (action.RequiresRestart)
        {
            message += " Restart the PC for it to take effect.";
        }

        return OperationResult.Ok(message, action.RequiresRestart);
    }

    /// <summary>Reads the values an action is about to change. Keys are "target|value" (or just the value name); null means the value was absent.</summary>
    private static async Task<IReadOnlyDictionary<string, string?>> CaptureAsync(string actionId, CancellationToken ct)
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        switch (actionId)
        {
            case LatencyActionIds.HagsOn:
            case LatencyActionIds.HagsOff:
                map["HwSchMode"] = LatencyProbes.ReadDword(RegistryHive.LocalMachine, LatencyProbes.GraphicsDriversKey, "HwSchMode")?.ToString();
                break;

            case LatencyActionIds.UsbSelectiveSuspendOff:
                await CapturePowerAsync(map, LatencyProbes.UsbSubgroup, LatencyProbes.UsbSelectiveSuspendSetting, ct).ConfigureAwait(false);
                break;

            case LatencyActionIds.PcieAspmOff:
                await CapturePowerAsync(map, LatencyProbes.PciSubgroup, LatencyProbes.PciAspmSetting, ct).ConfigureAwait(false);
                break;

            case LatencyActionIds.CoreParkingOff:
                await CapturePowerAsync(map, LatencyProbes.ProcessorSubgroup, LatencyProbes.CoreParkingMinCoresSetting, ct).ConfigureAwait(false);
                break;

            case LatencyActionIds.NicPowerSavingOff:
                foreach (var (subKey, _) in LatencyProbes.ReadNetworkAdapters())
                {
                    map[$"{subKey}|PnPCapabilities"] = LatencyProbes.ReadDword(RegistryHive.LocalMachine, subKey, "PnPCapabilities")?.ToString();
                    foreach (var name in NicPowerSavingStringValues)
                    {
                        map[$"{subKey}|{name}"] = LatencyProbes.ReadString(RegistryHive.LocalMachine, subKey, name);
                    }
                }

                // Marks a capture made by a version that no longer runs Disable-NetAdapterPowerManagement (see SetNicPowerSavingAsync).
                map[NicCmdletMarker] = NicCmdletUnchanged;
                break;

            case LatencyActionIds.PointerPrecisionOff:
                map["EnhancePointerPrecision"] = LatencyProbes.ReadPointerPrecision() switch { true => "1", false => "0", _ => null };
                break;

            case LatencyActionIds.GameModeOn:
                map["AutoGameModeEnabled"] = LatencyProbes.ReadDword(RegistryHive.CurrentUser, LatencyProbes.GameBarKey, "AutoGameModeEnabled")?.ToString();
                map["AllowAutoGameMode"] = LatencyProbes.ReadDword(RegistryHive.CurrentUser, LatencyProbes.GameBarKey, "AllowAutoGameMode")?.ToString();
                break;

            case LatencyActionIds.HpetOff:
            case LatencyActionIds.DynamicTickOff:
            {
                string name = actionId == LatencyActionIds.HpetOff ? "useplatformclock" : "disabledynamictick";
                var boot = await LatencyProbes.ReadBootConfigAsync(ct).ConfigureAwait(false);
                if (boot is not null)
                {
                    map[name] = boot.TryGetValue(name, out var value) ? value : null;
                }

                break;
            }

            case LatencyActionIds.NagleOff:
                foreach (var key in ActiveTcpInterfaces())
                {
                    map[$"{key}|TcpAckFrequency"] = LatencyProbes.ReadDword(RegistryHive.LocalMachine, key, "TcpAckFrequency")?.ToString();
                    map[$"{key}|TCPNoDelay"] = LatencyProbes.ReadDword(RegistryHive.LocalMachine, key, "TCPNoDelay")?.ToString();
                }

                break;

            case LatencyActionIds.MsiGpuOn:
                foreach (var gpu in LatencyProbes.FindGpuPnpPaths())
                {
                    map[$"{gpu}|MSISupported"] = LatencyProbes.ReadDword(RegistryHive.LocalMachine, MsiKey(gpu), "MSISupported")?.ToString();
                }

                break;
        }

        return map;
    }

    /// <summary>
    /// Records the AC/DC indexes of the active plan and which plan that is, so a revert writes them back into the same
    /// plan even when the user has switched plans since.
    /// </summary>
    private static async Task CapturePowerAsync(Dictionary<string, string?> map, string subgroup, string setting, CancellationToken ct)
    {
        var (scheme, _) = await LatencyProbes.ReadActiveSchemeAsync(ct).ConfigureAwait(false);
        var indexes = await LatencyProbes.ReadPowerSettingAsync(subgroup, setting, ct).ConfigureAwait(false);
        map["Scheme"] = scheme?.ToString("D");
        map["AC"] = indexes.Ac?.ToString();
        map["DC"] = indexes.Dc?.ToString();
    }

    private static string DescribeCapture(IReadOnlyDictionary<string, string?> previous)
    {
        var parts = previous
            .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .Select(p => $"{ShortKey(p.Key)}={p.Value ?? Absent}")
            .ToList();
        const int max = 12;
        return parts.Count <= max
            ? string.Join(", ", parts)
            : string.Join(", ", parts.Take(max)) + $" … ({parts.Count - max} more)";
    }

    private static string ShortKey(string key)
    {
        int bar = key.LastIndexOf('|');
        if (bar < 0)
        {
            return key;
        }

        // Keep only the last path segment of the target so the hint stays readable.
        string target = key[..bar];
        int slash = target.LastIndexOf('\\');
        return (slash >= 0 ? target[(slash + 1)..] : target) + "\\" + key[(bar + 1)..];
    }

    private static string? Previous(IReadOnlyDictionary<string, string?>? previous, string key) =>
        previous is not null && previous.TryGetValue(key, out var value) ? value : null;

    private static bool WasCaptured(IReadOnlyDictionary<string, string?>? previous, string key) =>
        previous is not null && previous.ContainsKey(key);

    /// <summary>Restores a DWORD to the captured value (deleting it when it was absent), or to <paramref name="fallback"/> when nothing was captured.</summary>
    private static OperationResult RestoreDword(RegistryHive hive, string subKey, string valueName, IReadOnlyDictionary<string, string?>? previous, string captureKey, int? fallback)
    {
        if (WasCaptured(previous, captureKey))
        {
            return int.TryParse(Previous(previous, captureKey), out int value)
                ? LatencyProbes.WriteDword(hive, subKey, valueName, value)
                : LatencyProbes.DeleteValue(hive, subKey, valueName);
        }

        return fallback is { } f ? LatencyProbes.WriteDword(hive, subKey, valueName, f) : LatencyProbes.DeleteValue(hive, subKey, valueName);
    }

    /// <summary>Restores a REG_SZ value to the captured text (deleting it when it was absent), or writes <paramref name="fallback"/> only when the value exists today.</summary>
    private static OperationResult RestoreString(RegistryHive hive, string subKey, string valueName, IReadOnlyDictionary<string, string?>? previous, string captureKey, string fallback)
    {
        if (WasCaptured(previous, captureKey))
        {
            var value = Previous(previous, captureKey);
            return value is null ? LatencyProbes.DeleteValue(hive, subKey, valueName) : LatencyProbes.WriteString(hive, subKey, valueName, value);
        }

        return LatencyProbes.ValueExists(hive, subKey, valueName)
            ? LatencyProbes.WriteString(hive, subKey, valueName, fallback)
            : OperationResult.Ok($"{valueName} is not defined by this driver.");
    }

    private static Task<OperationResult> SetOrRestorePowerSettingAsync(string subgroup, string setting, int? applyValue, IReadOnlyDictionary<string, string?>? previous, int defaultValue, CancellationToken ct)
    {
        if (applyValue is { } v)
        {
            return LatencyProbes.SetPowerSettingAsync(subgroup, setting, v, ct);
        }

        int ac = int.TryParse(Previous(previous, "AC"), out int pa) ? pa : defaultValue;
        int dc = int.TryParse(Previous(previous, "DC"), out int pd) ? pd : defaultValue;
        // The values belong to the plan that was active when they were captured, which may no longer be the active one.
        return LatencyProbes.SetPowerSettingAsync(LatencyProbes.SchemeArgument(Previous(previous, "Scheme")), subgroup, setting, ac, dc, ct);
    }

    /// <summary>
    /// Sets or deletes a boot option. <paramref name="value"/> null deletes it; <paramref name="fallbackWhenUnknown"/> is used
    /// when nothing was captured (null leaves the deletion as the revert).
    /// </summary>
    private static Task<OperationResult> SetBootValueAsync(string name, string? value, string? fallbackWhenUnknown, CancellationToken ct)
    {
        value ??= fallbackWhenUnknown;
        return value is null
            ? LatencyProbes.BcdEditAsync($"/deletevalue {name}", ct)
            : LatencyProbes.BcdEditAsync($"/set {name} {value}", ct);
    }

    private static OperationResult SetGameMode(bool enabled, IReadOnlyDictionary<string, string?>? previous)
    {
        if (enabled)
        {
            var first = LatencyProbes.WriteDword(RegistryHive.CurrentUser, LatencyProbes.GameBarKey, "AutoGameModeEnabled", 1);
            return first.Success ? LatencyProbes.WriteDword(RegistryHive.CurrentUser, LatencyProbes.GameBarKey, "AllowAutoGameMode", 1) : first;
        }

        // Revert: the Windows 11 default is "on" with the values absent, so restore what was there (or remove them).
        var auto = RestoreDword(RegistryHive.CurrentUser, LatencyProbes.GameBarKey, "AutoGameModeEnabled", previous, "AutoGameModeEnabled", null);
        return auto.Success ? RestoreDword(RegistryHive.CurrentUser, LatencyProbes.GameBarKey, "AllowAutoGameMode", previous, "AllowAutoGameMode", null) : auto;
    }

    private static readonly string[] NicPowerSavingStringValues = ["*EEE", "EnableGreenEthernet"];

    private const string NicCmdletMarker = "NetAdapterPowerManagement";
    private const string NicCmdletUnchanged = "unchanged";

    private static async Task<OperationResult> SetNicPowerSavingAsync(bool apply, IReadOnlyDictionary<string, string?>? previous, CancellationToken ct)
    {
        var adapters = LatencyProbes.ReadNetworkAdapters();
        if (adapters.Count == 0)
        {
            return OperationResult.Fail("No physical network adapters were found in the registry.");
        }

        var errors = new List<string>();
        int adaptersChanged = 0;
        foreach (var (subKey, state) in adapters)
        {
            // Only the "turn off this device" bit is set; the wake bits stay as they were, so Wake-on-LAN keeps its setting.
            var writes = new List<OperationResult>
            {
                apply
                    ? LatencyProbes.WriteDword(RegistryHive.LocalMachine, subKey, "PnPCapabilities",
                        NicPowerRules.PowerOffCapabilities(LatencyProbes.ReadDword(RegistryHive.LocalMachine, subKey, "PnPCapabilities")))
                    : RestoreDword(RegistryHive.LocalMachine, subKey, "PnPCapabilities", previous, $"{subKey}|PnPCapabilities", null),
            };

            // *EEE and EnableGreenEthernet only exist on drivers that implement them (wired adapters, mostly); never invent them.
            foreach (var name in NicPowerSavingStringValues)
            {
                if (apply)
                {
                    if (LatencyProbes.ValueExists(RegistryHive.LocalMachine, subKey, name))
                    {
                        writes.Add(LatencyProbes.WriteString(RegistryHive.LocalMachine, subKey, name, "0"));
                    }
                }
                else
                {
                    writes.Add(RestoreString(RegistryHive.LocalMachine, subKey, name, previous, $"{subKey}|{name}", "1"));
                }
            }

            var failed = writes.Where(w => !w.Success).ToList();
            if (failed.Count < writes.Count)
            {
                adaptersChanged++;
            }

            errors.AddRange(failed.Select(f => $"{state.Name}: {f.Message}"));
        }

        if (adaptersChanged == 0)
        {
            return OperationResult.Fail("None of the adapters could be changed (administrator rights are needed): " + string.Join("; ", errors));
        }

        // PnPCapabilities above is what the "Allow the computer to turn off this device" checkbox stores. Earlier versions
        // also ran Disable-NetAdapterPowerManagement, which turns off every power feature including Wake-on-LAN and
        // wake on pattern match, without recording them. Apply no longer does that; only an Undo of such an older
        // change (a capture without the marker) still runs Enable-NetAdapterPowerManagement, as that change expects.
        var warnings = new List<string>(errors);
        if (!apply && previous is not null && !WasCaptured(previous, NicCmdletMarker))
        {
            var ps = await PowerShellRunner.RunAsync("Get-NetAdapter -Physical -ErrorAction SilentlyContinue | Enable-NetAdapterPowerManagement -NoRestart -ErrorAction SilentlyContinue", TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
            if (!ps.Success && !string.IsNullOrWhiteSpace(ps.StdErr))
            {
                warnings.Add("PowerShell: " + ps.StdErr.Trim());
            }
        }

        return OperationResult.Ok(warnings.Count == 0
            ? $"Updated {adaptersChanged} adapter(s)."
            : $"Updated {adaptersChanged} of {adapters.Count} adapter(s) with warnings: {string.Join("; ", warnings)}");
    }

    private static OperationResult SetNagle(bool apply, IReadOnlyDictionary<string, string?>? previous)
    {
        // A revert goes back over exactly the interfaces that were changed, even if they hold no address right now
        // (a disconnected dock or VPN), and leaves interfaces it never changed alone. Only without a capture does it
        // fall back to removing the values from the interfaces that are active now.
        IReadOnlyList<string> captured = apply ? [] : LatencyCaptureStore.CapturedTargets(previous, LatencyProbes.TcpInterfacesKey, "TcpAckFrequency", "TCPNoDelay");
        if (captured.Count > 0)
        {
            var existing = new HashSet<string>(LatencyProbes.SubKeyNames(RegistryHive.LocalMachine, LatencyProbes.TcpInterfacesKey), StringComparer.OrdinalIgnoreCase);
            var present = captured.Where(key => existing.Contains(key[(key.LastIndexOf('\\') + 1)..])).ToList();
            if (present.Count == 0)
            {
                return OperationResult.Ok("The network interfaces that were changed no longer exist, so there is nothing to restore.");
            }

            return SetNagleOn(present, apply, previous);
        }

        var interfaces = ActiveTcpInterfaces();
        if (interfaces.Count == 0)
        {
            return OperationResult.Fail("No active network interfaces were found under Tcpip\\Parameters\\Interfaces.");
        }

        return SetNagleOn(interfaces, apply, previous);
    }

    private static OperationResult SetNagleOn(IReadOnlyList<string> interfaces, bool apply, IReadOnlyDictionary<string, string?>? previous)
    {
        foreach (var key in interfaces)
        {
            var result = apply
                ? Combine(LatencyProbes.WriteDword(RegistryHive.LocalMachine, key, "TcpAckFrequency", 1), LatencyProbes.WriteDword(RegistryHive.LocalMachine, key, "TCPNoDelay", 1))
                : Combine(RestoreDword(RegistryHive.LocalMachine, key, "TcpAckFrequency", previous, $"{key}|TcpAckFrequency", null),
                    RestoreDword(RegistryHive.LocalMachine, key, "TCPNoDelay", previous, $"{key}|TCPNoDelay", null));
            if (!result.Success)
            {
                return result;
            }
        }

        return OperationResult.Ok($"Updated {interfaces.Count} interface(s).");
    }

    private static OperationResult SetGpuMsi(bool apply, IReadOnlyDictionary<string, string?>? previous)
    {
        var gpus = LatencyProbes.FindGpuPnpPaths();
        if (gpus.Count == 0)
        {
            return OperationResult.Fail("No PCI display adapter was found.");
        }

        foreach (var gpu in gpus)
        {
            var result = apply
                ? LatencyProbes.WriteDword(RegistryHive.LocalMachine, MsiKey(gpu), "MSISupported", 1)
                : RestoreDword(RegistryHive.LocalMachine, MsiKey(gpu), "MSISupported", previous, $"{gpu}|MSISupported", 0);
            if (!result.Success)
            {
                return result;
            }
        }

        return OperationResult.Ok($"Updated {gpus.Count} display adapter(s).");
    }

    private static string MsiKey(string gpuPath) => $@"{LatencyProbes.PciEnumKey}\{gpuPath}\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties";

    /// <summary>Interfaces that currently hold an IPv4 address (DHCP lease or static REG_MULTI_SZ list).</summary>
    private static IReadOnlyList<string> ActiveTcpInterfaces()
    {
        var list = new List<string>();
        foreach (var guid in LatencyProbes.SubKeyNames(RegistryHive.LocalMachine, LatencyProbes.TcpInterfacesKey))
        {
            var key = $@"{LatencyProbes.TcpInterfacesKey}\{guid}";
            var dhcp = LatencyProbes.ReadString(RegistryHive.LocalMachine, key, "DhcpIPAddress");
            var fixedIps = LatencyProbes.ReadStrings(RegistryHive.LocalMachine, key, "IPAddress");
            if (NicPowerRules.InterfaceHoldsAddress(dhcp, fixedIps))
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
        LatencyActionIds.CoreParkingOff => "powercfg /setacvalueindex <Scheme> SUB_PROCESSOR CPMINCORES <AC> and /setdcvalueindex <Scheme> SUB_PROCESSOR CPMINCORES <DC>, using the previous values listed after this hint.",
        LatencyActionIds.PointerPrecisionOff => "Mouse settings → Additional mouse options → Pointer Options → Enhance pointer precision.",
        LatencyActionIds.GameModeOn => "Settings → Gaming → Game Mode.",
        LatencyActionIds.HpetOff => "bcdedit /deletevalue useplatformclock (the Windows default); only bcdedit /set useplatformclock true if HPET had been forced on before.",
        LatencyActionIds.DynamicTickOff => "bcdedit /deletevalue disabledynamictick",
        LatencyActionIds.NagleOff => "Delete TcpAckFrequency and TCPNoDelay under Tcpip\\Parameters\\Interfaces\\<guid>.",
        LatencyActionIds.MsiGpuOn => "Set MSISupported back to its previous value under the GPU's Device Parameters\\Interrupt Management key (modern drivers default to 1).",
        _ => null,
    };
}
