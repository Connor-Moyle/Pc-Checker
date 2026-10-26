using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;

namespace PcChecker.Core.Storage;

public sealed partial class WindowsStorageToolsService
{
    private const int ProgressIntervalMs = 40;
    private const int ConsecutiveFailuresBeforeAbort = 64;
    private const int ResultMapCells = 4000;

    public Task<SurfaceScanResult> SurfaceScanAsync(int diskNumber, int blockSizeMb, IProgress<SurfaceScanProgress>? progress, CancellationToken cancellationToken) =>
        SurfaceScanAsync(diskNumber, SurfaceScanOptions.Full(blockSizeMb), progress, cancellationToken);

    /// <summary>
    /// Reads the drive block by block through <c>\\.\PhysicalDrive{n}</c> with unbuffered sequential reads. Never writes.
    /// A block that fails to read is "bad" (its offset is recorded and the scan skips ahead); a block that takes more
    /// than max(3 × median, 500 ms) is "slow". Progress is reported continuously with a 2000-cell map.
    /// </summary>
    public async Task<SurfaceScanResult> SurfaceScanAsync(int diskNumber, SurfaceScanOptions options, IProgress<SurfaceScanProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!PlatformGuard.IsWindows)
        {
            return new SurfaceScanResult(diskNumber, 0, 0, 0, 0, 0, TimeSpan.Zero, false, [], [], "Surface scans are only available on Windows.");
        }

        if (_lastDevices.Count == 0)
        {
            try
            {
                await GetDevicesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // The scan can still run on a device number alone.
            }
        }

