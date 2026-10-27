using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Logging;
using PcChecker.Core.Optimization;
using PcChecker.Core.Restore;
using PcChecker.Core.Settings;
using PcChecker.Core.Startup;
using PcChecker.Core.WindowsFeatures;

namespace PcChecker.Core.Debloat;

/// <summary>
/// Windows implementation of <see cref="IDebloatService"/>. Every batch runs as one <see cref="IChangeGuard"/> change
/// that always asks for a restore point first (whatever the global setting says), with the previous values captured to
/// <c>debloat-capture.json</c>. A batch that removes apps or runs another one-way change is not started when that
/// restore point cannot be created; the same holds for batches that change a Windows optional feature, as the Windows
/// Features page does. App and feature removals and the linked Optimize page actions in the batch go through instances
/// of those services that share the batch's restore point instead of each trying to create their own.
/// Each tweak gets its own action journal record with the values before and after.
/// </summary>
public sealed class WindowsDebloatService : DebloatServiceBase
{
    public const string ModuleName = "Debloat";

    private readonly IChangeGuard _guard;
    private readonly IActionJournal _journal;
    private readonly IOptimizationService _optimization;
    private readonly IOptimizationService _batchOptimization;
    private readonly ILogger<WindowsDebloatService> _logger;

    public WindowsDebloatService(IChangeGuard guard, IActionJournal journal, IOptimizationService optimization, IWindowsFeatureService features,
        IStartupService startup, ISettingsService settings, ILogger<WindowsDebloatService> logger, ILogger<WindowsFeatureService> featureLogger,
        ILogger<WindowsOptimizationService> optimizationLogger)
        : base(new DebloatEngine(new WindowsDebloatSystem(BatchFeatures(features, guard, featureLogger), logger), new DebloatCaptureBook(CapturePath(settings, logger))))
    {
        _guard = guard;
        _journal = journal;
        _optimization = optimization;
        _batchOptimization = BatchOptimization(optimization, guard, startup, optimizationLogger);
        _logger = logger;
        if (Engine.Captures.LoadWarning is { } warning)
        {
            _logger.LogWarning("Debloat: {Warning}", warning);
        }

        Engine.InstalledVendors = InstalledVendorSoftware;
    }

    private DebloatVendorSoftware? _vendors;

    /// <summary>AMD Software and the NVIDIA app / control panel, found in Program Files.</summary>
    protected override DebloatVendorSoftware InstalledVendorSoftware => _vendors ??= DetectVendors();

    private static DebloatVendorSoftware DetectVendors()
    {
        var found = DebloatVendorSoftware.None;
        try
        {
            if (Overclocking.VendorSoftwareCatalog.AmdAdrenalin.Locate(File.Exists) is not null)
            {
                found |= DebloatVendorSoftware.Amd;
            }

            if (Overclocking.VendorSoftwareCatalog.NvidiaDriverToolsInstalled(File.Exists, Directory.Exists))
            {
                found |= DebloatVendorSoftware.Nvidia;
            }
        }
        catch (Exception)
        {
            // Unknown: offer nothing vendor-specific.
        }

        return found;
    }

    /// <summary>
    /// True when the batch must not run without its restore point: one-way changes (app or OneDrive removal) and Windows
    /// optional feature changes (Recall), which the Windows Features page also never makes without one.
    /// </summary>
    public static bool RequiresRestorePoint(IReadOnlyList<DebloatTweak> tweaks, bool revert) =>
        tweaks.Any(t => t.Operations.Any(o => o is OptionalFeatureOp)) || (!revert && tweaks.Any(t => !t.Reversible));

    public override async Task<IReadOnlyDictionary<string, TweakState>> GetStatesAsync(CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return Tweaks.ToDictionary(t => t.Id, t => TweakState.Unknown(t.Id, "Only available on Windows"), StringComparer.OrdinalIgnoreCase);
        }

