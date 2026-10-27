using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using PcChecker.Core.Common;
using PcChecker.Core.Hardware;
using PcChecker.Core.Restore;
using PcChecker.Core.Startup;

namespace PcChecker.Core.WindowsUpdate;

/// <summary>
/// Windows implementation of <see cref="IUpdateRollbackService"/>. Installed updates come from
/// <c>Win32_QuickFixEngineering</c>, the Windows Update history and <c>DISM /Get-Packages</c>; removal runs
/// <c>wusa.exe</c> or <c>dism.exe</c> from the system folder; hiding uses the Windows Update Agent; going back uses
/// DISM's OS uninstall commands. Outcomes are decided from exit codes, never from the tools' (translated) text, and
/// every change goes through <see cref="IChangeGuard"/>.
/// </summary>
public sealed class WindowsUpdateRollbackService : IUpdateRollbackService
{
    private const string Module = "Windows Update";
    private const string NotOnWindows = "This is only available on Windows.";
    private const string RecoveryHint = "You can also use Settings → System → Recovery → Go back.";
    private const string QuickFixQuery = "SELECT HotFixID, Description, InstalledOn FROM Win32_QuickFixEngineering";
    private const string CurrentVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromMinutes(3);

    // Removing an update or starting to go back is never cut short: CommandRunner kills the whole process tree when a
    // timeout runs out, and killing wusa or DISM halfway could leave Windows in between. On a slow disk this can take
    // well over an hour, so these runs wait for the tool to finish however long it takes.
    private static readonly TimeSpan ChangeTimeout = Timeout.InfiniteTimeSpan;

    private readonly ILogger<WindowsUpdateRollbackService> _logger;
    private readonly IChangeGuard _guard;
    private readonly WindowsUpdateClient _client;

    public WindowsUpdateRollbackService(ILogger<WindowsUpdateRollbackService> logger, IChangeGuard guard)
    {
        _logger = logger;
        _guard = guard;
        _client = new WindowsUpdateClient(logger);
    }

    private static string Dism => Path.Combine(Environment.SystemDirectory, "dism.exe");

    private static string Wusa => Path.Combine(Environment.SystemDirectory, "wusa.exe");

    public async Task<InstalledUpdateList> GetInstalledUpdatesAsync(CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return new InstalledUpdateList([], [NotOnWindows], DateTimeOffset.Now);
        }

        var warnings = new List<string>();
        var quickFixes = await Task.Run(() =>
        {
            var wmiWarnings = new List<string>();
            var rows = new WmiQueryRunner(_logger, wmiWarnings).Query(QuickFixQuery, friendlyName: "The installed updates list", cancellationToken: cancellationToken);
            lock (warnings)
            {
                warnings.AddRange(wmiWarnings);
            }

            return rows.Select(r => new QuickFixRecord(r.Str("HotFixID"), r.Str("Description"), r.Str("InstalledOn"))).ToList();
        }, cancellationToken).ConfigureAwait(false);

        var (history, historyWarnings) = await _client.QueryHistoryAsync(1000, cancellationToken).ConfigureAwait(false);
        warnings.AddRange(historyWarnings);

        IReadOnlyList<DismPackage> packages = [];
        var dism = await RunAsync(Dism, InstalledUpdateRules.GetPackagesArguments(), ReadTimeout, cancellationToken).ConfigureAwait(false);
        if (dism.ExitCode == 0)
        {
            packages = DismOutputParser.ParsePackages(dism.StdOut);
        }
        else
        {
            var mapped = Interpret(dism, "DISM");
            _logger.LogWarning("DISM /Get-Packages ended with {Code}: {Line}", mapped.CodeText, dism.FirstLine);
            warnings.Add("DISM could not list the Windows packages, so the cumulative update cannot be removed from here right now: " + mapped.Message);
        }

