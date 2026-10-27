using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using PcChecker.Core.Common;
using PcChecker.Core.Performance;

namespace PcChecker.Core.Optimization;

/// <summary>The per-action implementations behind <see cref="WindowsOptimizationService"/>.</summary>
public sealed partial class WindowsOptimizationService
{
    private const string DesktopKey = @"Control Panel\Desktop";
    private const string WindowMetricsKey = @"Control Panel\Desktop\WindowMetrics";
    private const string AdvancedExplorerKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string ContentDeliveryKey = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
    private const string DataCollectionPolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\DataCollection";
    private const string PrivacyKey = @"Software\Microsoft\Windows\CurrentVersion\Privacy";
    private const string GameConfigStoreKey = @"System\GameConfigStore";

    /// <summary>Where the plan that was active before a power.* action is remembered, so Revert survives an app restart.</summary>
    public const string PreviousPowerSchemeKey = @"Software\PcChecker\Optimization";
    public const string PreviousPowerSchemeValue = "PreviousPowerScheme";
    public const string PreviousPowerSchemeNameValue = "PreviousPowerSchemeName";
    private static readonly TimeSpan LongToolTimeout = TimeSpan.FromMinutes(45);

    private Dictionary<string, Handler> BuildHandlers() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["power.highperformance"] = new(ct => PowerPlanStateAsync(PowerPlans.HighPerformance, ct), ct => SetPowerPlanAsync(PowerPlans.HighPerformance, ct), RevertPowerPlanAsync),
        ["power.balanced"] = new(ct => PowerPlanStateAsync(PowerPlans.Balanced, ct), ct => SetPowerPlanAsync(PowerPlans.Balanced, ct), RevertPowerPlanAsync),
        ["power.ultimate"] = new(ct => PowerPlanStateAsync(PowerPlans.Ultimate, ct), SetUltimatePlanAsync, RevertPowerPlanAsync),

        ["visuals.performance"] = new(_ => Task.FromResult(VisualEffectsState("visuals.performance", 2)), ct => ApplyVisualEffectsAsync(bestPerformance: true), ct => ApplyVisualEffectsAsync(bestPerformance: false)),
        ["visuals.balanced"] = new(_ => Task.FromResult(VisualEffectsState("visuals.balanced", 0)), ct => ApplyVisualEffectsAsync(bestPerformance: false), ct => ApplyVisualEffectsAsync(bestPerformance: true)),
        ["transparency.disable"] = DwordToggle("transparency.disable", RegistryHive.CurrentUser, WindowsSystemProbe.Personalize, "EnableTransparency", 0, 1, missingIsApplied: false, "Transparency off", "Transparency on"),
        ["animations.disable"] = new(_ => Task.FromResult(AnimationsState()), _ => Task.FromResult(SetAnimations(enabled: false)), _ => Task.FromResult(SetAnimations(enabled: true))),

        ["faststartup.disable"] = DwordToggle("faststartup.disable", RegistryHive.LocalMachine, WindowsSystemProbe.SessionManagerPower, "HiberbootEnabled", 0, 1, missingIsApplied: false, "Fast startup off", "Fast startup on"),
        ["faststartup.enable"] = DwordToggle("faststartup.enable", RegistryHive.LocalMachine, WindowsSystemProbe.SessionManagerPower, "HiberbootEnabled", 1, 0, missingIsApplied: true, "Fast startup on", "Fast startup off"),
        ["hibernation.disable"] = new(_ => Task.FromResult(HibernationState()), ct => SetHibernationAsync(false, ct), ct => SetHibernationAsync(true, ct)),

