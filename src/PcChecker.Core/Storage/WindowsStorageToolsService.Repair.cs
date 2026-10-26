using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Restore;

namespace PcChecker.Core.Storage;

public sealed partial class WindowsStorageToolsService
{
    private const string Module = "Storage";

    /// <summary>
    /// <c>chkdsk X: /scan</c> for an online read-only check; <c>chkdsk X: /spotfix</c> for a quick fix of logged
    /// problems; <c>chkdsk X: /f /r</c> for the full offline repair. On the Windows volume the full repair is scheduled
    /// for the next restart. On any other volume chkdsk is told not to force a dismount (which would invalidate every
    /// open file on it) and to schedule instead; if nothing is using the volume the repair simply runs right away.
    /// Anything that can change the volume runs through the change guard.
    /// </summary>
    public async Task<OperationResult> CheckDiskAsync(string driveLetter, bool scanOnly, bool scheduleOfflineFix, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        var letter = StorageCodes.NormalizeDriveLetter(driveLetter);
        if (letter is null)
        {
            return OperationResult.Fail("Choose a drive letter first.");
        }

        if (!PlatformGuard.IsWindows)
        {
            return OperationResult.Fail("Check disk is only available on Windows.");
        }

        string arguments = StorageCommandParsers.ChkdskArguments(letter, scanOnly, scheduleOfflineFix);
        if (scanOnly)
        {
            return await RunChkdskAsync(letter, arguments, answers: null, scheduleOfflineFix: false, progress, cancellationToken).ConfigureAwait(false);
        }

        bool systemVolume = StorageCodes.IsSystemVolume(letter, SafeSystemRoot());
        string answers = StorageCommandParsers.ChkdskAnswers(scheduleOfflineFix, systemVolume);
        string description = scheduleOfflineFix
            ? systemVolume
                ? $"Schedule a full check and repair of {letter} at the next restart"
                : $"Full check and repair of {letter} (scheduled for the next restart if the volume is in use, otherwise run now)"
            : $"Spot-fix file system problems on {letter}";
        return await _changeGuard.RunGuardedAsync(description,
            ChangeOptions.For(Module, letter, reversible: false, undoHint: scheduleOfflineFix ? "Run 'chkntfs /x " + letter + "' before restarting to cancel a scheduled check." : null),
            ct => RunChkdskAsync(letter, arguments, answers, scheduleOfflineFix, progress, ct), progress, cancellationToken).ConfigureAwait(false);
    }

    private static readonly TimeSpan ScanTimeout = TimeSpan.FromHours(6);
    private static readonly TimeSpan RepairTimeout = TimeSpan.FromHours(72);

