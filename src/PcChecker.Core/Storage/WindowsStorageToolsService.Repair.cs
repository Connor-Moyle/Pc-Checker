using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Restore;

namespace PcChecker.Core.Storage;

public sealed partial class WindowsStorageToolsService
{
    private const string Module = "Storage";

    /// <summary>
    /// <c>chkdsk X: /scan</c> for an online read-only check; <c>chkdsk X: /spotfix</c> for a quick fix of logged
    /// problems; <c>chkdsk X: /f /r</c> (answering "Y" to schedule at the next restart) for the full offline repair.
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
            return await RunChkdskAsync(letter, arguments, feedYes: false, progress, cancellationToken).ConfigureAwait(false);
        }

        string description = scheduleOfflineFix ? $"Schedule a full check and repair of {letter} at the next restart" : $"Spot-fix file system problems on {letter}";
        return await _changeGuard.RunGuardedAsync(description,
            ChangeOptions.For(Module, letter, reversible: false, undoHint: scheduleOfflineFix ? "Run 'chkntfs /x " + letter + "' before restarting to cancel the scheduled check." : null),
            ct => RunChkdskAsync(letter, arguments, feedYes: true, progress, ct), progress, cancellationToken).ConfigureAwait(false);
    }

    private async Task<OperationResult> RunChkdskAsync(string letter, string arguments, bool feedYes, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new ProgressUpdate($"chkdsk {arguments}", null, "Starting…"));
        _logger.LogInformation("Running chkdsk {Arguments}.", arguments);
        CommandRunResult run;
        if (feedYes)
        {
            // "echo Y|" answers the "schedule at next restart?" / "force a dismount?" prompt without a console.
            run = await StorageCommandRunner.RunAsync("cmd.exe", $"/d /c echo Y| chkdsk {arguments}", null, Line, TimeSpan.FromHours(6), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            run = await StorageCommandRunner.RunAsync("chkdsk.exe", arguments, null, Line, TimeSpan.FromHours(6), cancellationToken).ConfigureAwait(false);
        }

        if (!run.Started)
        {
            return OperationResult.Fail($"Could not start chkdsk: {run.StartError}");
        }

        if (run.TimedOut)
        {
            return OperationResult.Fail("chkdsk did not finish within six hours and was stopped.");
        }

        var outcome = StorageCommandParsers.ParseChkdsk(run.Output, run.ExitCode, letter);
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
        bool reboot = mode == RepairMode.OfflineScanAndFix && (FindDeviceByVolume(letter)?.IsSystemDisk ?? true);
        if (reboot)
        {
            summary += " The offline check runs the next time Windows starts.";
        }

        progress?.Report(new ProgressUpdate("Done", 100, summary));
        return OperationResult.Ok(summary, reboot);
    }

    /// <summary><c>Optimize-Volume -ReTrim</c> for solid-state drives, <c>-Defrag</c> for hard disks, with verbose output streamed to progress.</summary>
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
        string what = sw == "-Defrag" ? "defragment" : "send TRIM to";
        return await _changeGuard.RunGuardedAsync($"Optimize {letter} ({what})", ChangeOptions.For(Module, letter, reversible: false, undoHint: "Optimizing does not change files; nothing to undo."),
            async ct =>
            {
                string command = $"Optimize-Volume -DriveLetter {letter[0]} {sw} -Verbose 4>&1 | Out-String -Stream";
                progress?.Report(new ProgressUpdate("Optimize-Volume", null, $"{letter} {sw}"));
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

                return OperationResult.Ok(sw == "-Defrag"
                    ? $"{letter} was defragmented."
                    : $"{letter} optimized: TRIM told the SSD which blocks are free, which keeps it fast.");
            }, progress, cancellationToken).ConfigureAwait(false);
    }
}