        ["gamemode.enable"] = DwordToggle("gamemode.enable", RegistryHive.CurrentUser, WindowsSystemProbe.GameBar, "AutoGameModeEnabled", 1, 0, missingIsApplied: true, "Game Mode on", "Game Mode off"),
        ["gamebar.disable"] = new(_ => Task.FromResult(GameBarState()), _ => Task.FromResult(SetGameBar(enabled: false)), _ => Task.FromResult(SetGameBar(enabled: true))),
        ["backgroundapps.disable"] = DwordToggle("backgroundapps.disable", RegistryHive.CurrentUser, WindowsSystemProbe.BackgroundApps, "GlobalUserDisabled", 1, 0, missingIsApplied: false, "Background apps blocked", "Background apps allowed"),
        ["storagesense.enable"] = DwordToggle("storagesense.enable", RegistryHive.CurrentUser, WindowsSystemProbe.StorageSensePolicy, "01", 1, 0, missingIsApplied: false, "Storage Sense on", "Storage Sense off"),
        ["notifications.focus"] = new(_ => Task.FromResult(NotificationsState()), _ => Task.FromResult(SetNotificationNoise(quiet: true)), _ => Task.FromResult(SetNotificationNoise(quiet: false))),

        ["trim.enable"] = new(TrimStateAsync, ct => SetTrimAsync(true, ct), ct => SetTrimAsync(false, ct)),
        ["defrag.optimize"] = new(_ => Task.FromResult(new ActionState("defrag.optimize", null, "Runs on demand")), OptimizeDrivesAsync, null),
        ["tempfiles.clean"] = new(_ => Task.FromResult(new ActionState("tempfiles.clean", null, "Opens the Cleanup page")),
            _ => Task.FromResult(OperationResult.Fail("Use the Cleanup page to choose what to remove.")), null),

        ["dns.cloudflare"] = new(DnsStateAsync, ct => SetDnsAsync(cloudflare: true, ct), ct => SetDnsAsync(cloudflare: false, ct)),
        ["telemetry.minimal"] = new(_ => Task.FromResult(TelemetryState()), ct => SetTelemetryAsync(minimal: true, ct), ct => SetTelemetryAsync(minimal: false, ct)),
        ["sysmain.disable"] = new(ct => ServiceDisabledStateAsync("sysmain.disable", "SysMain", ct), ct => SetServiceAsync("SysMain", "disabled", stop: true, ct), ct => SetServiceAsync("SysMain", "auto", stop: false, ct)),
        ["search.indexing.disable"] = new(ct => ServiceDisabledStateAsync("search.indexing.disable", "WSearch", ct), ct => SetServiceAsync("WSearch", "disabled", stop: true, ct), ct => SetServiceAsync("WSearch", "delayed-auto", stop: false, ct)),

