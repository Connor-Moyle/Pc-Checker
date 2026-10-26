using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Restore;

namespace PcChecker.Core.WindowsFeatures;

/// <summary>
/// Windows implementation of <see cref="IWindowsFeatureService"/> on top of the DISM and Appx PowerShell cmdlets.
/// Every change runs through <see cref="IChangeGuard"/> (module "Features") so a restore point is taken first and the
/// action is journaled. Cmdlet failures become failed results with the cmdlet's own message; nothing throws to the UI.
/// </summary>
public sealed class WindowsFeatureService : IWindowsFeatureService
{
    public const string ModuleName = "Features";

    private static readonly TimeSpan ListTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan ChangeTimeout = TimeSpan.FromMinutes(20);

    private readonly IChangeGuard _guard;
    private readonly ILogger<WindowsFeatureService> _logger;

    public WindowsFeatureService(IChangeGuard guard, ILogger<WindowsFeatureService> logger)
    {
        _guard = guard;
        _logger = logger;
    }

    public async Task<IReadOnlyList<WindowsFeature>> ListAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return [];
        }

        var all = new List<WindowsFeature>();

        progress?.Report(new ProgressUpdate("Reading optional features", 10, "Get-WindowsOptionalFeature"));
        all.AddRange(await ListOptionalFeaturesAsync(cancellationToken).ConfigureAwait(false));

        progress?.Report(new ProgressUpdate("Reading capabilities", 45, "Get-WindowsCapability"));
        all.AddRange(await ListCapabilitiesAsync(cancellationToken).ConfigureAwait(false));

        progress?.Report(new ProgressUpdate("Reading built-in apps", 75, "Get-AppxPackage"));
        all.AddRange(await ListInboxAppsAsync(cancellationToken).ConfigureAwait(false));

        progress?.Report(new ProgressUpdate("Done", 100));
        _logger.LogInformation("Windows features listed: {Count} items.", all.Count);
        return all;
    }

    public Task<OperationResult> EnableAsync(WindowsFeature feature, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feature);
        return feature.Kind switch
        {
            FeatureKind.OptionalFeature => RunChangeAsync($"Enable {feature.DisplayName}", feature,
                $"Enable-WindowsOptionalFeature -Online -FeatureName {PowerShellRunner.Quote(feature.Name)} -NoRestart -All -WarningAction SilentlyContinue",
                $"Disable the feature again from this page (Disable-WindowsOptionalFeature {feature.Name}).", cancellationToken),
            FeatureKind.Capability => RunChangeAsync($"Install {feature.DisplayName}", feature,
                $"Add-WindowsCapability -Online -Name {PowerShellRunner.Quote(feature.Name)} -WarningAction SilentlyContinue",
                $"Remove the capability again from this page (Remove-WindowsCapability {feature.Name}).", cancellationToken),
            _ => Task.FromResult(OperationResult.Fail("Built-in apps are reinstalled from the Microsoft Store, not from here.")),
        };
    }

    public Task<OperationResult> DisableAsync(WindowsFeature feature, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feature);
        return feature.Kind switch
        {
            FeatureKind.OptionalFeature => RunChangeAsync($"Disable {feature.DisplayName}", feature,
                $"Disable-WindowsOptionalFeature -Online -FeatureName {PowerShellRunner.Quote(feature.Name)} -NoRestart -WarningAction SilentlyContinue",
                $"Enable the feature again from this page (Enable-WindowsOptionalFeature {feature.Name}).", cancellationToken),
            FeatureKind.Capability => RunChangeAsync($"Remove {feature.DisplayName}", feature,
                $"Remove-WindowsCapability -Online -Name {PowerShellRunner.Quote(feature.Name)} -WarningAction SilentlyContinue",
                $"Install the capability again from this page (Add-WindowsCapability {feature.Name}).", cancellationToken),
            _ => RemoveInboxAppAsync(feature, false, cancellationToken),
        };
    }

    public Task<OperationResult> RemoveInboxAppAsync(WindowsFeature feature, bool allUsers, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feature);
        if (feature.Kind != FeatureKind.InboxApp)
        {
            return Task.FromResult(OperationResult.Fail($"{feature.DisplayName} is not a built-in app."));
        }

        if (!feature.Removable)
        {
            return Task.FromResult(OperationResult.Fail($"{feature.DisplayName} is marked as not removable by Windows or by PC Checker's safety list."));
        }

        var name = PowerShellRunner.Quote(feature.Name);
        var script = allUsers
            ? $"$ErrorActionPreference = 'Stop'; Get-AppxPackage -AllUsers -Name {name} | Remove-AppxPackage -AllUsers; " +
              $"Get-AppxProvisionedPackage -Online | Where-Object {{ $_.DisplayName -eq {name} }} | Remove-AppxProvisionedPackage -Online | Out-Null; " +
              "[pscustomobject]@{ RestartNeeded = $false; Removed = $true } | ConvertTo-Json -Compress"
            : $"$ErrorActionPreference = 'Stop'; Get-AppxPackage -Name {name} | Remove-AppxPackage; " +
              "[pscustomobject]@{ RestartNeeded = $false; Removed = $true } | ConvertTo-Json -Compress";

        var options = new ChangeOptions(null, null, ModuleName, feature.Name, Reversible: true,
            UndoHint: "Reinstall the app from the Microsoft Store (search for its name).")
        { OnRestorePointFailed = RestorePointFailedBehavior.Abort };

        return _guard.RunGuardedAsync($"Remove {feature.DisplayName}{(allUsers ? " for all users" : string.Empty)}", options,
            ct => ExecuteAsync(script, $"{feature.DisplayName} removed{(allUsers ? " for all users" : string.Empty)}.", ct), null, cancellationToken);
    }

    private Task<OperationResult> RunChangeAsync(string description, WindowsFeature feature, string command, string undoHint, CancellationToken cancellationToken)
    {
        var script = $"$ErrorActionPreference = 'Stop'; $r = {command}; " +
                     "[pscustomobject]@{ RestartNeeded = [bool]$r.RestartNeeded; Online = [bool]$r.Online } | ConvertTo-Json -Compress";
        var options = new ChangeOptions(null, null, ModuleName, feature.Name, Reversible: true, UndoHint: undoHint)
        { OnRestorePointFailed = RestorePointFailedBehavior.Abort };

        return _guard.RunGuardedAsync(description, options,
            ct => ExecuteAsync(script, description + " completed.", ct), null, cancellationToken);
    }

    private async Task<OperationResult> ExecuteAsync(string script, string successMessage, CancellationToken cancellationToken)
    {
        var result = await PowerShellRunner.RunAsync(script, ChangeTimeout, cancellationToken).ConfigureAwait(false);
        if (result.TimedOut)
        {
            return OperationResult.Fail("Windows took too long to apply the change. It may still finish in the background; check again after a restart.");
        }

        if (!result.Success)
        {
            var error = FirstLine(result.StdErr) ?? FirstLine(result.StdOut) ?? $"PowerShell exit code {result.ExitCode}.";
            _logger.LogWarning("Feature change failed: {Error}", error);
            return OperationResult.Fail(Friendly(error));
        }

        bool restart = FeatureStateParser.ParseRestartNeeded(result.StdOut) || FeatureStateParser.ParseRestartNeeded(result.StdErr);
        return OperationResult.Ok(restart ? successMessage + " Restart Windows to finish." : successMessage, restart);
    }

    private async Task<IReadOnlyList<WindowsFeature>> ListOptionalFeaturesAsync(CancellationToken cancellationToken)
    {
        var rows = await QueryAsync<List<OptionalFeatureRow>>("Optional features",
            "Get-WindowsOptionalFeature -Online | Select-Object FeatureName, @{ n = 'State'; e = { $_.State.ToString() } }",
            cancellationToken).ConfigureAwait(false);
        return FeatureMapper.MapOptionalFeatures(rows);
    }

    private async Task<IReadOnlyList<WindowsFeature>> ListCapabilitiesAsync(CancellationToken cancellationToken)
    {
        var rows = await QueryAsync<List<CapabilityRow>>("Capabilities",
            "Get-WindowsCapability -Online | Where-Object { $_.State -ne 'NotPresent' } | Select-Object Name, @{ n = 'State'; e = { $_.State.ToString() } }",
            cancellationToken).ConfigureAwait(false);
        return FeatureMapper.MapCapabilities(rows);
    }

    private async Task<IReadOnlyList<WindowsFeature>> ListInboxAppsAsync(CancellationToken cancellationToken)
    {
        // Version and SignatureKind are projected to strings: Windows PowerShell 5.1 serializes System.Version and enums
        // as nested objects, which would not deserialize into the string row properties.
        var rows = await QueryAsync<List<AppxPackageRow>>("Built-in apps",
            "Get-AppxPackage -AllUsers | Where-Object { $_.SignatureKind -eq 'System' -or $_.Publisher -like '*Microsoft*' } " +
            "| Select-Object Name, PackageFullName, PackageFamilyName, @{ n = 'Version'; e = { [string]$_.Version } }, Publisher, NonRemovable, IsFramework, @{ n = 'SignatureKind'; e = { [string]$_.SignatureKind } }",
            cancellationToken).ConfigureAwait(false);
        var provisioned = await QueryAsync<List<ProvisionedPackageRow>>("Provisioned apps",
            "Get-AppxProvisionedPackage -Online | Select-Object DisplayName, PackageName",
            cancellationToken).ConfigureAwait(false);
        return FeatureMapper.MapInboxApps(rows, provisioned);
    }

    /// <summary>
    /// Runs a listing cmdlet and deserializes its JSON. A failed, timed-out or unparseable query returns null and is
    /// logged as a warning with the cmdlet's first error line, so an empty tab can be explained from the Logs page.
    /// </summary>
    private async Task<T?> QueryAsync<T>(string section, string command, CancellationToken cancellationToken) where T : class
    {
        PowerShellResult result;
        try
        {
            result = await PowerShellRunner.RunAsync($"({command}) | ConvertTo-Json -Depth 4 -Compress", ListTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Section}: the PowerShell query could not be started; the list will be empty.", section);
            return null;
        }

        if (result.TimedOut)
        {
            _logger.LogWarning("{Section}: the query did not finish within {Minutes} minutes (DISM or the Appx service may be busy); the list will be empty.", section, ListTimeout.TotalMinutes);
            return null;
        }

        if (!result.Success)
        {
            var error = FirstLine(result.StdErr) ?? FirstLine(result.StdOut) ?? $"PowerShell exit code {result.ExitCode}.";
            _logger.LogWarning("{Section}: the query failed, so the list will be empty. {Error}", section, error);
            return null;
        }

        if (string.IsNullOrWhiteSpace(result.StdOut))
        {
            _logger.LogInformation("{Section}: the query returned no items.", section);
            return null;
        }

        var parsed = PowerShellRunner.ParseJson<T>(result.StdOut);
        if (parsed is null)
        {
            _logger.LogWarning("{Section}: the query output could not be read as JSON, so the list will be empty. First line: {Line}", section, FirstLine(result.StdOut));
        }

        return parsed;
    }

    private static string? FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                return trimmed;
            }
        }

        return null;
    }

    private static string Friendly(string error)
    {
        if (error.Contains("0x800f081f", StringComparison.OrdinalIgnoreCase) || error.Contains("source files could not be found", StringComparison.OrdinalIgnoreCase))
        {
            return "Windows could not download the files it needs. Check your internet connection or Windows Update settings and try again.";
        }

        if (error.Contains("0x80070422", StringComparison.OrdinalIgnoreCase))
        {
            return "The Windows Update service is disabled, so Windows cannot download this feature. Enable the Windows Update service and try again.";
        }

        if (error.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) || error.Contains("elevated", StringComparison.OrdinalIgnoreCase))
        {
            return "Windows refused the change. Run PC Checker as administrator and try again.";
        }

        return error;
    }
}
