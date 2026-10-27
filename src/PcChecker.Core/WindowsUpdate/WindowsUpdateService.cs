using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using PcChecker.Core.Common;
using PcChecker.Core.Restore;

namespace PcChecker.Core.WindowsUpdate;

/// <summary>
/// Windows implementation of <see cref="IWindowsUpdateService"/>: the Windows Update Agent COM API for checking,
/// installing and history, and the registry values the Settings app uses for status, pause and active hours.
/// Every change goes through <see cref="IChangeGuard"/>.
/// </summary>
public sealed partial class WindowsUpdateService : IWindowsUpdateService
{
    private const string Module = "Windows Update";
    private const string NotOnWindows = "Windows Update is only available on Windows.";
    private const string ServiceKey = @"SYSTEM\CurrentControlSet\Services\wuauserv";
    private const string RebootRequiredKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired";
    private const string ServicingRebootPendingKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending";

    private readonly ILogger<WindowsUpdateService> _logger;
    private readonly IChangeGuard _guard;
    private readonly WindowsUpdateClient _client;

    public WindowsUpdateService(ILogger<WindowsUpdateService> logger, IChangeGuard guard)
    {
        _logger = logger;
        _guard = guard;
        _client = new WindowsUpdateClient(logger);
    }

    public Task<WindowsUpdateStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return Task.FromResult(new WindowsUpdateStatus(null, null, false, UpdatePauseState.NotPaused, null, UpdatePolicyStatus.None,
                UpdatePauseRules.DefaultMaxPauseDays, false, [NotOnWindows], DateTimeOffset.Now));
        }

        return Task.Run(() =>
        {
            var warnings = new List<string>();
            var settings = ReadUxSettings(warnings);
            var policy = UpdatePolicyRules.Evaluate(ReadPolicyValues());
            bool serviceDisabled = UpdatePolicyRules.IsServiceDisabled(ReadDword(ServiceKey, "Start"));
            if (serviceDisabled)
            {
                warnings.Add("The Windows Update service is disabled, so Windows cannot check for or install updates. Set the 'Windows Update' service back to Manual to fix this.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var agent = _client.ReadAgentState();
            warnings.AddRange(agent.Warnings);
            bool restartPending = agent.RebootRequired == true || KeyExists(RebootRequiredKey) || KeyExists(ServicingRebootPendingKey);

            return new WindowsUpdateStatus(agent.LastSearch, agent.LastInstall, restartPending, settings.Pause, settings.ActiveHours, policy,
                settings.MaxPauseDays, serviceDisabled, warnings, DateTimeOffset.Now);
        }, cancellationToken);
    }

    public async Task<PendingUpdateScan> CheckForUpdatesAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return new PendingUpdateScan([], [NotOnWindows], DateTimeOffset.Now);
        }

        // With an organisation's update server configured, check that server instead of going around it.
        var policy = UpdatePolicyRules.Evaluate(ReadPolicyValues());
        var (updates, warnings) = await _client.SearchAsync(policy.UsesUpdateServer, progress, cancellationToken).ConfigureAwait(false);
        var software = updates.Where(u => u.Classification != UpdateClassification.Driver).ToList();
        progress?.Report(new ProgressUpdate("Done", 100));
        return new PendingUpdateScan(software, warnings, DateTimeOffset.Now);
    }

    public async Task<UpdateHistory> GetHistoryAsync(int limit, CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return new UpdateHistory([], [NotOnWindows]);
        }

        int wanted = Math.Clamp(limit, 1, 1000);
        // Uninstalls and blank entries are dropped by the mapper, so read some extra records.
        var (records, warnings) = await _client.QueryHistoryAsync(wanted + 200, cancellationToken).ConfigureAwait(false);
        return new UpdateHistory(UpdateHistoryMapper.MapAll(records, wanted), warnings);
    }

    public async Task<OperationResult<UpdateInstallResult>> InstallAsync(IReadOnlyList<PendingUpdate> updates, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(updates);
        if (updates.Count == 0)
        {
            return OperationResult<UpdateInstallResult>.Fail("No updates were selected.");
        }

        if (!PlatformGuard.IsWindows)
        {
            return OperationResult<UpdateInstallResult>.Fail(NotOnWindows);
        }

        var target = updates.Count == 1 ? updates[0].Title : $"{updates.Count} Windows updates";
        var options = ChangeOptions.For(Module, target, reversible: true,
            undoHint: "Settings → Windows Update → Update history → Uninstall updates, or restore the restore point.")
            with { OnRestorePointFailed = RestorePointFailedBehavior.Abort };
        // Cancelling only stops before the next update: the guard gets no token, so the restore point, the journal entry
        // and the results of the updates that did install are never lost to a cancellation.
        var guarded = await _guard.RunGuardedAsync("Install Windows updates", options, async _ =>
        {
            var r = await _client.InstallAsync(updates, progress, cancellationToken).ConfigureAwait(false);
            // The per-update results travel with the outcome either way; Success only decides how the journal records it.
            return new OperationResult<UpdateInstallResult>(r.Installed > 0, UpdateInstallSummary.Summarize(r), r, null, r.RebootRequired);
        }, progress, CancellationToken.None).ConfigureAwait(false);

        if (guarded.Value is not null)
        {
            return guarded;
        }

        // The change never ran (for example the restore point failed): every update gets the guard's reason.
        var notRun = new UpdateInstallResult(updates.Select(u => new UpdateInstallItem(u, false, "Not installed: " + guarded.Message, false)).ToList(), false);
        return new OperationResult<UpdateInstallResult>(false, guarded.Message, notRun, guarded.Error);
    }

    private static bool KeyExists(string path)
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(path);
            return key is not null;
        }
        catch
        {
            return false;
        }
    }
}