        return await Task.Run(() => RunSurfaceScan(diskNumber, options, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private SurfaceScanResult RunSurfaceScan(int diskNumber, SurfaceScanOptions options, IProgress<SurfaceScanProgress>? progress, CancellationToken cancellationToken)
    {
        var device = FindDevice(diskNumber);
        string model = device?.Model ?? $"Disk {diskNumber}";
        long blockBytes = options.BlockSizeBytes;
        var started = Stopwatch.StartNew();

        Microsoft.Win32.SafeHandles.SafeFileHandle handle;
        try
        {
            handle = RawDiskIo.OpenPhysicalDrive(diskNumber);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not open disk {Number} for a surface scan.", diskNumber);
            return new SurfaceScanResult(diskNumber, 0, 0, 0, 0, 0, TimeSpan.Zero, false, [], [], $"Could not open {model}: {ex.Message}");
        }

        // A read stuck on a failing sector can take tens of seconds; CancelSynchronousIo aborts it so Cancel is immediate.
        using var scanThread = RawDiskIo.OpenCurrentThread();
        using var cancelRead = scanThread is null
            ? default
            : cancellationToken.Register(static state => RawDiskIo.CancelSynchronousIo((Microsoft.Win32.SafeHandles.SafeFileHandle)state!), scanThread);

        using (handle)
        using (var buffer = new AlignedBuffer((int)blockBytes))
        {
            long length = RawDiskIo.GetDriveLength(handle) ?? device?.SizeBytes ?? 0;
            if (length <= 0)
            {
                return new SurfaceScanResult(diskNumber, 0, 0, 0, 0, 0, started.Elapsed, false, [], [], $"Could not determine the size of {model}.");
            }

            long totalBlocks = ScanPlan.BlockCount(length, blockBytes);
            var plan = ScanPlan.Build(totalBlocks, options, seed: diskNumber + 20240);
            // Two fixed-size cell maps instead of one state per block: 2000 cells for live progress and a finer
            // 4000-cell map for the result. Nothing here grows with the size of the drive.
            var cells = new BlockMapAccumulator(totalBlocks);
            var resultCells = new BlockMapAccumulator(totalBlocks, ResultMapCells);
            var median = new RunningMedian();
            var badOffsets = new List<long>();
            long bad = 0, slow = 0, bytesScanned = 0, done = 0;
            double speedSum = 0, speedMin = double.MaxValue;
            int speedSamples = 0;
            int consecutiveFailures = 0;
            int lastError = 0;
            bool completed = false;
            string? abortReason = null;
            var lastReport = Stopwatch.StartNew();
            var blockTimer = new Stopwatch();

            _logger.LogInformation("Surface scan of disk {Number} ({Model}) started: {Mode}, {Blocks} of {Total} blocks, {BlockMb} MB blocks.",
                diskNumber, model, options.Mode, plan.Count, totalBlocks, options.EffectiveBlockSizeMb);

            void SetState(long index, BlockState state)
            {
                cells.Set(index, state);
                resultCells.Set(index, state);
            }

            try
            {
                foreach (long index in plan)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    long offset = index * blockBytes;
                    int toRead = RawDiskIo.AlignDown(Math.Min(blockBytes, length - offset));
                    if (toRead <= 0)
                    {
                        SetState(index, BlockState.Ok);
                        done++;
                        continue;
                    }

                    blockTimer.Restart();
                    var (read, error) = RawDiskIo.ReadAt(handle, offset, buffer, toRead);
                    blockTimer.Stop();
                    if (error != 0 && cancellationToken.IsCancellationRequested)
                    {
                        // The read was aborted by the cancel request, not by the drive: do not count it as a bad block.
                        throw new OperationCanceledException(cancellationToken);
                    }

                    double ms = blockTimer.Elapsed.TotalMilliseconds;

                    BlockState state;
                    double mbps = 0;
                    if (error != 0 || read <= 0)
                    {
                        state = BlockState.Bad;
                        bad++;
                        badOffsets.Add(offset);
                        lastError = error;
                        consecutiveFailures++;
                        if (consecutiveFailures >= ConsecutiveFailuresBeforeAbort && !RawDiskIo.SectorErrors.Contains(error))
                        {
                            abortReason = $"The drive stopped responding ({RawDiskIo.DescribeError(error)}). The scan was stopped after {consecutiveFailures} failed reads in a row.";
                            SetState(index, state);
                            done++;
                            break;
                        }
                    }
                    else
                    {
                        consecutiveFailures = 0;
                        bytesScanned += read;
                        mbps = SurfaceScanMath.BlockMBps(read, ms);
                        double currentMedian = median.Value;
                        state = median.Count >= 8 && SurfaceScanMath.IsSlow(ms, currentMedian) ? BlockState.Slow : BlockState.Ok;
                        if (state == BlockState.Slow)
                        {
                            slow++;
                        }

                        median.Add(ms);
                        speedSum += mbps;
                        speedMin = Math.Min(speedMin, mbps);
                        speedSamples++;
                    }

                    SetState(index, state);
                    done++;

                    if (progress is not null && (state != BlockState.Ok || lastReport.ElapsedMilliseconds >= ProgressIntervalMs || done == plan.Count))
                    {
                        progress.Report(new SurfaceScanProgress(plan.Count, done, bad, slow, Math.Round(mbps, 1), cells.Snapshot()));
                        lastReport.Restart();
                    }
                }

                completed = abortReason is null && done >= plan.Count;
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Surface scan of disk {Number} cancelled after {Done} blocks.", diskNumber, done);
            }
            catch (Exception ex)
            {
                abortReason = $"The scan stopped unexpectedly: {ex.Message}";
                _logger.LogError(ex, "Surface scan of disk {Number} failed.", diskNumber);
            }

            started.Stop();
            double avg = speedSamples > 0 ? speedSum / speedSamples : 0;
            double min = speedSamples > 0 ? speedMin : 0;
            string summary = SurfaceScanMath.BuildSummary(model, options.Mode, bad, slow, done, completed);
            if (abortReason is not null)
            {
                summary = abortReason + " " + summary;
            }
            else if (!completed && lastError != 0 && bad > 0)
            {
                summary += $" Last read error: {RawDiskIo.DescribeError(lastError)}.";
            }

            IReadOnlyList<BlockState> resultMap = resultCells.Snapshot();
            _logger.LogInformation("Surface scan of disk {Number} finished: {Bad} bad, {Slow} slow, {Done}/{Planned} blocks, avg {Avg:0} MB/s.", diskNumber, bad, slow, done, plan.Count, avg);
            return new SurfaceScanResult(diskNumber, bytesScanned, bad, slow, Math.Round(avg, 1), Math.Round(min, 1), started.Elapsed, completed, badOffsets, resultMap, summary);
        }
    }
}
