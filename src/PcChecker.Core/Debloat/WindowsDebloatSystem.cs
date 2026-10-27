using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Optimization;
using PcChecker.Core.Performance;
using PcChecker.Core.Storage;
using PcChecker.Core.WindowsFeatures;

namespace PcChecker.Core.Debloat;

/// <summary>
/// <see cref="IDebloatSystem"/> on Windows: registry through <see cref="WindowsRegistryAccessor"/>, services with
/// <c>sc.exe config</c>, scheduled tasks with <c>schtasks /Change</c> (judged by exit code, so it works on every
/// display language; their messages are decoded from the console OEM code page), app and optional feature changes
/// through <see cref="IWindowsFeatureService"/> (which guards them itself), and reviewed catalog scripts through <see cref="PowerShellRunner"/>. Every query degrades to "unknown".
/// </summary>
public sealed class WindowsDebloatSystem : IDebloatSystem
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ScriptTimeout = TimeSpan.FromMinutes(10);

    private readonly IWindowsFeatureService _features;
    private readonly ILogger _logger;

    public WindowsDebloatSystem(IWindowsFeatureService features, ILogger logger)
    {
        _features = features;
        _logger = logger;
        OsBuild = ReadBuild();
    }

    public IRegistryAccessor Registry { get; } = new WindowsRegistryAccessor();

    public int? OsBuild { get; }

    public async Task<IReadOnlyDictionary<string, bool>?> GetTaskStatesAsync(IReadOnlyCollection<string> taskPaths, CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows || taskPaths.Count == 0)
        {
            return null;
        }

        var list = string.Join(", ", taskPaths.Select(PowerShellRunner.Quote));
        var script =
            $"$paths = @({list}); $out = foreach ($p in $paths) {{ $i = $p.LastIndexOf('\\'); " +
            "$t = Get-ScheduledTask -TaskPath $p.Substring(0, $i + 1) -TaskName $p.Substring($i + 1) -ErrorAction SilentlyContinue; " +
            "if ($t) { [pscustomobject]@{ Path = $p; State = [int]$t.State } } }; ConvertTo-Json -InputObject @($out) -Compress";
        var result = await PowerShellRunner.RunAsync(script, QueryTimeout, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            _logger.LogWarning("Scheduled task query failed: {Error}", FirstLine(result.StdErr));
            return null;
        }

        var rows = PowerShellRunner.ParseJson<List<TaskRow>>(result.StdOut) ?? [];
        // ScheduledTask State: 0 Unknown, 1 Disabled, 2 Queued, 3 Ready, 4 Running.
        return rows.Where(r => !string.IsNullOrEmpty(r.Path))
            .GroupBy(r => r.Path!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().State != 1, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlySet<string>?> GetInstalledAppsAsync(CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return null;
        }

        return await QueryAppNamesAsync("Get-AppxPackage | ForEach-Object { $_.Name }", cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlySet<string>?> GetInstalledAppsForAllUsersAsync(CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return null;
        }

        // Every account's packages plus the provisioned ones new accounts get, as Remove-AppxPackage -AllUsers and
        // Remove-AppxProvisionedPackage act on both.
        const string script =
            "$ErrorActionPreference = 'Stop'; Get-AppxPackage -AllUsers | ForEach-Object { $_.Name }; " +
            "Get-AppxProvisionedPackage -Online | ForEach-Object { $_.DisplayName }";
        return await QueryAppNamesAsync(script, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlySet<string>?> QueryAppNamesAsync(string script, CancellationToken cancellationToken)
    {
        var result = await PowerShellRunner.RunAsync(script, QueryTimeout, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            _logger.LogWarning("Installed app query failed: {Error}", FirstLine(result.StdErr));
            return null;
        }

        return result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<FeatureState> GetOptionalFeatureStateAsync(string featureName, CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return FeatureState.Unknown;
        }

        var script = $"$f = Get-WindowsOptionalFeature -Online -FeatureName {PowerShellRunner.Quote(featureName)} -ErrorAction SilentlyContinue; " +
                     "if ($f) { [string]$f.State } else { 'NotPresent' }";
        var result = await PowerShellRunner.RunAsync(script, QueryTimeout, cancellationToken).ConfigureAwait(false);
        return result.Success ? ParseFeatureState(LastLine(result.StdOut)) : FeatureState.Unknown;
    }

    /// <summary>Maps DISM feature states (Enabled, EnablePending, Disabled, DisabledWithPayloadRemoved, …) to <see cref="FeatureState"/>.</summary>
    public static FeatureState ParseFeatureState(string? text) => text?.Trim() switch
    {
        null or "" => FeatureState.Unknown,
        var s when s.Equals("NotPresent", StringComparison.OrdinalIgnoreCase) => FeatureState.NotPresent,
        var s when s.StartsWith("Enable", StringComparison.OrdinalIgnoreCase) => FeatureState.Enabled,
        var s when s.StartsWith("Disable", StringComparison.OrdinalIgnoreCase) => FeatureState.Disabled,
        _ => FeatureState.Unknown,
    };

    public async Task<bool?> DetectAsync(PowerShellOp operation, CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return null;
        }

        var result = await PowerShellRunner.RunAsync(operation.DetectScript, QueryTimeout, cancellationToken).ConfigureAwait(false);
        return result.Success && bool.TryParse(LastLine(result.StdOut), out bool value) ? value : null;
    }

    /// <summary>
    /// Local printers plus network printer connections (per-user under HKCU\Printers\Connections and machine-wide
    /// ones pushed by policy), so a PC that only prints to a shared printer still counts as having a printer.
    /// </summary>
    public IReadOnlyList<string> GetPrinterNames()
    {
        var local = Registry.GetSubKeyNames(DebloatHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Print\Printers");
        var userConnections = Registry.GetSubKeyNames(DebloatHive.CurrentUser, @"Printers\Connections");
        var machineConnections = Registry.GetSubKeyNames(DebloatHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Print\Connections");
        return local
            .Concat(userConnections.Select(DebloatRules.ConnectionKeyToPrinterName))
            .Concat(machineConnections.Select(DebloatRules.ConnectionKeyToPrinterName))
            .ToList();
    }

    public async Task<OperationResult> SetServiceStartModeAsync(string serviceName, DebloatStartMode mode, CancellationToken cancellationToken)
    {
        var result = await ExternalCommand.RunAsync("sc.exe", $"config \"{serviceName}\" start= {ServiceStartModes.ScArgument(mode)}", cancellationToken, TimeSpan.FromSeconds(30),
            outputEncoding: StorageCommandRunner.OemEncoding()).ConfigureAwait(false);
        return result.Success
            ? OperationResult.Ok($"{serviceName} set to {ServiceStartModes.Display(mode)}.")
            : OperationResult.Fail($"Could not change the {serviceName} service: {Clean(result.LastLine)}");
    }

    public async Task<OperationResult> SetTaskEnabledAsync(string taskPath, bool enabled, CancellationToken cancellationToken)
    {
        var result = await ExternalCommand.RunAsync("schtasks.exe", $"/Change /TN \"{taskPath}\" /{(enabled ? "Enable" : "Disable")}", cancellationToken,
            TimeSpan.FromSeconds(30), outputEncoding: StorageCommandRunner.OemEncoding()).ConfigureAwait(false);
        return result.Success
            ? OperationResult.Ok($"{taskPath} {(enabled ? "enabled" : "disabled")}.")
            : OperationResult.Fail($"Could not change the task {taskPath}: {Clean(result.LastLine)}");
    }

    public Task<OperationResult> RemoveAppAsync(AppxRemovalOp operation, bool allUsers, CancellationToken cancellationToken)
    {
        var entry = InboxAppCatalog.Find(operation.PackageName);
        var feature = new WindowsFeature(operation.PackageName, operation.FriendlyName, entry?.Note, FeatureKind.InboxApp, FeatureState.Enabled,
            entry?.Risk ?? RiskLevel.Safe, InboxAppCatalog.IsRemovable(operation.PackageName, nonRemovable: false), entry?.Note, null, false);
        return _features.RemoveInboxAppAsync(feature, allUsers, cancellationToken);
    }

    public Task<OperationResult> SetOptionalFeatureAsync(OptionalFeatureOp operation, bool enabled, CancellationToken cancellationToken)
    {
        var feature = new WindowsFeature(operation.FeatureName, operation.FriendlyName, null, FeatureKind.OptionalFeature,
            enabled ? FeatureState.Disabled : FeatureState.Enabled, RiskLevel.Moderate, true, null, null, false);
        return enabled ? _features.EnableAsync(feature, cancellationToken) : _features.DisableAsync(feature, cancellationToken);
    }

    public async Task<OperationResult> RunScriptAsync(string script, CancellationToken cancellationToken)
    {
        var result = await PowerShellRunner.RunAsync(script, ScriptTimeout, cancellationToken).ConfigureAwait(false);
        if (result.Success)
        {
            return OperationResult.Ok("Done.");
        }

        return OperationResult.Fail(result.TimedOut ? "The script timed out." : $"The script failed (exit code {result.ExitCode}): {FirstLine(result.StdErr)}");
    }

    private static int? ReadBuild()
    {
        if (!PlatformGuard.IsWindows)
        {
            return null;
        }

        try
        {
            int build = Environment.OSVersion.Version.Build;
            return build > 0 ? build : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string LastLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? string.Empty;

    private static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "no details";

    private static string Clean(string line) => string.IsNullOrWhiteSpace(line) ? "the command failed" : line;

    private sealed class TaskRow
    {
        [JsonPropertyName("Path")]
        public string? Path { get; set; }

        [JsonPropertyName("State")]
        public int State { get; set; }
    }
}
