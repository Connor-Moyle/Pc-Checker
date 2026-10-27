using System.Globalization;
using PcChecker.Core.Common;
using PcChecker.Core.Drivers;

namespace PcChecker.Core.WindowsUpdate;

/// <summary>
/// Demo Windows Update for the sample Windows 11 24H2 desktop: this month's cumulative updates, a definition update
/// and an optional preview waiting, a history with two failed installs, and pause and active hours kept in memory.
/// Nothing on the PC is read or changed. Pure .NET.
/// </summary>
public sealed class DemoWindowsUpdateService : IWindowsUpdateService
{
    private readonly object _gate = new();
    private readonly List<PendingUpdate> _pending;
    private readonly List<UpdateHistoryRecord> _history;
    private readonly HashSet<string> _hiddenIds = new(StringComparer.OrdinalIgnoreCase);
    private UpdatePauseState _pause = UpdatePauseState.NotPaused;
    private ActiveHours _activeHours = new(8, 23, false);
    private DateTimeOffset _lastCheck = DateTimeOffset.UtcNow.AddHours(-3).AddMinutes(-12);
    private bool _restartPending;

    public DemoWindowsUpdateService()
    {
        _pending = SamplePending(DateTime.Today);
        _history = SampleHistory(DateTime.UtcNow);
    }

