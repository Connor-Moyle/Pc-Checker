using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;

namespace PcChecker.Core.Storage;

public sealed partial class WindowsStorageToolsService
{
    private const int SequentialBlockBytes = 1024 * 1024;
    private const int RandomBlockBytes = 4096;
    private const int RandomOperations = 2000;

    /// <summary>
    /// Writes a temporary file with unbuffered, write-through 1 MB blocks, reads it back sequentially, then performs
    /// 2000 random 4 K reads and 2000 random 4 K writes at queue depth 1. The file is deleted afterwards whatever happens.
    /// </summary>
    public async Task<DiskBenchmarkResult> BenchmarkAsync(string driveLetter, int testFileMb, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        var letter = StorageCodes.NormalizeDriveLetter(driveLetter);
        if (letter is null)
        {
            return Failed(driveLetter, testFileMb, "Choose a drive letter to benchmark.");
        }

        if (!PlatformGuard.IsWindows)
        {
            return Failed(letter, testFileMb, "Benchmarks are only available on Windows.");
        }

        testFileMb = Math.Clamp(testFileMb, 64, 16384);
        long testBytes = testFileMb * 1024L * 1024L;
        try
        {
            var drive = new DriveInfo(letter);
            if (!drive.IsReady)
            {
                return Failed(letter, testFileMb, $"{letter} is not ready.");
            }

            long needed = testBytes + testBytes / 10;
            if (drive.AvailableFreeSpace < needed)
            {
                return Failed(letter, testFileMb, $"{letter} needs at least {ByteSize.Format(needed)} free for a {testFileMb} MB test (only {ByteSize.Format(drive.AvailableFreeSpace)} free). Pick a smaller size.");
            }
        }
        catch (Exception ex)
        {
            return Failed(letter, testFileMb, $"Could not inspect {letter}: {ex.Message}");
        }

        return await Task.Run(() => RunBenchmark(letter, testBytes, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private DiskBenchmarkResult RunBenchmark(string letter, long testBytes, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        string path = Path.Combine(letter + Path.DirectorySeparatorChar, $"PcChecker-bench-{Guid.NewGuid():N}.bin");
        var device = FindDeviceByVolume(letter);
        var expected = BenchmarkReference.Expected(device?.MediaType, device?.BusType);
        _logger.LogInformation("Benchmark of {Drive} started with a {Size} test file at {Path}.", letter, ByteSize.Format(testBytes), path);

        double seqWrite = 0, seqRead = 0, randRead = 0, randWrite = 0, randReadIops = 0, randWriteIops = 0;
        try
        {
            using var handle = RawDiskIo.CreateUnbufferedFile(path);
            using var big = new AlignedBuffer(SequentialBlockBytes);
            using var small = new AlignedBuffer(RandomBlockBytes);
            big.FillRandom(1);
            small.FillRandom(2);
            long blocks = testBytes / SequentialBlockBytes;

            // 1. Sequential write
            var timer = Stopwatch.StartNew();
            for (long i = 0; i < blocks; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (written, error) = RawDiskIo.WriteAt(handle, i * SequentialBlockBytes, big, SequentialBlockBytes);
                if (error != 0 || written != SequentialBlockBytes)
                {
                    throw new IOException($"Write failed at {ByteSize.Format(i * SequentialBlockBytes)}: {RawDiskIo.DescribeError(error)}");
                }

                ReportStage(progress, "Sequential write", 0, i + 1, blocks, timer, SequentialBlockBytes);
            }

            timer.Stop();
            RawDiskIo.Flush(handle);
            seqWrite = BenchmarkMath.MegabytesPerSecond(blocks * SequentialBlockBytes, timer.Elapsed);

            // 2. Sequential read
            timer.Restart();
            for (long i = 0; i < blocks; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (read, error) = RawDiskIo.ReadAt(handle, i * SequentialBlockBytes, big, SequentialBlockBytes);
                if (error != 0 || read != SequentialBlockBytes)
                {
                    throw new IOException($"Read failed at {ByteSize.Format(i * SequentialBlockBytes)}: {RawDiskIo.DescribeError(error)}");
                }

                ReportStage(progress, "Sequential read", 25, i + 1, blocks, timer, SequentialBlockBytes);
            }

            timer.Stop();
            seqRead = BenchmarkMath.MegabytesPerSecond(blocks * SequentialBlockBytes, timer.Elapsed);

            // 3. Random 4 K read (QD1)
            long slots = testBytes / RandomBlockBytes;
            var random = new Random(4070);
            timer.Restart();
            for (int i = 0; i < RandomOperations; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long offset = random.NextInt64(slots) * RandomBlockBytes;
                var (read, error) = RawDiskIo.ReadAt(handle, offset, small, RandomBlockBytes);
                if (error != 0 || read != RandomBlockBytes)
                {
                    throw new IOException($"Random read failed at {ByteSize.Format(offset)}: {RawDiskIo.DescribeError(error)}");
                }

                if (i % 100 == 0)
                {
                    ReportStage(progress, "Random 4K read", 50, i + 1, RandomOperations, timer, RandomBlockBytes);
                }
            }

            timer.Stop();
            randRead = BenchmarkMath.Random4kMegabytesPerSecond(RandomOperations, timer.Elapsed);
            randReadIops = BenchmarkMath.Iops(RandomOperations, timer.Elapsed);

            // 4. Random 4 K write (QD1, write-through)
            timer.Restart();
            for (int i = 0; i < RandomOperations; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long offset = random.NextInt64(slots) * RandomBlockBytes;
                var (written, error) = RawDiskIo.WriteAt(handle, offset, small, RandomBlockBytes);
                if (error != 0 || written != RandomBlockBytes)
                {
                    throw new IOException($"Random write failed at {ByteSize.Format(offset)}: {RawDiskIo.DescribeError(error)}");
                }

                if (i % 100 == 0)
                {
                    ReportStage(progress, "Random 4K write", 75, i + 1, RandomOperations, timer, RandomBlockBytes);
                }
            }

            timer.Stop();
            randWrite = BenchmarkMath.Random4kMegabytesPerSecond(RandomOperations, timer.Elapsed);
            randWriteIops = BenchmarkMath.Iops(RandomOperations, timer.Elapsed);
            progress?.Report(new ProgressUpdate("Done", 100));
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Benchmark of {Drive} cancelled.", letter);
            TryDelete(path);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Benchmark of {Drive} failed.", letter);
            TryDelete(path);
            return Failed(letter, (int)(testBytes / (1024 * 1024)), $"The benchmark could not finish: {ex.Message}");
        }

        TryDelete(path);
        var result = new DiskBenchmarkResult(letter, Round(seqRead), Round(seqWrite), Round(randRead), Round(randWrite), Math.Round(randReadIops), Math.Round(randWriteIops), testBytes, string.Empty);
        result = result with { Summary = BenchmarkReference.Summarize(result, expected) };
        _logger.LogInformation("Benchmark of {Drive}: seq {Read:0}/{Write:0} MB/s, 4K {RRead:0.0}/{RWrite:0.0} MB/s.", letter, result.SeqReadMBps, result.SeqWriteMBps, result.Rand4kReadMBps, result.Rand4kWriteMBps);
        return result;
    }

    private static void ReportStage(IProgress<ProgressUpdate>? progress, string stage, double basePercent, long done, long total, Stopwatch timer, long bytesPerOp)
    {
        if (progress is null || total <= 0)
        {
            return;
        }

        if (done != total && done % Math.Max(1, total / 20) != 0)
        {
            return;
        }

        double mbps = BenchmarkMath.MegabytesPerSecond(done * bytesPerOp, timer.Elapsed);
        progress.Report(new ProgressUpdate(stage, basePercent + 25.0 * done / total, $"{mbps:0} MB/s so far"));
    }

    private static double Round(double value) => value >= 100 ? Math.Round(value) : Math.Round(value, 1);

    private static DiskBenchmarkResult Failed(string drive, int testFileMb, string message) =>
        new(drive, 0, 0, 0, 0, 0, 0, testFileMb * 1024L * 1024L, message);

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete the benchmark file {Path}; delete it by hand.", path);
        }
    }
}