        return new InstalledUpdateList(InstalledUpdateRules.Merge(quickFixes, history, packages), warnings, DateTimeOffset.Now);
    }

    public async Task<OperationResult> UninstallAsync(InstalledUpdate update, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        var plan = InstalledUpdateRules.PlanUninstall(update);
        if (!plan.CanUninstall)
        {
            return OperationResult.Fail(plan.Explanation);
        }

        if (!PlatformGuard.IsWindows)
        {
            return OperationResult.Fail(NotOnWindows);
        }

        var (tool, exe, arguments) = plan.Method == UninstallMethod.DismPackage
            ? ("DISM", Dism, InstalledUpdateRules.DismRemoveArguments(plan.PackageName!))
            : ("wusa", Wusa, InstalledUpdateRules.WusaUninstallArguments(update.Kb));
        var options = new ChangeOptions(CreateBefore: true, CreateAfter: null, Module, update.Kb, Reversible: true,
            UndoHint: "Install it again with Check for updates on the Windows Update page (show it again first if you hid it), or restore the restore point.")
        {
            OnRestorePointFailed = RestorePointFailedBehavior.Abort,
        };

        // Once wusa or DISM has started, stopping it halfway could leave Windows in between, so the run is not cancellable.
        return await _guard.RunGuardedAsync($"Uninstall update {update.Kb}", options, async _ =>
        {
            progress?.Report(new ProgressUpdate($"Removing {update.Kb}", null, "This can take several minutes. Do not turn off the PC."));
            var run = await RunAsync(exe, arguments, ChangeTimeout, CancellationToken.None).ConfigureAwait(false);
            var mapped = Interpret(run, tool);
            _logger.LogInformation("{Tool} uninstall of {Kb} ended with {Code} ({Outcome}).", tool, update.Kb, mapped.CodeText, mapped.Outcome);
            if (mapped.Success)
            {
                return OperationResult.Ok(mapped.RestartNeeded
                    ? $"{update.Kb} was removed. Restart Windows to finish."
                    : $"{update.Kb} was removed.", mapped.RestartNeeded);
            }

            return new OperationResult(false, $"{update.Kb} was not removed: {mapped.Message} (code {mapped.CodeText})", null, mapped.RestartNeeded);
        }, progress, CancellationToken.None).ConfigureAwait(false);
    }

    public async Task<HiddenUpdateList> GetHiddenUpdatesAsync(CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return new HiddenUpdateList([], [NotOnWindows]);
        }

        var (updates, warnings) = await _client.ListHiddenAsync(UsesUpdateServer(), cancellationToken).ConfigureAwait(false);
        return new HiddenUpdateList(updates, warnings);
    }

    public Task<OperationResult> HideAsync(UpdateHideTarget target, CancellationToken cancellationToken) => SetHiddenAsync(target, true, cancellationToken);

    public Task<OperationResult> UnhideAsync(HiddenUpdate update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        return SetHiddenAsync(new UpdateHideTarget(update.Id, update.Kb, update.Title), false, cancellationToken);
    }

    public async Task<RollbackWindow> GetRollbackWindowAsync(CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return RollbackWindow.Unknown(NotOnWindows);
        }

        var warnings = new List<string>();
        var windowsOld = WindowsOldPath();
        bool exists = SafeDirectoryExists(windowsOld);
        var upgradedOn = RollbackRules.FromInstallDate(ReadInstallDate()) ?? (exists ? SafeCreationTime(windowsOld) : null);
        if (!exists)
        {
            return RollbackRules.Evaluate(null, false, upgradedOn, false, DateTimeOffset.Now, warnings);
        }

        var run = await RunAsync(Dism, RollbackRules.GetWindowArguments(), ReadTimeout, cancellationToken).ConfigureAwait(false);
        if (run.ExitCode == 0 && !run.TimedOut)
        {
            return RollbackRules.Evaluate(DismOutputParser.ParseUninstallWindow(run.StdOut), false, upgradedOn, true, DateTimeOffset.Now, warnings);
        }

        if (!run.TimedOut && RollbackRules.MeansNotAvailable(run.ExitCode))
        {
            return RollbackRules.Evaluate(null, true, upgradedOn, true, DateTimeOffset.Now, warnings);
        }

        var mapped = Interpret(run, "DISM");
        _logger.LogWarning("DISM /Get-OSUninstallWindow ended with {Code}: {Line}", mapped.CodeText, run.FirstLine);
        warnings.Add("DISM could not read how long going back is possible: " + mapped.Message);
        return RollbackRules.Evaluate(null, false, upgradedOn, true, DateTimeOffset.Now, warnings);
    }

    public async Task<OperationResult> GoBackAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return OperationResult.Fail(NotOnWindows);
        }

        var window = await GetRollbackWindowAsync(cancellationToken).ConfigureAwait(false);
        if (window.State != RollbackState.Available)
        {
            return OperationResult.Fail(window.Message);
        }

        // No restore point: going back replaces the whole Windows installation and its restore points with the old one.
        var options = new ChangeOptions(CreateBefore: false, CreateAfter: false, Module, "Previous version of Windows", Reversible: false,
            UndoHint: "Going back cannot be undone. To return to the newer version, install it again from Windows Update.");
        return await _guard.RunGuardedAsync("Go back to the previous version of Windows", options, async _ =>
        {
            progress?.Report(new ProgressUpdate("Preparing to go back to the previous version of Windows", null, "This can take a few minutes."));
            var run = await RunAsync(Dism, RollbackRules.GoBackArguments(), ChangeTimeout, CancellationToken.None).ConfigureAwait(false);
            var mapped = Interpret(run, "DISM");
            _logger.LogInformation("DISM /Initiate-OSUninstall ended with {Code} ({Outcome}).", mapped.CodeText, mapped.Outcome);
            return mapped.Success
                ? OperationResult.Ok("Windows is ready to go back to the previous version. It does so while the PC restarts, which takes a while.", rebootRequired: true)
                : OperationResult.Fail($"Going back did not start: {mapped.Message} (code {mapped.CodeText}) {RecoveryHint}");
        }, progress, CancellationToken.None).ConfigureAwait(false);
    }

    public async Task<OperationResult> SetRollbackWindowAsync(int days, CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return OperationResult.Fail(NotOnWindows);
        }

        var window = await GetRollbackWindowAsync(cancellationToken).ConfigureAwait(false);
        if (window.State != RollbackState.Available)
        {
            return OperationResult.Fail(window.Message);
        }

        if (RollbackRules.ValidateExtension(days, window.DaysSinceUpgrade) is { } problem)
        {
            return OperationResult.Fail(problem);
        }

        var before = window.WindowDays is { } current ? $"{current} days" : "unknown";
        var options = new ChangeOptions(CreateBefore: false, CreateAfter: false, Module, "Go back window", Reversible: true,
            UndoHint: $"The window was {before}. Set it again on the Windows Update page (Roll back tab).");
        return await _guard.RunGuardedAsync($"Set the go back window to {days} days", options, async ct =>
        {
            var run = await RunAsync(Dism, RollbackRules.SetWindowArguments(days), ReadTimeout, ct).ConfigureAwait(false);
            var mapped = Interpret(run, "DISM");
            if (!mapped.Success)
            {
                return OperationResult.Fail($"The window was not changed: {mapped.Message} (code {mapped.CodeText})");
            }

            int? left = window.DaysSinceUpgrade is { } since ? RollbackRules.DaysLeft(days, since) : null;
            return OperationResult.Ok(left is { } l
                ? $"Going back is now possible for {days} days after the upgrade ({l} days left)."
                : $"Going back is now possible for {days} days after the upgrade.");
        }, null, cancellationToken).ConfigureAwait(false);
    }

    private async Task<OperationResult> SetHiddenAsync(UpdateHideTarget target, bool hide, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!PlatformGuard.IsWindows)
        {
            return OperationResult.Fail(NotOnWindows);
        }

        var options = new ChangeOptions(CreateBefore: false, CreateAfter: false, Module, target.Title, Reversible: true,
            UndoHint: hide ? "Show it again under Hidden updates on the Windows Update page (Roll back tab)." : "Hide it again on the Windows Update page.");
        bool server = UsesUpdateServer();
        return await _guard.RunGuardedAsync(hide ? $"Hide update {target.Title}" : $"Show update {target.Title} again", options,
            ct => _client.SetHiddenAsync(target, hide, server, ct), null, cancellationToken).ConfigureAwait(false);
    }

    private bool UsesUpdateServer() => UpdatePolicyRules.Evaluate(WindowsUpdateService.ReadPolicyValues(_logger)).UsesUpdateServer;

    private async Task<CommandOutput> RunAsync(string exe, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!File.Exists(exe))
        {
            return new CommandOutput(-1, string.Empty, $"{Path.GetFileName(exe)} was not found in the Windows system folder.", false);
        }

        _logger.LogInformation("Running {Tool} {Arguments}", Path.GetFileName(exe), string.Join(' ', arguments));
        return await CommandRunner.RunAsync(exe, arguments, timeout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The run's exit code as an outcome; a tool that could not start at all keeps the reason it gave.</summary>
    private static UpdateToolResult Interpret(CommandOutput run, string tool)
    {
        if (run.TimedOut)
        {
            return UpdateToolExitCodes.Map(UpdateToolExitCodes.TimedOutCode, tool);
        }

        return run.ExitCode == -1 && run.StdOut.Length == 0 && run.StdErr.Length > 0
            ? new UpdateToolResult(UpdateToolOutcome.Failed, -1, $"{tool} could not be started: {run.StdErr}")
            : UpdateToolExitCodes.Map(run.ExitCode, tool);
    }

    private static string WindowsOldPath() =>
        Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\", "Windows.old");

    private static bool SafeDirectoryExists(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    private static DateTimeOffset? SafeCreationTime(string path)
    {
        try
        {
            return new DateTimeOffset(Directory.GetCreationTimeUtc(path), TimeSpan.Zero);
        }
        catch
        {
            return null;
        }
    }

    private long? ReadInstallDate()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(CurrentVersionKey);
            return key?.GetValue("InstallDate") switch
            {
                int i => unchecked((uint)i),
                long l => l,
                _ => null,
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "InstallDate could not be read.");
            return null;
        }
    }
}