    public async Task<WindowsUpdateStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 100, 300).ConfigureAwait(false);
        lock (_gate)
        {
            if (_pause.IsPaused && _pause.PausedUntil <= DateTimeOffset.UtcNow)
            {
                _pause = UpdatePauseState.NotPaused;
            }

            var lastInstall = UpdateHistoryMapper.LastSuccessfulInstall(UpdateHistoryMapper.MapAll(_history));
            return new WindowsUpdateStatus(_lastCheck, lastInstall, _restartPending, _pause, _activeHours, UpdatePolicyStatus.None,
                UpdatePauseRules.DefaultMaxPauseDays, false, [], DateTimeOffset.Now);
        }
    }

    public async Task<PendingUpdateScan> CheckForUpdatesAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new ProgressUpdate("Connecting to Windows Update", 10));
        await DemoData.DelayAsync(cancellationToken, 200, 400).ConfigureAwait(false);
        progress?.Report(new ProgressUpdate("Searching for updates", 40, "This can take a minute."));
        await DemoData.DelayAsync(cancellationToken, 500, 900).ConfigureAwait(false);
        progress?.Report(new ProgressUpdate("Done", 100));
        lock (_gate)
        {
            _lastCheck = DateTimeOffset.UtcNow;
            return new PendingUpdateScan(_pending.Where(p => !_hiddenIds.Contains(p.Id)).ToList(), [], DateTimeOffset.Now);
        }
    }

    public async Task<UpdateHistory> GetHistoryAsync(int limit, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 100, 300).ConfigureAwait(false);
        lock (_gate)
        {
            return new UpdateHistory(UpdateHistoryMapper.MapAll(_history, limit), []);
        }
    }

    public async Task<OperationResult<UpdateInstallResult>> InstallAsync(IReadOnlyList<PendingUpdate> updates, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(updates);
        if (updates.Count == 0)
        {
            return OperationResult<UpdateInstallResult>.Fail("No updates were selected.");
        }

        var items = new List<UpdateInstallItem>();
        for (int i = 0; i < updates.Count; i++)
        {
            var u = updates[i];
            if (cancellationToken.IsCancellationRequested)
            {
                items.Add(new UpdateInstallItem(u, false, "Skipped: the install was cancelled before this update started.", false));
                continue;
            }

            string label = $"{i + 1} of {updates.Count}: {u.Title}";
            if (!u.IsDownloaded)
            {
                progress?.Report(new ProgressUpdate("Downloading update " + label, 100.0 * i / updates.Count));
                await DemoData.DelayAsync(CancellationToken.None, 300, 600).ConfigureAwait(false);
            }

            progress?.Report(new ProgressUpdate("Installing update " + label, 100.0 * (i + 0.5) / updates.Count, "Windows may be slow while an update installs."));
            await DemoData.DelayAsync(CancellationToken.None, 400, 800).ConfigureAwait(false);
            bool reboot = u.Restart == UpdateRestartNeed.Yes;
            items.Add(new UpdateInstallItem(u, true, reboot ? "Installed (demo). Restart Windows to finish." : "Installed (demo).", reboot));
        }

        progress?.Report(new ProgressUpdate("Install finished", 100));
        var result = new UpdateInstallResult(items, items.Any(i => i.RebootRequired));
        lock (_gate)
        {
            foreach (var done in items.Where(i => i.Success))
            {
                _pending.RemoveAll(p => p.Id == done.Update.Id);
                _history.Add(new UpdateHistoryRecord(done.Update.Title, UpdateHistoryMapper.OperationInstallation, WuaResultCodes.Succeeded, 0,
                    DateTime.UtcNow, [], done.Update.Id, done.Update.Description));
            }

            _restartPending |= result.RebootRequired;
        }

        return new OperationResult<UpdateInstallResult>(result.Installed > 0, UpdateInstallSummary.Summarize(result), result, null, result.RebootRequired);
    }

    /// <summary>
    /// Hides or shows a waiting update in the next check, as Windows Update does for hidden updates. Called by
    /// <see cref="DemoUpdateRollbackService"/> so the demo's two halves agree.
    /// </summary>
    public void SetHidden(string updateId, bool hidden)
    {
        lock (_gate)
        {
            if (hidden)
            {
                _hiddenIds.Add(updateId);
            }
            else
            {
                _hiddenIds.Remove(updateId);
            }
        }
    }

    public async Task<OperationResult> PauseAsync(int weeks, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 100, 300).ConfigureAwait(false);
        lock (_gate)
        {
            var plan = UpdatePauseRules.Plan(_pause, DateTimeOffset.UtcNow, weeks, UpdatePauseRules.DefaultMaxPauseDays);
            if (!plan.Allowed)
            {
                return OperationResult.Fail(plan.Message);
            }

            _pause = new UpdatePauseState(true, plan.Start, plan.Expiry);
            return OperationResult.Ok(plan.Message + " (demo)");
        }
    }

    public async Task<OperationResult> ResumeAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 100, 300).ConfigureAwait(false);
        lock (_gate)
        {
            if (!_pause.IsPaused)
            {
                return OperationResult.Ok("Updates are not paused.");
            }

            _pause = UpdatePauseState.NotPaused;
            return OperationResult.Ok("Updates are no longer paused. Windows will look for updates again shortly. (demo)");
        }
    }

    public async Task<OperationResult> SetActiveHoursAsync(int startHour, int endHour, CancellationToken cancellationToken)
    {
        if (ActiveHoursRules.Validate(startHour, endHour) is { } problem)
        {
            return OperationResult.Fail(problem);
        }

        await DemoData.DelayAsync(cancellationToken, 100, 300).ConfigureAwait(false);
        lock (_gate)
        {
            _activeHours = new ActiveHours(startHour, endHour, false);
            return OperationResult.Ok($"Active hours are now {ActiveHoursRules.Describe(_activeHours)}. (demo)");
        }
    }

    private static List<PendingUpdate> SamplePending(DateTime today)
    {
        var month = today.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var released = new DateTime(today.Year, today.Month, Math.Min(14, DateTime.DaysInMonth(today.Year, today.Month)));
        return
        [
            new("demo-lcu", $"{month} Cumulative Update for Windows 11 Version 24H2 for x64-based Systems (KB5066835)", "KB5066835",
                812_400_000, UpdateClassification.Cumulative, UpdateSeverity.Critical, UpdateRestartNeed.Yes, false,
                "Install this update to resolve issues in Windows. For a complete listing of the issues that are included in this update, see the associated Microsoft Knowledge Base article.")
            {
                Released = released,
                SupportUrl = "https://support.microsoft.com/help/5066835",
            },
            new("demo-dotnet", $"{month} Cumulative Update for .NET Framework 3.5 and 4.8.1 for Windows 11, version 24H2 for x64 (KB5066131)", "KB5066131",
                71_300_000, UpdateClassification.Cumulative, UpdateSeverity.Important, UpdateRestartNeed.Maybe, false,
                "A security issue has been identified in a Microsoft software product that could affect your system.")
            {
                Released = released,
            },
            new("demo-defender", "Security Intelligence Update for Microsoft Defender Antivirus - KB2267602 (Version 1.441.205.0) - Current Channel (Broad)", "KB2267602",
                54_800_000, UpdateClassification.Definition, UpdateSeverity.Unspecified, UpdateRestartNeed.No, true,
                "Install this update to revise the files that are used to detect viruses, spyware, and other potentially unwanted software.")
            {
                Released = today.AddHours(-5),
            },
            new("demo-msrt", "Windows Malicious Software Removal Tool x64 - v5.141 (KB890830)", "KB890830",
                78_900_000, UpdateClassification.Other, UpdateSeverity.Unspecified, UpdateRestartNeed.No, false,
                "After the download, this tool runs one time to check your computer for infection by specific, prevalent malicious software.")
            {
                Released = released,
            },
            new("demo-preview", $"{month} Cumulative Update Preview for Windows 11 Version 24H2 for x64-based Systems (KB5067036)", "KB5067036",
                845_100_000, UpdateClassification.Cumulative, UpdateSeverity.Unspecified, UpdateRestartNeed.Yes, false,
                "This optional preview contains improvements that will be part of next month's cumulative update.")
            {
                IsOptional = true,
                Released = released.AddDays(-7),
            },
        ];
    }

    private static List<UpdateHistoryRecord> SampleHistory(DateTime nowUtc)
    {
        var records = new List<UpdateHistoryRecord>();
        var rng = DemoData.CreateRandom(2400);
        const string definitionId = UpdateClassifier.CategoryIds.DefinitionUpdates;
        const string securityId = UpdateClassifier.CategoryIds.SecurityUpdates;

        // Daily definition updates for the last few weeks.
        for (int day = 0; day < 40; day++)
        {
            var when = nowUtc.Date.AddDays(-day).AddHours(9 + rng.Next(0, 8)).AddMinutes(rng.Next(0, 60));
            var version = $"1.441.{205 - day * 2}.0";
            records.Add(Record($"Security Intelligence Update for Microsoft Defender Antivirus - KB2267602 (Version {version}) - Current Channel (Broad)",
                WuaResultCodes.Succeeded, 0, when, definitionId));
        }

        // Monthly cumulative, .NET and removal tool updates for the last six months.
        for (int m = 1; m <= 6; m++)
        {
            var patchDay = SecondTuesday(nowUtc.AddMonths(-m));
            var month = patchDay.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            int kb = 5066835 - m * 3127;
            records.Add(Record($"{month} Cumulative Update for Windows 11 Version 24H2 for x64-based Systems (KB{kb})",
                WuaResultCodes.Succeeded, 0, patchDay.AddDays(1).AddHours(3), securityId));
            records.Add(Record($"{month} Cumulative Update for .NET Framework 3.5 and 4.8.1 for Windows 11, version 24H2 for x64 (KB{kb - 704})",
                WuaResultCodes.Succeeded, 0, patchDay.AddDays(1).AddHours(3).AddMinutes(-20), securityId));
            records.Add(Record($"Windows Malicious Software Removal Tool x64 - v5.{141 - m} (KB890830)",
                WuaResultCodes.Succeeded, 0, patchDay.AddDays(1).AddHours(2), UpdateClassifier.CategoryIds.UpdateRollups));
        }

        // Last month's cumulative update failed once before it went through.
        var failedDay = SecondTuesday(nowUtc.AddMonths(-1));
        int failedKb = 5066835 - 3127;
        records.Add(Record($"{failedDay:yyyy-MM} Cumulative Update for Windows 11 Version 24H2 for x64-based Systems (KB{failedKb})",
            WuaResultCodes.Failed, unchecked((int)0x800F0922), failedDay.AddHours(22), securityId));
        records.Add(Record("Windows 11, version 25H2", WuaResultCodes.Failed, unchecked((int)0xC1900101),
            nowUtc.Date.AddDays(-19).AddHours(1), UpdateClassifier.CategoryIds.Upgrades));
        records.Add(Record("NVIDIA - Display - 32.0.15.8097", WuaResultCodes.Succeeded, 0, nowUtc.Date.AddDays(-27).AddHours(14), UpdateClassifier.CategoryIds.Drivers));
        records.Add(Record("Update for Microsoft Defender Antivirus antimalware platform - KB4052623 (Version 4.18.25090.3009) - Current Channel (Broad)",
            WuaResultCodes.Succeeded, 0, nowUtc.Date.AddDays(-12).AddHours(11), UpdateClassifier.CategoryIds.CriticalUpdates));
        return records;
    }

    private static UpdateHistoryRecord Record(string title, int resultCode, int hresult, DateTime whenUtc, string categoryId) =>
        new(title, UpdateHistoryMapper.OperationInstallation, resultCode, hresult, DateTime.SpecifyKind(whenUtc, DateTimeKind.Utc),
            [categoryId], null, null);

    private static DateTime SecondTuesday(DateTime inMonth)
    {
        var first = new DateTime(inMonth.Year, inMonth.Month, 1, 18, 0, 0, DateTimeKind.Utc);
        int offset = ((int)DayOfWeek.Tuesday - (int)first.DayOfWeek + 7) % 7;
        return first.AddDays(offset + 7);
    }
}
