using System.Globalization;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Cleanup;
using PcChecker.Core.Common;
using PcChecker.Core.DiskSpace;
using PcChecker.Core.Restore;
using PcChecker.Core.Startup;

namespace PcChecker.Core.Backups;

/// <summary>
/// The changes the Backups page makes. Each one runs through <see cref="IChangeGuard"/> (journal, and a restore point
/// per the user's settings where the change alters system configuration); the page has already asked the user.
/// Backups are moved to the Recycle Bin, never deleted for good.
/// </summary>
public sealed partial class WindowsBackupsService
{
    public Task<OperationResult<RestorePoint>> CreateRestorePointAsync(string description, CancellationToken cancellationToken)
    {
        var text = string.IsNullOrWhiteSpace(description) ? "Manual restore point (PC Checker)" : description.Trim();
        var options = new ChangeOptions(CreateBefore: false, CreateAfter: false, Module, text, Reversible: false, UndoHint: null);
        return _guard.RunGuardedAsync("Create a restore point", options, ct => _restorePoints.CreateAsync(text, ct), null, cancellationToken);
    }

    public Task<OperationResult> DeleteRestorePointAsync(SystemRestorePoint point, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(point);
        if (!PlatformGuard.IsWindows)
        {
            return Task.FromResult(OperationResult.Fail("System Restore is only available on Windows."));
        }

        if (point.SequenceNumber is <= 0 or > uint.MaxValue)
        {
            return Task.FromResult(OperationResult.Fail("That restore point has no number Windows can delete it by."));
        }

        var target = $"Restore point {point.SequenceNumber.ToString(CultureInfo.InvariantCulture)} ({point.Description})";
        var options = new ChangeOptions(CreateBefore: false, CreateAfter: false, Module, target, Reversible: false,
            UndoHint: "A deleted restore point cannot be brought back.");
        return _guard.RunGuardedAsync($"Delete restore point \"{point.Description}\"", options, async ct =>
        {
            var code = await Task.Run(() => CleanupNativeMethods.RemoveRestorePoint((uint)point.SequenceNumber), ct).ConfigureAwait(false);
            if (code == 0)
            {
                return OperationResult.Ok($"The restore point \"{point.Description}\" was deleted.");
            }

            _logger.LogWarning("SRRemoveRestorePoint({Sequence}) returned {Code}.", point.SequenceNumber, code);
            return code is null
                ? OperationResult.Fail("System Restore is not available on this PC, so the restore point was not deleted.")
                : OperationResult.Fail($"Windows did not delete the restore point (error {code.Value.ToString(CultureInfo.InvariantCulture)}). It may already be gone; refresh the list.");
        }, null, cancellationToken);
    }

    public Task<OperationResult> DeleteAllButNewestAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        // The same work as Cleanup's "Older restore points": only SystemRestore points are removed, the newest stays,
        // and each deletion is journaled by the cleanup command service.
        var category = CleanupCatalog.Find("restore.points");
        if (category is null)
        {
            return Task.FromResult(OperationResult.Fail("The restore point cleanup is not available in this version."));
        }