        ["dism.restorehealth"] = new(_ => Task.FromResult(new ActionState("dism.restorehealth", null, "Runs on demand (5–20 min)")), RunDismAsync, null),
        ["sfc.scannow"] = new(_ => Task.FromResult(new ActionState("sfc.scannow", null, "Runs on demand (5–15 min)")), RunSfcAsync, null),
        ["network.reset"] = new(_ => Task.FromResult(new ActionState("network.reset", null, "Runs on demand; restart afterwards")), ResetNetworkAsync, null),
    };

    // ---- Generic registry toggles -------------------------------------------------------------------------------

    private static Handler DwordToggle(string id, RegistryHive hive, string key, string name, int appliedValue, int revertedValue,
        bool? missingIsApplied, string appliedText, string revertedText)
    {
        return new Handler(
            _ =>
            {
                if (!PlatformGuard.IsWindows)
                {
                    return Task.FromResult(new ActionState(id, null, "Unknown"));
                }

                var value = RegistryTweaks.GetDword(hive, key, name);
                bool? applied = value is null ? missingIsApplied : value == appliedValue;
                return Task.FromResult(new ActionState(id, applied, applied switch { true => appliedText, false => revertedText, null => "Unknown" }));
            },
            _ => Task.FromResult(Describe(RegistryTweaks.SetDword(hive, key, name, appliedValue), appliedText)),
            _ => Task.FromResult(Describe(RegistryTweaks.SetDword(hive, key, name, revertedValue), revertedText)));
    }

    private static OperationResult Describe(OperationResult result, string successText) =>
        result.Success ? OperationResult.Ok(successText + ".") : result;

    // ---- Power plans ---------------------------------------------------------------------------------------------

    private static async Task<ActionState> PowerPlanStateAsync(Guid target, CancellationToken ct)
    {
        var active = await WindowsSystemProbe.ActivePowerSchemeAsync(ct).ConfigureAwait(false);
        if (active is null)
        {
            return new ActionState(ActionIdFor(target), null, "Unknown");
        }

        bool applied = target == PowerPlans.Ultimate
            ? active.Name.Contains("Ultimate", StringComparison.OrdinalIgnoreCase)
            : active.Id == target;
        return new ActionState(ActionIdFor(target), applied, $"Active: {active.Name}");
    }

    private static string ActionIdFor(Guid plan) =>
        plan == PowerPlans.HighPerformance ? "power.highperformance" : plan == PowerPlans.Ultimate ? "power.ultimate" : "power.balanced";

    private async Task<OperationResult> SetPowerPlanAsync(Guid plan, CancellationToken ct)
    {
        var current = await WindowsSystemProbe.ActivePowerSchemeAsync(ct).ConfigureAwait(false);
        RememberPreviousScheme(current, plan);
        var result = await ExternalCommand.RunAsync("powercfg.exe", $"/setactive {plan}", ct, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        if (!result.Success && plan == PowerPlans.HighPerformance)
        {
            // Some OEM images hide the High performance plan; recreate it from the template.
            var dup = await ExternalCommand.RunAsync("powercfg.exe", $"-duplicatescheme {plan}", ct, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            var created = PowerPlans.ParseScheme(dup.Output);
            if (created is not null)
            {
                result = await ExternalCommand.RunAsync("powercfg.exe", $"/setactive {created.Id}", ct, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            }
        }

        return result.Success
            ? OperationResult.Ok($"The {PowerPlans.NameFor(plan)} power plan is now active.")
            : OperationResult.Fail($"powercfg could not switch the plan: {result.LastLine}");
    }

    private async Task<OperationResult> SetUltimatePlanAsync(CancellationToken ct)
    {
        var list = await ExternalCommand.RunAsync("powercfg.exe", "/list", ct, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        var existing = PowerPlans.ParseList(list.Output).FirstOrDefault(s => s.Name.Contains("Ultimate", StringComparison.OrdinalIgnoreCase));
        Guid target;
        if (existing is not null)
        {
            target = existing.Id;
        }
        else
        {
            var dup = await ExternalCommand.RunAsync("powercfg.exe", $"-duplicatescheme {PowerPlans.Ultimate}", ct, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            var created = PowerPlans.ParseScheme(dup.Output);
            if (created is null)
            {
                return OperationResult.Fail($"The Ultimate Performance plan could not be created: {dup.LastLine}");
            }

            target = created.Id;
        }

        var current = await WindowsSystemProbe.ActivePowerSchemeAsync(ct).ConfigureAwait(false);
        RememberPreviousScheme(current, target);
        var result = await ExternalCommand.RunAsync("powercfg.exe", $"/setactive {target}", ct, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        return result.Success
            ? OperationResult.Ok("The Ultimate Performance power plan is now active.")
            : OperationResult.Fail($"powercfg could not switch the plan: {result.LastLine}");
    }

    /// <summary>
    /// Keeps the plan that was active before the first power.* change, in memory and in HKCU so that Revert still
    /// finds a custom or OEM plan after the app has been restarted. Switching between the app's own plans does not
    /// overwrite the remembered original.
    /// </summary>
    private void RememberPreviousScheme(PowerScheme? current, Guid target)
    {
        if (current is null || current.Id == target)
        {
            return;
        }

        if (_previousPowerScheme is null && ReadPersistedPreviousScheme() is null)
        {
            _previousPowerScheme = current.Id;
            RegistryTweaks.SetString(RegistryHive.CurrentUser, PreviousPowerSchemeKey, PreviousPowerSchemeValue, current.Id.ToString());
            RegistryTweaks.SetString(RegistryHive.CurrentUser, PreviousPowerSchemeKey, PreviousPowerSchemeNameValue, current.Name);
        }
        else if (_previousPowerScheme is null)
        {
            _previousPowerScheme = ReadPersistedPreviousScheme();
        }
    }

    private static Guid? ReadPersistedPreviousScheme() =>
        Guid.TryParse(RegistryTweaks.GetString(RegistryHive.CurrentUser, PreviousPowerSchemeKey, PreviousPowerSchemeValue), out var id) ? id : null;

    private void ForgetPreviousScheme()
    {
        _previousPowerScheme = null;
        RegistryTweaks.DeleteValue(RegistryHive.CurrentUser, PreviousPowerSchemeKey, PreviousPowerSchemeValue);
        RegistryTweaks.DeleteValue(RegistryHive.CurrentUser, PreviousPowerSchemeKey, PreviousPowerSchemeNameValue);
    }

    private async Task<OperationResult> RevertPowerPlanAsync(CancellationToken ct)
    {
        var remembered = _previousPowerScheme ?? ReadPersistedPreviousScheme();
        var target = remembered ?? PowerPlans.Balanced;
        string? name = remembered is null ? null : RegistryTweaks.GetString(RegistryHive.CurrentUser, PreviousPowerSchemeKey, PreviousPowerSchemeNameValue);
        var result = await ExternalCommand.RunAsync("powercfg.exe", $"/setactive {target}", ct, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        if (!result.Success && target != PowerPlans.Balanced)
        {
            // The remembered plan no longer exists (deleted or a different OEM image): fall back to Balanced.
            result = await ExternalCommand.RunAsync("powercfg.exe", $"/setactive {PowerPlans.Balanced}", ct, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            name = null;
            remembered = null;
        }

        if (!result.Success)
        {
            return OperationResult.Fail($"powercfg failed: {result.LastLine}");
        }

        ForgetPreviousScheme();
        return OperationResult.Ok(remembered is null
            ? "The Balanced power plan is active again."
            : $"The previous power plan ({name ?? PowerPlans.NameFor(remembered.Value)}) is active again.");
    }

    // ---- Visual effects, animations, transparency ------------------------------------------------------------------

    private static ActionState VisualEffectsState(string id, int appliedValue)
    {
        var value = WindowsSystemProbe.VisualEffectsSetting();
        string text = value switch
        {
            0 => "Let Windows choose",
            1 => "Best appearance",
            2 => "Best performance",
            3 => "Custom",
            _ => "Unknown",
        };
        return new ActionState(id, value is null ? null : value == appliedValue, text);
    }

    private static Task<OperationResult> ApplyVisualEffectsAsync(bool bestPerformance)
    {
        var results = new List<OperationResult>
        {
            RegistryTweaks.SetDword(RegistryHive.CurrentUser, WindowsSystemProbe.ExplorerVisualEffects, "VisualFXSetting", bestPerformance ? 2 : 0),
            RegistryTweaks.SetString(RegistryHive.CurrentUser, WindowMetricsKey, "MinAnimate", bestPerformance ? "0" : "1"),
            RegistryTweaks.SetDword(RegistryHive.CurrentUser, AdvancedExplorerKey, "TaskbarAnimations", bestPerformance ? 0 : 1),
            RegistryTweaks.SetDword(RegistryHive.CurrentUser, AdvancedExplorerKey, "ListviewAlphaSelect", bestPerformance ? 0 : 1),
            RegistryTweaks.SetDword(RegistryHive.CurrentUser, AdvancedExplorerKey, "ListviewShadow", bestPerformance ? 0 : 1),
            RegistryTweaks.SetDword(RegistryHive.CurrentUser, WindowsSystemProbe.Personalize, "EnableTransparency", bestPerformance ? 0 : 1),
            RegistryTweaks.SetString(RegistryHive.CurrentUser, DesktopKey, "DragFullWindows", bestPerformance ? "0" : "1"),
        };
        var failed = results.FirstOrDefault(r => !r.Success);
        return Task.FromResult(failed ?? OperationResult.Ok(bestPerformance
            ? "Visual effects set for best performance. Sign out and back in to see every change."
            : "Visual effects restored to the Windows defaults. Sign out and back in to see every change."));
    }

    private static ActionState AnimationsState()
    {
        if (!PlatformGuard.IsWindows)
        {
            return new ActionState("animations.disable", null, "Unknown");
        }

        var value = RegistryTweaks.GetString(RegistryHive.CurrentUser, WindowMetricsKey, "MinAnimate");
        bool? applied = value is null ? false : value.Trim() == "0";
        return new ActionState("animations.disable", applied, applied == true ? "Animations off" : "Animations on");
    }

    private static OperationResult SetAnimations(bool enabled)
    {
        var a = RegistryTweaks.SetString(RegistryHive.CurrentUser, WindowMetricsKey, "MinAnimate", enabled ? "1" : "0");
        var b = RegistryTweaks.SetDword(RegistryHive.CurrentUser, AdvancedExplorerKey, "TaskbarAnimations", enabled ? 1 : 0);
        return !a.Success ? a : !b.Success ? b : OperationResult.Ok(enabled ? "Window animations are on again." : "Window animations are off. Takes effect after you sign out and back in.");
    }

    // ---- Hibernation, Game Bar, notifications --------------------------------------------------------------------

    private static ActionState HibernationState()
    {
        var enabled = WindowsSystemProbe.HibernationEnabled();
        return new ActionState("hibernation.disable", enabled is null ? null : !enabled.Value, enabled switch { true => "Hibernation on", false => "Hibernation off", null => "Unknown" });
    }

    private static async Task<OperationResult> SetHibernationAsync(bool enabled, CancellationToken ct)
    {
        var result = await ExternalCommand.RunAsync("powercfg.exe", enabled ? "/hibernate on" : "/hibernate off", ct, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        return result.Success
            ? OperationResult.Ok(enabled ? "Hibernation is on again (fast startup can be used)." : "Hibernation is off; hiberfil.sys has been removed and fast startup is off too.")
            : OperationResult.Fail($"powercfg failed: {result.LastLine}");
    }

    private static ActionState GameBarState()
    {
        var enabled = WindowsSystemProbe.GameBarEnabled();
        return new ActionState("gamebar.disable", enabled is null ? null : !enabled.Value, enabled switch { true => "Game Bar on", false => "Game Bar off", null => "Unknown" });
    }

    private static OperationResult SetGameBar(bool enabled)
    {
        var a = RegistryTweaks.SetDword(RegistryHive.CurrentUser, WindowsSystemProbe.GameDvr, "AppCaptureEnabled", enabled ? 1 : 0);
        var b = RegistryTweaks.SetDword(RegistryHive.CurrentUser, GameConfigStoreKey, "GameDVR_Enabled", enabled ? 1 : 0);
        return !a.Success ? a : !b.Success ? b : OperationResult.Ok(enabled ? "Xbox Game Bar is on again." : "Xbox Game Bar and background recording are off.");
    }

    private static ActionState NotificationsState()
    {
        if (!PlatformGuard.IsWindows)
        {
            return new ActionState("notifications.focus", null, "Unknown");
        }

        var tips = RegistryTweaks.GetDword(RegistryHive.CurrentUser, ContentDeliveryKey, "SubscribedContent-338389Enabled");
        bool applied = tips == 0;
        return new ActionState("notifications.focus", applied, applied ? "Tips and suggestions off" : "Tips and suggestions on");
    }

    private static OperationResult SetNotificationNoise(bool quiet)
    {
        int v = quiet ? 0 : 1;
        var results = new[]
        {
            RegistryTweaks.SetDword(RegistryHive.CurrentUser, ContentDeliveryKey, "SubscribedContent-338389Enabled", v),
            RegistryTweaks.SetDword(RegistryHive.CurrentUser, ContentDeliveryKey, "SubscribedContent-310093Enabled", v),
            RegistryTweaks.SetDword(RegistryHive.CurrentUser, ContentDeliveryKey, "SoftLandingEnabled", v),
            RegistryTweaks.SetDword(RegistryHive.CurrentUser, ContentDeliveryKey, "SubscribedContent-338388Enabled", v),
        };
        return results.FirstOrDefault(r => !r.Success) ?? OperationResult.Ok(quiet ? "Tips, suggestions and welcome notifications are off." : "Windows tips and suggestions are on again.");
    }

    // ---- Storage -------------------------------------------------------------------------------------------------

    private static async Task<ActionState> TrimStateAsync(CancellationToken ct)
    {
        var enabled = await WindowsSystemProbe.TrimEnabledAsync(ct).ConfigureAwait(false);
        return new ActionState("trim.enable", enabled, enabled switch { true => "TRIM on", false => "TRIM off", null => "Unknown" });
    }

    private static async Task<OperationResult> SetTrimAsync(bool enabled, CancellationToken ct)
    {
        var result = await ExternalCommand.RunAsync("fsutil.exe", $"behavior set DisableDeleteNotify {(enabled ? 0 : 1)}", ct, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        return result.Success ? OperationResult.Ok(enabled ? "TRIM is on for SSDs." : "TRIM is off.") : OperationResult.Fail($"fsutil failed: {result.LastLine}");
    }

    private async Task<OperationResult> OptimizeDrivesAsync(CancellationToken ct)
    {
        Emit("Optimizing all fixed drives (TRIM for SSDs, defragment for hard disks)…");
        var result = await ExternalCommand.RunAsync("defrag.exe", "/C /O /U /V", ct, TimeSpan.FromHours(2), Emit).ConfigureAwait(false);
        return result.Success ? OperationResult.Ok("All drives have been optimized.") : OperationResult.Fail($"Drive optimization did not complete: {result.LastLine}");
    }

    // ---- Network -------------------------------------------------------------------------------------------------

    private static async Task<ActionState> DnsStateAsync(CancellationToken ct)
    {
        const string command = "(Get-DnsClientServerAddress -AddressFamily IPv4 | Where-Object { $_.ServerAddresses -contains '1.1.1.1' } | Measure-Object).Count";
        var result = await PowerShellRunner.RunAsync(command, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        if (!result.Success || !int.TryParse(result.StdOut.Trim(), out int count))
        {
            return new ActionState("dns.cloudflare", null, "Unknown");
        }

        return new ActionState("dns.cloudflare", count > 0, count > 0 ? "Cloudflare DNS in use" : "Automatic (DHCP) DNS");
    }

    private static async Task<OperationResult> SetDnsAsync(bool cloudflare, CancellationToken ct)
    {
        string command = cloudflare
            ? "Get-NetAdapter -Physical | Where-Object Status -eq 'Up' | ForEach-Object { Set-DnsClientServerAddress -InterfaceIndex $_.ifIndex -ServerAddresses 1.1.1.1,1.0.0.1 }; Clear-DnsClientCache"
            : "Get-NetAdapter -Physical | ForEach-Object { Set-DnsClientServerAddress -InterfaceIndex $_.ifIndex -ResetServerAddresses }; Clear-DnsClientCache";
        var result = await PowerShellRunner.RunAsync(command, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        return result.Success
            ? OperationResult.Ok(cloudflare ? "Active network adapters now use Cloudflare DNS (1.1.1.1 / 1.0.0.1)." : "DNS is back to automatic (DHCP).")
            : OperationResult.Fail($"DNS change failed: {Last(result.StdErr, result.StdOut)}");
    }

    private async Task<OperationResult> ResetNetworkAsync(CancellationToken ct)
    {
        var steps = new (string Exe, string Args)[]
        {
            ("netsh.exe", "winsock reset"),
            ("netsh.exe", "int ip reset"),
            ("ipconfig.exe", "/flushdns"),
        };
        var warnings = new List<string>();
        for (int i = 0; i < steps.Length; i++)
        {
            var (exe, args) = steps[i];
            Emit($"> {exe} {args}");
            var result = await ExternalCommand.RunAsync(exe, args, ct, TimeSpan.FromMinutes(2), Emit, Storage.StorageCommandRunner.OemEncoding()).ConfigureAwait(false);
            if (result.Success)
            {
                continue;
            }

            if (i == 0)
            {
                // Nothing has been changed yet, so stopping here is safe.
                return OperationResult.Fail($"{exe} {args} failed: {result.LastLine}");
            }

            // Once Winsock has been reset the remaining steps still run and a restart is needed either way. netsh int
            // ip reset routinely exits non-zero for one key it cannot reset, with a message in the display language.
            warnings.Add($"{exe} {args} reported a problem (exit code {result.ExitCode}): {result.LastLine}");
        }

        string message = "Network stack reset. Restart Windows to finish; you may need to reconnect to Wi-Fi.";
        if (warnings.Count > 0)
        {
            message += " Some steps reported a problem, which is common and usually harmless: " + string.Join(" ", warnings);
        }

        return OperationResult.Ok(message, rebootRequired: true);
    }

    // ---- Privacy and services ------------------------------------------------------------------------------------

    private static ActionState TelemetryState()
    {
        if (!PlatformGuard.IsWindows)
        {
            return new ActionState("telemetry.minimal", null, "Unknown");
        }

        var level = RegistryTweaks.GetDword(RegistryHive.LocalMachine, DataCollectionPolicyKey, "AllowTelemetry");
        bool applied = level is 0 or 1;
        return new ActionState("telemetry.minimal", applied, applied ? "Diagnostic data: required only" : "Diagnostic data: Windows default");
    }

    private async Task<OperationResult> SetTelemetryAsync(bool minimal, CancellationToken ct)
    {
        OperationResult policy = minimal
            ? RegistryTweaks.SetDword(RegistryHive.LocalMachine, DataCollectionPolicyKey, "AllowTelemetry", 1)
            : RegistryTweaks.DeleteValue(RegistryHive.LocalMachine, DataCollectionPolicyKey, "AllowTelemetry");
        if (!policy.Success)
        {
            return policy;
        }

        RegistryTweaks.SetDword(RegistryHive.CurrentUser, PrivacyKey, "TailoredExperiencesWithDiagnosticDataEnabled", minimal ? 0 : 1);
        var service = await SetServiceAsync("DiagTrack", minimal ? "demand" : "auto", stop: minimal, ct).ConfigureAwait(false);
        return OperationResult.Ok(minimal
            ? "Diagnostic data is set to the required minimum and tailored experiences are off." + (service.Success ? string.Empty : " (DiagTrack service unchanged: " + service.Message + ")")
            : "Diagnostic data settings are back to the Windows default.");
    }

    private static async Task<ActionState> ServiceDisabledStateAsync(string id, string serviceName, CancellationToken ct)
    {
        var rows = await Task.Run(() => WindowsSystemProbe.Query(@"root\cimv2",
            $"SELECT StartMode, State, DelayedAutoStart FROM Win32_Service WHERE Name = '{serviceName}'",
            row => (Mode: ServiceClassifier.NormalizeStartMode(WindowsSystemProbe.Str(row, "StartMode"), WindowsSystemProbe.Bool(row, "DelayedAutoStart") ?? false), State: WindowsSystemProbe.Str(row, "State") ?? "Unknown")), ct).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return new ActionState(id, null, "Service not found");
        }

        var (mode, state) = rows[0];
        bool applied = mode.Equals("Disabled", StringComparison.OrdinalIgnoreCase);
        return new ActionState(id, applied, $"{mode}, {state.ToLowerInvariant()}");
    }

    private static async Task<OperationResult> SetServiceAsync(string serviceName, string scMode, bool stop, CancellationToken ct)
    {
        var config = await ExternalCommand.RunAsync("sc.exe", $"config {serviceName} start= {scMode}", ct, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        if (!config.Success)
        {
            return OperationResult.Fail($"sc config {serviceName} failed: {config.LastLine}");
        }

        if (stop)
        {
            await ExternalCommand.RunAsync("sc.exe", $"stop {serviceName}", ct, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            return OperationResult.Ok($"{serviceName} is set to {ScModeName(scMode)} and has been stopped.");
        }

        if (scMode is "auto" or "delayed-auto")
        {
            await ExternalCommand.RunAsync("sc.exe", $"start {serviceName}", ct, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        }

        return OperationResult.Ok($"{serviceName} is set to {ScModeName(scMode)}.");
    }

    private static string ScModeName(string scMode) => scMode switch
    {
        "auto" => "Automatic",
        "delayed-auto" => "Automatic (Delayed)",
        "demand" => "Manual",
        "disabled" => "Disabled",
        _ => scMode,
    };

    // ---- Repair tools --------------------------------------------------------------------------------------------

    private async Task<OperationResult> RunDismAsync(CancellationToken ct)
    {
        Emit("> DISM /Online /Cleanup-Image /RestoreHealth");
        var result = await ExternalCommand.RunAsync("dism.exe", "/Online /Cleanup-Image /RestoreHealth", ct, LongToolTimeout, Emit).ConfigureAwait(false);
        if (result.TimedOut)
        {
            return OperationResult.Fail("DISM did not finish within 45 minutes and was stopped.");
        }

        return result.Success
            ? OperationResult.Ok("DISM finished: the Windows component store is healthy or has been repaired.")
            : OperationResult.Fail($"DISM reported a problem (exit code {result.ExitCode}): {result.LastLine}");
    }

    private async Task<OperationResult> RunSfcAsync(CancellationToken ct)
    {
        Emit("> sfc /scannow");
        var cbsLog = CbsLogPath();
        long logStart = FileLength(cbsLog);
        var result = await ExternalCommand.RunAsync("sfc.exe", "/scannow", ct, LongToolTimeout, Emit, Encoding.Unicode).ConfigureAwait(false);
        if (result.TimedOut)
        {
            return OperationResult.Fail("System File Checker did not finish within 45 minutes and was stopped.");
        }

        // sfc prints its verdict in the display language; the [SR] lines it appends to CBS.log are always English.
        var outcome = SfcResultParser.Interpret(result.Combined, ReadLinesSince(cbsLog, logStart));
        switch (outcome)
        {
            case SfcOutcome.NoViolations:
                return OperationResult.Ok("System File Checker found no damaged files.");
            case SfcOutcome.Repaired:
                return OperationResult.Ok("System File Checker repaired damaged files. Restart to complete the repair.", rebootRequired: true);
            case SfcOutcome.ViolationsFound:
                return OperationResult.Ok("System File Checker found and repaired damaged files; check the output above (or CBS.log) in case some could not be fixed. Restart to complete the repair.", rebootRequired: true);
            case SfcOutcome.CouldNotRepair:
                return OperationResult.Fail("System File Checker found damaged files it could not repair. Run \"Repair the Windows image (DISM)\" first, then try again.");
        }

        if (!result.Success)
        {
            return OperationResult.Fail($"System File Checker reported a problem (exit code {result.ExitCode}): {result.LastLine}");
        }

        _logger.LogWarning("sfc /scannow finished (exit 0) but its verdict could not be read from the console output or CBS.log.");
        return OperationResult.Ok("System File Checker finished, but its verdict could not be read (it is printed in the Windows display language). See the output above for the result.");
    }

    private static string CbsLogPath()
    {
        try
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Logs", "CBS", "CBS.log");
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static long FileLength(string path)
    {
        try
        {
            return path.Length > 0 && File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>The [SR] lines appended to the log since <paramref name="offset"/>; null when the log cannot be read.</summary>
    private static List<string>? ReadLinesSince(string path, long offset)
    {
        try
        {
            if (path.Length == 0 || !File.Exists(path))
            {
                return null;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (offset > 0 && offset <= stream.Length)
            {
                stream.Seek(offset, SeekOrigin.Begin); // when the log shrank it was rotated, so it is read from the start
            }

            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var lines = new List<string>();
            while (reader.ReadLine() is { } line)
            {
                if (line.Contains("[SR]", StringComparison.Ordinal))
                {
                    lines.Add(line);
                }
            }

            return lines;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Last(string first, string second)
    {
        var text = string.IsNullOrWhiteSpace(first) ? second : first;
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? "unknown error" : lines[^1];
    }
}
