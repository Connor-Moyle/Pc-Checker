using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using PcChecker.Core.Common;
using PcChecker.Core.Logging;
using PcChecker.Core.Performance;

namespace PcChecker.Core.Cleanup;

/// <summary>
/// Windows implementation of <see cref="ICleanupCommandService"/>. Every tool is started by full path from System32,
/// with fixed arguments, a timeout and cancellation; DISM output is read by position (any display language) and asked
/// for in English; Disk Cleanup handlers are flagged for one /sagerun only and unflagged afterwards, whatever happens.
/// </summary>
public sealed class WindowsCleanupCommandService : ICleanupCommandService
{
    private const string Module = "Cleanup";
    private static readonly TimeSpan AnalyzeTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan DismTimeout = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan CleanmgrTimeout = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan AnalysisLifetime = TimeSpan.FromHours(1);

    private static readonly object CacheLock = new();
    private static (ComponentStoreAnalysis Analysis, DateTimeOffset Taken)? _cachedAnalysis;

    private readonly IActionJournal _journal;
    private readonly ILogger<WindowsCleanupCommandService> _logger;

    public WindowsCleanupCommandService(IActionJournal journal, ILogger<WindowsCleanupCommandService> logger)
    {
        _journal = journal;
        _logger = logger;
    }

    public async Task<CommandEstimate> EstimateAsync(CleanupCategory category, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(category);
        if (!PlatformGuard.IsWindows)
        {
            return CommandEstimate.Unknown();
        }

        try
        {
            return category.Command switch
            {
                CleanupCommand.ComponentCleanup => await EstimateComponentStoreAsync(cancellationToken).ConfigureAwait(false),
                CleanupCommand.ComponentResetBase => CommandEstimate.Unknown("Included in the Windows Update cleanup size."),
                CleanupCommand.DiskCleanupHandlers => EstimateDiskCleanup(category),
                CleanupCommand.RestorePointsKeepNewest => await EstimateRestorePointsAsync(cancellationToken).ConfigureAwait(false),
                CleanupCommand.ClipboardClear => CommandEstimate.Unknown("Frees no disk space."),
                _ => CommandEstimate.Unknown(),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not measure {Category}.", category.Id);
            return CommandEstimate.Unknown("The size could not be measured.");
        }
    }

    public async Task<OperationResult> RunAsync(CleanupCategory category, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(category);
        if (!PlatformGuard.IsWindows)
        {
            return OperationResult.Fail("This is only possible on Windows.");
        }

        return category.Command switch
        {
            CleanupCommand.ComponentCleanup => await RunDismAsync(resetBase: false, progress, cancellationToken).ConfigureAwait(false),
            CleanupCommand.ComponentResetBase => await RunDismAsync(resetBase: true, progress, cancellationToken).ConfigureAwait(false),
            CleanupCommand.DiskCleanupHandlers => await RunDiskCleanupAsync(category, progress, cancellationToken).ConfigureAwait(false),
            CleanupCommand.RestorePointsKeepNewest => await TrimRestorePointsAsync(progress, cancellationToken).ConfigureAwait(false),
            CleanupCommand.ClipboardClear => await ClearClipboardAsync(cancellationToken).ConfigureAwait(false),
            _ => OperationResult.Fail("Nothing to run for this category."),
        };
    }

    // ---- DISM component store -------------------------------------------------------------------------------------

    private async Task<CommandEstimate> EstimateComponentStoreAsync(CancellationToken ct)
    {
        if (!CleanupNativeMethods.IsElevated())
        {
            return CommandEstimate.Unknown("Run PC Checker as administrator to measure this.");
        }

        ComponentStoreAnalysis? analysis = null;
        lock (CacheLock)
        {
            if (_cachedAnalysis is { } cached && DateTimeOffset.Now - cached.Taken < AnalysisLifetime)
            {
                analysis = cached.Analysis;
            }
        }

        if (analysis is null)
        {
            var result = await ExternalCommand.RunAsync(SystemTool("dism.exe"), "/Online /Cleanup-Image /AnalyzeComponentStore /English", ct, AnalyzeTimeout).ConfigureAwait(false);
            analysis = result.Success ? CleanupCommandRules.ParseAnalyzeComponentStore(result.Output) : null;
            if (analysis is null)
            {
                _logger.LogInformation("DISM /AnalyzeComponentStore gave no result (exit code {Code}).", result.ExitCode);
                return CommandEstimate.Unknown("DISM could not analyse the component store right now.");
            }

            lock (CacheLock)
            {
                _cachedAnalysis = (analysis, DateTimeOffset.Now);
            }
        }

        var note = analysis.CleanupRecommended
            ? $"DISM recommends a cleanup ({analysis.ReclaimablePackages} reclaimable packages). The size is DISM's estimate."
            : "DISM does not recommend a cleanup right now; only temporary data would go.";
        return new CommandEstimate(analysis.EstimatedReclaimableBytes, true, [note]);
    }

    private async Task<OperationResult> RunDismAsync(bool resetBase, IProgress<ProgressUpdate>? progress, CancellationToken ct)
    {
        var args = "/Online /Cleanup-Image /StartComponentCleanup" + (resetBase ? " /ResetBase" : string.Empty);
        var stage = resetBase ? "DISM: resetting the component store base" : "DISM: cleaning up the component store";
        progress?.Report(new ProgressUpdate(stage, null, "This can take 5 to 30 minutes."));
        var result = await ExternalCommand.RunAsync(SystemTool("dism.exe"), args, ct, DismTimeout,
            line => progress?.Report(new ProgressUpdate(stage, null, line))).ConfigureAwait(false);
        lock (CacheLock)
        {
            _cachedAnalysis = null;
        }

        if (result.TimedOut)
        {
            return OperationResult.Fail("DISM did not finish within 60 minutes and was stopped.");
        }

        // 3010: success, a restart completes the work.
        return result.ExitCode switch
        {
            0 => OperationResult.Ok(resetBase ? "DISM removed superseded components and reset the base." : "DISM cleaned up the component store."),
            3010 => OperationResult.Ok("DISM cleaned up the component store. Restart Windows to finish."),
            _ => OperationResult.Fail($"DISM reported a problem (exit code {result.ExitCode}): {result.LastLine}"),
        };
    }

    // ---- Disk Cleanup handlers ------------------------------------------------------------------------------------

    private static CommandEstimate EstimateDiskCleanup(CleanupCategory category)
    {
        var available = VolumeCacheHandlers();
        if (available.Count == 0)
        {
            return CommandEstimate.Unknown("Windows works out the size while it cleans.");
        }

        return CleanupCommandRules.HandlersToRun(category.DiskCleanupHandlers, available).Count == 0
            ? CommandEstimate.Unknown("This version of Windows does not offer this Disk Cleanup item.")
            : CommandEstimate.Unknown("Windows works out the size while it cleans.");
    }

    private static IReadOnlyList<string> VolumeCacheHandlers()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(CleanupCommandRules.VolumeCachesKey, writable: false);
            return key?.GetSubKeyNames() ?? [];
        }
        catch
        {
            return [];
        }
    }

    private async Task<OperationResult> RunDiskCleanupAsync(CleanupCategory category, IProgress<ProgressUpdate>? progress, CancellationToken ct)
    {
        if (!CleanupNativeMethods.IsElevated())
        {
            return OperationResult.Fail("Disk Cleanup items need PC Checker to run as administrator.");
        }

        var available = VolumeCacheHandlers();
        var handlers = CleanupCommandRules.HandlersToRun(category.DiskCleanupHandlers, available);
        if (handlers.Count == 0)
        {
            return OperationResult.Fail("This version of Windows does not offer these Disk Cleanup items.");
        }

        var valueName = CleanupCommandRules.StateFlagsValueName(CleanupCommandRules.SageRunNumber);
        var flagged = new List<string>();
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

            // A run that was interrupted earlier may have left our flag behind on another handler: clear it first.
            foreach (var name in available)
            {
                using var key = hklm.OpenSubKey(CleanupCommandRules.VolumeCachesKey + "\\" + name, writable: true);
                key?.DeleteValue(valueName, throwOnMissingValue: false);
            }

            foreach (var name in handlers)
            {
                using var key = hklm.OpenSubKey(CleanupCommandRules.VolumeCachesKey + "\\" + name, writable: true);
                if (key is null)
                {
                    continue;
                }

                key.SetValue(valueName, 2, RegistryValueKind.DWord);
                flagged.Add(name);
            }

            if (flagged.Count == 0)
            {
                return OperationResult.Fail("The Disk Cleanup items could not be selected.");
            }

            progress?.Report(new ProgressUpdate("Windows Disk Cleanup", null, string.Join(", ", flagged)));
            int exit = await RunHiddenAsync(SystemTool("cleanmgr.exe"), "/sagerun:" + CleanupCommandRules.SageRunNumber, CleanmgrTimeout, ct).ConfigureAwait(false);
            _logger.LogInformation("cleanmgr /sagerun finished with exit code {Code} for {Handlers}.", exit, string.Join(", ", flagged));
            return exit switch
            {
                0 => OperationResult.Ok("Windows Disk Cleanup removed: " + string.Join(", ", flagged) + "."),
                -2 => OperationResult.Fail("Windows Disk Cleanup did not finish within 60 minutes and was stopped."),
                _ => OperationResult.Fail($"Windows Disk Cleanup ended with exit code {exit}."),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return OperationResult.Fail("The Disk Cleanup items could not be selected: " + ex.Message);
        }
        finally
        {
            ClearFlags(flagged, valueName);
        }
    }

    private void ClearFlags(IEnumerable<string> handlers, string valueName)
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            foreach (var name in handlers)
            {
                using var key = hklm.OpenSubKey(CleanupCommandRules.VolumeCachesKey + "\\" + name, writable: true);
                key?.DeleteValue(valueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not remove the Disk Cleanup selection flags.");
        }
    }

    private static async Task<int> RunHiddenAsync(string fileName, string argument, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        psi.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch
        {
            return -1;
        }

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort.
            }

            if (ct.IsCancellationRequested)
            {
                throw;
            }

            return -2;
        }
    }

    // ---- Restore points -------------------------------------------------------------------------------------------

    private sealed record ShadowInfo(int Count, long Used, long Max);

    private static string SystemDrive()
    {
        var drive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
        return drive.Length == 2 && char.IsAsciiLetter(drive[0]) && drive[1] == ':' ? drive.ToUpperInvariant() : "C:";
    }

    private static async Task<ShadowInfo?> QueryShadowsAsync(CancellationToken ct)
    {
        // CIM gives numbers in every display language, unlike vssadmin's text.
        var drive = SystemDrive();
        var script =
            $"$v = Get-CimInstance Win32_Volume -Filter \"DriveLetter='{drive}'\" -ErrorAction Stop; " +
            "$c = @(Get-CimInstance Win32_ShadowCopy -ErrorAction SilentlyContinue | Where-Object { $_.VolumeName -eq $v.DeviceID }).Count; " +
            "$s = @(Get-CimInstance Win32_ShadowStorage -ErrorAction SilentlyContinue | Where-Object { $_.Volume.DeviceID -eq $v.DeviceID }) | Select-Object -First 1; " +
            "[pscustomobject]@{ Count = $c; Used = [int64]($s.UsedSpace); Max = [int64]($s.MaxSpace) } | ConvertTo-Json -Compress";
        var result = await PowerShellRunner.RunAsync(script, TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
        if (!result.Success || string.IsNullOrWhiteSpace(result.StdOut))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(result.StdOut.Trim());
            var root = doc.RootElement;
            return new ShadowInfo(root.GetProperty("Count").GetInt32(), ReadLong(root, "Used"), ReadLong(root, "Max"));
        }
        catch
        {
            return null;
        }
    }

    private static long ReadLong(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    private static async Task<CommandEstimate> EstimateRestorePointsAsync(CancellationToken ct)
    {
        if (!CleanupNativeMethods.IsElevated())
        {
            return CommandEstimate.Unknown("Run PC Checker as administrator to measure this.");
        }

        var info = await QueryShadowsAsync(ct).ConfigureAwait(false);
        if (info is null)
        {
            return CommandEstimate.Unknown("Windows did not report its restore point storage.");
        }

        var points = info.Count == 1 ? "1 restore point" : $"{info.Count} restore points";
        var note = info.Max > 0
            ? $"{points} use {ByteSize.Format(info.Used)} of up to {ByteSize.Format(info.Max)}. The size is an estimate; restore points share data."
            : $"{points}. The size is an estimate; restore points share data.";
        return new CommandEstimate(CleanupCommandRules.RestorePointEstimate(info.Used, info.Count), true, [note]);
    }

    private async Task<OperationResult> TrimRestorePointsAsync(IProgress<ProgressUpdate>? progress, CancellationToken ct)
    {
        if (!CleanupNativeMethods.IsElevated())
        {
            return OperationResult.Fail("Deleting restore points needs PC Checker to run as administrator.");
        }

        var drive = SystemDrive();
        var first = await QueryShadowsAsync(ct).ConfigureAwait(false);
        if (first is null)
        {
            return OperationResult.Fail("Windows did not report its restore points, so none were deleted.");
        }

        int initial = first.Count;
        int before = initial;
        int done = 0;
        while (CleanupCommandRules.ShouldDeleteAnother(before, before + 1, done, initial))
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new ProgressUpdate("Deleting older restore points", 100.0 * done / Math.Max(1, initial - 1), $"{before} left"));
            var result = await ExternalCommand.RunAsync(SystemTool("vssadmin.exe"), $"delete shadows /for={drive} /oldest /quiet", ct, TimeSpan.FromMinutes(5)).ConfigureAwait(false);
            var now = await QueryShadowsAsync(ct).ConfigureAwait(false);
            bool deleted = result.Success && now is not null && now.Count < before;
            await _journal.RecordAsync(ActionRecord.Create(Module, "Delete oldest restore point", drive,
                deleted ? "Deleted" : $"Failed (exit code {result.ExitCode})",
                before: $"{before} restore points", after: now is null ? null : $"{now.Count} restore points",
                undoHint: "A deleted restore point cannot be brought back.")).ConfigureAwait(false);
            if (!deleted || now is null)
            {
                return done == 0
                    ? OperationResult.Fail("Windows did not delete the oldest restore point: " + result.LastLine)
                    : OperationResult.Ok($"Deleted {done} older restore points; Windows stopped before the rest.");
            }

            done++;
            if (!CleanupCommandRules.ShouldDeleteAnother(now.Count, before, done, initial))
            {
                break;
            }

            before = now.Count;
        }

        return done == 0
            ? OperationResult.Ok("There was only one restore point, so nothing was deleted.")
            : OperationResult.Ok(done == 1 ? "Deleted 1 older restore point; the newest one stays." : $"Deleted {done} older restore points; the newest one stays.");
    }

    // ---- Clipboard --------------------------------------------------------------------------------------------------

    private const string ClipboardHistoryMarker = "PCCHECKER_HISTORY=";

    private static async Task<OperationResult> ClearClipboardAsync(CancellationToken ct)
    {
        // Windows PowerShell runs STA, which both clipboard APIs need. ClearHistory keeps pinned items.
        var script =
            "Add-Type -AssemblyName System.Windows.Forms; [System.Windows.Forms.Clipboard]::Clear(); " +
            "$h = $false; try { $null = [Windows.ApplicationModel.DataTransfer.Clipboard, Windows.ApplicationModel.DataTransfer, ContentType = WindowsRuntime]; " +
            "$h = [Windows.ApplicationModel.DataTransfer.Clipboard]::ClearHistory() } catch { $h = $false }; " +
            $"'{ClipboardHistoryMarker}' + $h";
        var result = await PowerShellRunner.RunAsync(script, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        if (!result.Success)
        {
            return OperationResult.Fail("The clipboard could not be emptied.");
        }

        bool history = result.StdOut.Contains(ClipboardHistoryMarker + "True", StringComparison.OrdinalIgnoreCase);
        return OperationResult.Ok(history
            ? "The clipboard and the clipboard history were emptied. Pinned items stay."
            : "The clipboard was emptied. Clipboard history is off or could not be cleared.");
    }

    private static string SystemTool(string exe) => Path.Combine(Environment.SystemDirectory, exe);
}