        var options = new ChangeOptions(CreateBefore: false, CreateAfter: false, Module, "All restore points except the newest", Reversible: false,
            UndoHint: "Deleted restore points cannot be brought back.");
        return _guard.RunGuardedAsync("Delete all restore points except the newest", options,
            ct => _cleanupCommands.RunAsync(category, progress, ct), progress, cancellationToken);
    }

    public Task<OperationResult> TurnOnProtectionAsync(string drive, CancellationToken cancellationToken)
    {
        var letter = SystemProtectionRules.NormalizeDrive(drive);
        if (letter is null)
        {
            return Task.FromResult(OperationResult.Fail("That is not a drive letter."));
        }

        // No restore point first: Windows cannot make one on a drive whose protection is off.
        var options = new ChangeOptions(CreateBefore: false, CreateAfter: false, Module, $"System Protection on {letter}", Reversible: true,
            UndoHint: $"Turn System Protection off for {letter} on the Backups page or in System Protection (SystemPropertiesProtection.exe).");
        return _guard.RunGuardedAsync($"Turn on System Protection for {letter}", options,
            ct => _restorePoints.EnableAsync(letter + "\\", ct), null, cancellationToken);
    }

    public Task<OperationResult> TurnOffProtectionAsync(string drive, CancellationToken cancellationToken)
    {
        var letter = SystemProtectionRules.NormalizeDrive(drive);
        if (letter is null)
        {
            return Task.FromResult(OperationResult.Fail("That is not a drive letter."));
        }

        if (!PlatformGuard.IsWindows)
        {
            return Task.FromResult(OperationResult.Fail("System Restore is only available on Windows."));
        }

        // No restore point first: turning protection off deletes the drive's restore points anyway.
        var options = new ChangeOptions(CreateBefore: false, CreateAfter: false, Module, $"System Protection on {letter}", Reversible: false,
            UndoHint: $"Turn System Protection on again for {letter} on the Backups page. The deleted restore points do not come back.");
        return _guard.RunGuardedAsync($"Turn off System Protection for {letter}", options, async ct =>
        {
            var script = $"$ErrorActionPreference = 'Stop'; Disable-ComputerRestore -Drive {PowerShellRunner.Quote(letter + "\\")}; 'ok'";
            var result = await PowerShellRunner.RunAsync(script, ToolTimeout, ct).ConfigureAwait(false);
            if (!result.Success)
            {
                var error = PowerShellRunner.DecodeCliXml(result.StdErr);
                error = string.IsNullOrWhiteSpace(error) ? $"PowerShell exit code {result.ExitCode}" : error.Split('\n')[0].Trim();
                _logger.LogWarning("Disable-ComputerRestore failed for {Drive}: {Error}", letter, error);
                return OperationResult.Fail($"Could not turn off System Protection for {letter}: {error}");
            }

            return OperationResult.Ok($"System Protection is now off for {letter}. Its restore points were deleted.");
        }, null, cancellationToken);
    }

    public Task<OperationResult> SetMaxSpaceAsync(string drive, int percent, CancellationToken cancellationToken)
    {
        var letter = SystemProtectionRules.NormalizeDrive(drive);
        if (letter is null)
        {
            return Task.FromResult(OperationResult.Fail("That is not a drive letter."));
        }

        int value = Math.Clamp(percent, SystemProtectionRules.MinPercent, SystemProtectionRules.MaxPercent);
        var options = new ChangeOptions(null, null, Module, $"Restore point space on {letter}", Reversible: true,
            UndoHint: $"Set the space for {letter} back on the Backups page or in System Protection (Configure).");
        return _guard.RunGuardedAsync($"Let restore points use up to {value.ToString(CultureInfo.InvariantCulture)} % of {letter}", options,
            ct => ResizeAsync(letter, SystemProtectionRules.MaxSizeArgument(value), $"Restore points on {letter} can now use up to {value.ToString(CultureInfo.InvariantCulture)} % of the drive.", ct),
            null, cancellationToken);
    }

    public Task<OperationResult> RestoreMaxSpaceAsync(string drive, ShadowStorageInfo previous, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(previous);
        var letter = SystemProtectionRules.NormalizeDrive(drive);
        if (letter is null)
        {
            return Task.FromResult(OperationResult.Fail("That is not a drive letter."));
        }

        var options = new ChangeOptions(CreateBefore: false, CreateAfter: false, Module, $"Restore point space on {letter}", Reversible: true, UndoHint: null);
        return _guard.RunGuardedAsync($"Put back the restore point space on {letter}", options,
            ct => ResizeAsync(letter, SystemProtectionRules.MaxSizeArgument(previous), $"The restore point space on {letter} is back to what it was.", ct),
            null, cancellationToken);
    }

    /// <summary>vssadmin resize shadowstorage for a drive whose restore points are kept on the same drive.</summary>
    private async Task<OperationResult> ResizeAsync(string letter, string maxSizeArgument, string successMessage, CancellationToken ct)
    {
        if (!PlatformGuard.IsWindows)
        {
            return OperationResult.Fail("System Restore is only available on Windows.");
        }

        var output = await CommandRunner.RunAsync(CommandRunner.SystemTool("vssadmin.exe"),
            ["resize", "shadowstorage", "/For=" + letter, "/On=" + letter, maxSizeArgument], ToolTimeout, ct).ConfigureAwait(false);
        if (output.Success)
        {
            _logger.LogInformation("Shadow storage on {Drive} resized ({Argument}).", letter, maxSizeArgument);
            return OperationResult.Ok(successMessage);
        }

        _logger.LogWarning("vssadmin resize shadowstorage for {Drive} failed with {Code}: {Line}", letter, output.ExitCode, output.FirstLine);
        return OperationResult.Fail(output.TimedOut
            ? "Windows took too long to change the space for restore points."
            : $"Windows did not change the space for restore points on {letter}. Turn System Protection on for the drive first, or use System Protection's Configure button. ({output.FirstLine})");
    }

    public Task<OperationResult<int>> DeleteOldAppBackupsAsync(string locationId, int keepNewest, CancellationToken cancellationToken)
    {
        var location = Locations().FirstOrDefault(l => string.Equals(l.Id, locationId, StringComparison.Ordinal));
        if (location is null || !location.CanDeleteOld)
        {
            return Task.FromResult(OperationResult<int>.Fail("These backups cannot be deleted here."));
        }

        int keep = Math.Max(AppBackupCatalog.MinimumKept, keepNewest);
        var options = new ChangeOptions(CreateBefore: false, CreateAfter: false, Module, location.Folder, Reversible: false,
            UndoHint: "The deleted backups are in the Recycle Bin; restore them from there.");
        return _guard.RunGuardedAsync($"Delete old {location.Name.ToLowerInvariant()} (keep the newest {keep.ToString(CultureInfo.InvariantCulture)})", options,
            ct => Task.Run(() => RecycleOld(location, keep, ct), ct), null, cancellationToken);
    }

    private OperationResult<int> RecycleOld(AppBackupLocation location, int keep, CancellationToken ct)
    {
        var toDelete = AppBackupCatalog.SelectToDelete(AppBackupFiles.List(location, ct), keep);
        if (toDelete.Count == 0)
        {
            return OperationResult<int>.Ok(0, "Nothing to delete: there are no more backups than you chose to keep.");
        }

        int moved = 0;
        string? firstError = null;
        foreach (var item in toDelete)
        {
            ct.ThrowIfCancellationRequested();
            if (!AppBackupFiles.IsInside(location, item.Path))
            {
                continue;
            }

            var result = DiskSpaceNativeMethods.MoveToRecycleBin(item.Path, 0);
            if (!result.Success)
            {
                firstError ??= result.Message;
                _logger.LogWarning("Could not move {Path} to the Recycle Bin: {Message}", item.Path, result.Message);
                continue;
            }

            moved++;
            foreach (var companion in AppBackupFiles.Companions(location, item.Path).Where(File.Exists))
            {
                DiskSpaceNativeMethods.MoveToRecycleBin(companion, 0);
            }
        }

        if (moved == 0)
        {
            return OperationResult<int>.Fail("No backup was deleted: " + (firstError ?? "Windows refused."));
        }

        string message = moved == 1 ? "1 old backup was moved to the Recycle Bin." : $"{moved} old backups were moved to the Recycle Bin.";
        return firstError is null
            ? OperationResult<int>.Ok(moved, message)
            : OperationResult<int>.Ok(moved, message + " Some could not be moved: " + firstError);
    }
}