        var states = await base.GetStatesAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Debloat states read: {Applied} applied, {Partial} partial, {NotApplied} not applied, {NotApplicable} not applicable.",
            states.Values.Count(s => s.Status == TweakStatus.Applied), states.Values.Count(s => s.Status == TweakStatus.Partial),
            states.Values.Count(s => s.Status == TweakStatus.NotApplied), states.Values.Count(s => s.Status == TweakStatus.NotApplicable));
        return states;
    }

    public override Task<IReadOnlyDictionary<string, TweakState>> GetStatesAsync(IEnumerable<string> tweakIds, CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            IReadOnlyDictionary<string, TweakState> unknown = DebloatRules.WithRelated(tweakIds, Tweaks)
                .ToDictionary(t => t.Id, t => TweakState.Unknown(t.Id, "Only available on Windows"), StringComparer.OrdinalIgnoreCase);
            return Task.FromResult(unknown);
        }

        return base.GetStatesAsync(tweakIds, cancellationToken);
    }

    /// <summary>
    /// Restarts the shell of this session only (other signed-in users keep theirs). Windows restarts the shell itself
    /// (AutoRestartShell); when it does not come back, it is started through a one-off scheduled task as the user who
    /// owned it, with a standard (not elevated) token, and never from this elevated process.
    /// </summary>
    public const string RestartExplorerScript =
        "$session = (Get-Process -Id $PID).SessionId; " +
        "function Get-Shell { @(Get-Process explorer -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $session }) }; " +
        "$owner = $null; " +
        "foreach ($p in @(Get-CimInstance Win32_Process -Filter \"Name = 'explorer.exe' AND SessionId = $session\" -ErrorAction SilentlyContinue)) { " +
        "$o = Invoke-CimMethod -InputObject $p -MethodName GetOwnerSid -ErrorAction SilentlyContinue; " +
        "if ($o -and $o.ReturnValue -eq 0 -and $o.Sid) { $owner = $o.Sid; break } }; " +
        "Get-Shell | Stop-Process -Force; " +
        "for ($i = 0; $i -lt 16; $i++) { Start-Sleep -Milliseconds 500; if (Get-Shell) { break } }; " +
        "if (-not (Get-Shell)) { " +
        "if (-not $owner) { exit 2 }; " +
        "$name = 'PC Checker Explorer restart ' + [guid]::NewGuid().ToString('N'); " +
        "$action = New-ScheduledTaskAction -Execute (Join-Path $env:SystemRoot 'explorer.exe'); " +
        "$principal = New-ScheduledTaskPrincipal -UserId $owner -LogonType Interactive -RunLevel Limited; " +
        "$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero); " +
        "Register-ScheduledTask -TaskName $name -Action $action -Principal $principal -Settings $settings -Force -ErrorAction Stop | Out-Null; " +
        "try { Start-ScheduledTask -TaskName $name -ErrorAction Stop; for ($i = 0; $i -lt 20; $i++) { Start-Sleep -Milliseconds 500; if (Get-Shell) { break } } } " +
        "finally { Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction SilentlyContinue }; " +
        "if (-not (Get-Shell)) { exit 3 } }";

    public override async Task<OperationResult> RestartExplorerAsync(CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return OperationResult.Fail("Restarting File Explorer is only possible on Windows.");
        }

        var result = await PowerShellRunner.RunAsync(RestartExplorerScript, TimeSpan.FromSeconds(45), cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("File Explorer restart requested (exit code {Code}).", result.ExitCode);
        return result.Success
            ? OperationResult.Ok("File Explorer restarted. Taskbar, Start and context menu changes are now visible.")
            : OperationResult.Fail("File Explorer could not be restarted. Sign out and back in to see the changes.");
    }

    public override OperationResult OpenSettings(string settingsUri)
    {
        if (string.IsNullOrWhiteSpace(settingsUri) ||
            !(settingsUri.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase) || settingsUri.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            return OperationResult.Fail("That link cannot be opened.");
        }

        return ShellLauncher.Open(settingsUri);
    }

    protected override async Task<OperationResult<IReadOnlyList<TweakRunResult>>> RunGuardedAsync(string description, IReadOnlyList<DebloatTweak> tweaks, bool revert,
        Func<CancellationToken, Task<IReadOnlyList<TweakRunResult>>> work, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        var target = string.Join(", ", tweaks.Select(t => t.Id));
        bool oneWay = !revert && tweaks.Any(t => !t.Reversible);
        bool mustHaveRestorePoint = RequiresRestorePoint(tweaks, revert);
        var options = new ChangeOptions(CreateBefore: true, CreateAfter: null, ModuleName, target, Reversible: !oneWay,
            UndoHint: revert
                ? "Apply the tweaks again on the Debloat page."
                : "Select the tweaks on the Debloat page and choose Revert (previous values are saved in debloat-capture.json), or use the restore point.")
        {
            // One-way changes (app removal, OneDrive removal) and optional feature changes only run with a restore point behind them.
            OnRestorePointFailed = mustHaveRestorePoint ? RestorePointFailedBehavior.Abort : RestorePointFailedBehavior.Continue,
        };

        return await _guard.RunGuardedAsync<IReadOnlyList<TweakRunResult>>(description, options, async ct =>
        {
            var results = await work(ct).ConfigureAwait(false);
            var summary = Finish(results, revert, null).Summary;
            return results.Any(r => r.Outcome == TweakOutcome.Failed)
                ? new OperationResult<IReadOnlyList<TweakRunResult>>(false, summary, results)
                : OperationResult<IReadOnlyList<TweakRunResult>>.Ok(results, summary);
        }, progress, cancellationToken).ConfigureAwait(false);
    }

    protected override Task<OperationResult> RunDelegatedAsync(string optimizationId, bool revert, CancellationToken cancellationToken) =>
        revert ? _batchOptimization.RevertAsync(optimizationId, cancellationToken) : _batchOptimization.ApplyAsync(optimizationId, cancellationToken);

    protected override Task<ActionState> GetDelegatedStateAsync(string optimizationId, CancellationToken cancellationToken) =>
        _optimization.GetStateAsync(optimizationId, cancellationToken);

    protected override async Task RecordAsync(DebloatTweak tweak, TweakRunResult result, bool revert)
    {
        // Delegated and app/feature tweaks are journaled by the service that made the change; record the in-place ones.
        if (tweak.IsExternal)
        {
            return;
        }

        try
        {
            await _journal.RecordAsync(ActionRecord.Create(ModuleName, (revert ? "Revert: " : "Apply: ") + tweak.Title, tweak.Id,
                $"{result.Outcome}: {result.Message}", revert ? result.After : result.Before, revert ? result.Before : result.After,
                reversible: tweak.Reversible,
                undoHint: tweak.Reversible ? "Revert it on the Debloat page." : DebloatEngine.UndoHint(tweak))).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not journal debloat tweak {Tweak}.", tweak.Id);
        }

        if (result.Outcome == TweakOutcome.Failed)
        {
            _logger.LogWarning("Debloat {Verb} failed for {Tweak}: {Message}", revert ? "revert" : "apply", tweak.Id, result.Message);
        }
        else
        {
            _logger.LogInformation("Debloat {Verb} {Tweak}: {Outcome} — {Message}", revert ? "revert" : "apply", tweak.Id, result.Outcome, result.Message);
        }
    }

    /// <summary>
    /// The Windows Features service used for removals inside a batch: the real one, but behind a guard that relies on the
    /// batch's restore point. Anything else (tests, a decorated service) is used as given.
    /// </summary>
    private static IWindowsFeatureService BatchFeatures(IWindowsFeatureService features, IChangeGuard guard, ILogger<WindowsFeatureService> featureLogger) =>
        features is WindowsFeatureService ? new WindowsFeatureService(new DebloatBatchChangeGuard(guard), featureLogger) : features;

    /// <summary>
    /// The Optimize service used for linked actions inside a batch: a Windows one behind the batch guard (the linked
    /// actions are built-in catalog actions, so no providers are needed), or the given service as is.
    /// </summary>
    private static IOptimizationService BatchOptimization(IOptimizationService optimization, IChangeGuard guard, IStartupService startup,
        ILogger<WindowsOptimizationService> optimizationLogger) =>
        optimization is WindowsOptimizationService
            ? new WindowsOptimizationService(new DebloatBatchChangeGuard(guard), startup, [], optimizationLogger)
            : optimization;

    private static string? CapturePath(ISettingsService settings, ILogger logger)
    {
        try
        {
            return DebloatCaptureBook.DefaultPath(settings.SettingsDirectory);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Debloat captures will only be kept in memory: the settings folder is unknown.");
            return null;
        }
    }
}