    /// <param name="answers">Lines fed to chkdsk's prompts (see <see cref="StorageCommandParsers.ChkdskAnswers"/>); null for a read-only scan that never prompts.</param>
    /// <param name="scheduleOfflineFix">True for the full repair, whose result may be "scheduled at the next restart": the system state is then checked as well, because chkdsk's text is localized.</param>
    private async Task<OperationResult> RunChkdskAsync(string letter, string arguments, string? answers, bool scheduleOfflineFix, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new ProgressUpdate($"chkdsk {arguments}", null, "Starting…"));
        _logger.LogInformation("Running chkdsk {Arguments}.", arguments);
        // A read-only scan may be stopped at any time. A repair (/f, /r, /spotfix) rewrites the file system: killing it
        // half-way leaves the volume dirty, so it gets a far longer budget and a cancel is reported for what it is.
        bool repairing = answers is not null;
        var timeout = repairing ? RepairTimeout : ScanTimeout;
        CommandRunResult run;
        try
        {
            // chkdsk prints in the OEM code page; decode it as such so drive labels and localized text show correctly.
            run = await StorageCommandRunner.RunAsync("chkdsk.exe", arguments, answers, Line, timeout, cancellationToken,
                StorageCommandRunner.OemEncoding()).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (repairing)
        {
            bool? dirty = VolumeCheckState.IsCheckScheduled(letter);
            _logger.LogWarning("chkdsk repair of {Letter} was stopped before it finished (dirty: {Dirty}).", letter, dirty);
            return OperationResult.Fail(dirty switch
            {
                true => $"chkdsk was stopped while it was repairing {letter}. The volume is marked dirty, so Windows will check and repair it again automatically at the next restart; let that check finish.",
                false => $"chkdsk was stopped while it was repairing {letter}. Windows does not report the volume as dirty, but run a scan before relying on the drive.",
                _ => $"chkdsk was stopped while it was repairing {letter}. Run a scan now; if Windows reports the volume as dirty it will be checked again at the next restart.",
            });
        }

        if (!run.Started)
        {
            return OperationResult.Fail($"Could not start chkdsk: {run.StartError}");
        }

        if (run.TimedOut)
        {
            return OperationResult.Fail(repairing
                ? $"chkdsk did not finish within {RepairTimeout.TotalHours:0} hours and was stopped. The volume may be marked dirty; Windows then checks it again at the next restart."
                : $"chkdsk did not finish within {ScanTimeout.TotalHours:0} hours and was stopped.");
        }

        bool? scheduled = scheduleOfflineFix ? VolumeCheckState.IsCheckScheduled(letter) : null;
        var outcome = StorageCommandParsers.ParseChkdsk(run.Output, run.ExitCode, letter, scheduled);
        progress?.Report(new ProgressUpdate("Done", 100, outcome.Summary));
        bool success = run.ExitCode is 0 or 1 || outcome.RebootScheduled;
        return success
            ? OperationResult.Ok(outcome.Summary, outcome.RebootScheduled)
            : OperationResult.Fail(outcome.Summary);

        void Line(string text)
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                progress?.Report(new ProgressUpdate("chkdsk", null, text.Trim()));
            }
        }
    }

    /// <summary><c>Repair-Volume -DriveLetter X -Scan|-SpotFix|-OfflineScanAndFix</c>. Scan is read-only; the others go through the change guard.</summary>
    public async Task<OperationResult> RepairVolumeAsync(string driveLetter, RepairMode mode, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        var letter = StorageCodes.NormalizeDriveLetter(driveLetter);
        if (letter is null)
        {
            return OperationResult.Fail("Choose a drive letter first.");
        }

        if (!PlatformGuard.IsWindows)
        {
            return OperationResult.Fail("Repair-Volume is only available on Windows.");
        }

        if (mode == RepairMode.Scan)
        {
            return await RunRepairVolumeAsync(letter, mode, progress, cancellationToken).ConfigureAwait(false);
        }

        return await _changeGuard.RunGuardedAsync($"Repair-Volume {mode} on {letter}", ChangeOptions.For(Module, letter),
            ct => RunRepairVolumeAsync(letter, mode, progress, ct), progress, cancellationToken).ConfigureAwait(false);
    }

    private async Task<OperationResult> RunRepairVolumeAsync(string letter, RepairMode mode, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        string command = $"Repair-Volume -DriveLetter {letter[0]} {StorageCommandParsers.RepairVolumeSwitch(mode)} -Verbose 4>&1 | Out-String -Stream";
        progress?.Report(new ProgressUpdate($"Repair-Volume {mode}", null, letter));
        _logger.LogInformation("Running {Command}.", command);
        string? lastWord = null;
        var run = await StorageCommandRunner.RunPowerShellAsync(command, text =>
        {
            var t = text.Trim();
            if (t.Length == 0)
            {
                return;
            }

            progress?.Report(new ProgressUpdate("Repair-Volume", null, t));
            if (!t.Contains(' ') && !t.StartsWith("VERBOSE", StringComparison.OrdinalIgnoreCase))
            {
                lastWord = t;
            }
        }, TimeSpan.FromHours(6), cancellationToken).ConfigureAwait(false);

        if (!run.Started)
        {
            return OperationResult.Fail($"Could not start PowerShell: {run.StartError}");
        }

        if (run.TimedOut)
        {
            return OperationResult.Fail("Repair-Volume did not finish within six hours and was stopped.");
        }

        if (run.ExitCode != 0 && lastWord is null)
        {
            string tail = run.Output.Split('\n').LastOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim() ?? "unknown error";
            return OperationResult.Fail($"Repair-Volume failed on {letter}: {tail}");
        }

        string summary = StorageCommandParsers.DescribeRepairVolumeStatus(lastWord, mode, letter);
        // Only the Windows volume cannot be taken offline; a data volume (even one on the same physical disk) is checked right away.
        bool reboot = mode == RepairMode.OfflineScanAndFix && StorageCodes.IsSystemVolume(letter, SafeSystemRoot());
        if (reboot)
        {
            summary += " The offline check runs the next time Windows starts.";
        }

        progress?.Report(new ProgressUpdate("Done", 100, summary));
        return OperationResult.Ok(summary, reboot);
    }

    /// <summary>
    /// <c>Optimize-Volume -ReTrim</c> for solid-state drives, <c>-Defrag</c> for hard disks, and no switch when the media
    /// type is unknown (Optimize-Volume then chooses), with verbose output streamed to progress.
    /// </summary>
    public async Task<OperationResult> OptimizeAsync(string driveLetter, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        var letter = StorageCodes.NormalizeDriveLetter(driveLetter);
        if (letter is null)
        {
            return OperationResult.Fail("Choose a drive letter first.");
        }

        if (!PlatformGuard.IsWindows)
        {
            return OperationResult.Fail("Optimize-Volume is only available on Windows.");
        }

        var device = FindDeviceByVolume(letter);
        string sw = StorageCommandParsers.OptimizeSwitch(device?.MediaType);
        string what = sw switch
        {
            "-Defrag" => "defragment",
            "-ReTrim" => "send TRIM to",
            _ => "let Windows pick TRIM or defragment for",
        };
        return await _changeGuard.RunGuardedAsync($"Optimize {letter} ({what})", ChangeOptions.For(Module, letter, reversible: false, undoHint: "Optimizing does not change files; nothing to undo."),
            async ct =>
            {
                string command = $"Optimize-Volume -DriveLetter {letter[0]} {sw} -Verbose 4>&1 | Out-String -Stream".Replace("  ", " ");
                progress?.Report(new ProgressUpdate("Optimize-Volume", null, sw.Length == 0 ? letter : $"{letter} {sw}"));
                _logger.LogInformation("Running {Command}.", command);
                var run = await StorageCommandRunner.RunPowerShellAsync(command, text =>
                {
                    var t = text.Trim();
                    if (t.Length > 0)
                    {
                        progress?.Report(new ProgressUpdate("Optimize-Volume", null, t));
                    }
                }, TimeSpan.FromHours(6), ct).ConfigureAwait(false);

                if (!run.Started)
                {
                    return OperationResult.Fail($"Could not start PowerShell: {run.StartError}");
                }

                if (run.TimedOut)
                {
                    return OperationResult.Fail("Optimize-Volume did not finish within six hours and was stopped.");
                }

                if (run.ExitCode != 0)
                {
                    string tail = run.Output.Split('\n').LastOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim() ?? "unknown error";
                    return OperationResult.Fail($"Optimize-Volume failed on {letter}: {tail}");
                }

                return OperationResult.Ok(sw switch
                {
                    "-Defrag" => $"{letter} was defragmented.",
                    "-ReTrim" => $"{letter} optimized: TRIM told the SSD which blocks are free, which keeps it fast.",
                    _ => $"{letter} was optimized; Windows chose TRIM or defragmentation to match the drive.",
                });
            }, progress, cancellationToken).ConfigureAwait(false);
    }
}
