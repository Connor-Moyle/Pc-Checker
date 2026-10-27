using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using PcChecker.Core.Common;

namespace PcChecker.Core.DiskSpace;

/// <summary>
/// Reads candidate files to confirm duplicates: same size first (no reading), then a SHA-256 of the first and last
/// 64 KB, then a SHA-256 of the whole file for the groups that still match. Files are opened read-only and shared, a
/// file that changed size since the scan or cannot be read is left out, and hard links to the same data count once.
/// Uses only .NET file APIs plus an optional identity callback, so it also runs in the tests on Linux.
/// </summary>
public static class DuplicateHasher
{
    private const int FullHashBufferBytes = 1024 * 1024;

    /// <summary>Finds groups of identical files among <paramref name="candidates"/>.</summary>
    /// <param name="identityOf">Returns a volume-and-file-id key for an open file (hard links share it), or null.</param>
    public static async Task<DuplicateScanResult> FindAsync(IReadOnlyList<DiskFileEntry> candidates, long minBytes,
        Func<SafeFileHandle, string?>? identityOf, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var clock = Stopwatch.StartNew();
        var bySize = DuplicateFinder.GroupBySize(candidates, minBytes);
        var files = bySize.SelectMany(g => g).ToList();
        int parallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 4);
        long unreadable = 0;

        // Quick hash: start and end of every file that shares its size with another.
        var quick = new Dictionary<string, (string? Hash, string? Identity)>(StringComparer.OrdinalIgnoreCase);
        int done = 0;
        progress?.Report(new ProgressUpdate("Comparing files of the same size", 0, DiskSpaceFormat.Count(files.Count, "file", "files")));
        await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken }, (file, ct) =>
        {
            var (hash, identity) = QuickHash(file, identityOf, ct);
            lock (quick)
            {
                quick[file.Path] = (hash, identity);
                if (hash is null)
                {
                    unreadable++;
                }

                done++;
                if (done % 64 == 0)
                {
                    progress?.Report(new ProgressUpdate("Comparing files of the same size", 50.0 * done / Math.Max(1, files.Count),
                        $"{done:#,0} of {files.Count:#,0} files"));
                }
            }

            return ValueTask.CompletedTask;
        }).ConfigureAwait(false);

        var distinct = bySize.Select(g => DuplicateFinder.CollapseSameFile(g, f => quick.GetValueOrDefault(f.Path).Identity)).Where(g => g.Count > 1);
        var quickGroups = DuplicateFinder.SplitBy(distinct, f => quick.GetValueOrDefault(f.Path).Hash);

        // Full hash only where the quick hash did not already read the whole file.
        var needFull = quickGroups.Where(g => DuplicateFinder.NeedsFullHash(g[0].SizeBytes)).SelectMany(g => g).ToList();
        long totalBytes = Math.Max(1, needFull.Sum(f => f.SizeBytes));
        long hashedBytes = 0;
        long lastReportMs = 0;
        var full = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        await Parallel.ForEachAsync(needFull, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken }, async (file, ct) =>
        {
            var hash = await FullHashAsync(file, ct, read =>
            {
                long now = Interlocked.Add(ref hashedBytes, read);
                long ticks = clock.ElapsedMilliseconds;
                long last = Interlocked.Read(ref lastReportMs);
                if (ticks - last >= 250 && Interlocked.CompareExchange(ref lastReportMs, ticks, last) == last)
                {
                    progress?.Report(new ProgressUpdate("Confirming matches byte for byte", 50 + 50.0 * now / totalBytes,
                        $"{ByteSize.Format(now)} of {ByteSize.Format(totalBytes)}"));
                }
            }).ConfigureAwait(false);
            lock (full)
            {
                full[file.Path] = hash;
                if (hash is null)
                {
                    unreadable++;
                }
            }
        }).ConfigureAwait(false);

        var confirmed = new List<(string Hash, IReadOnlyList<DiskFileEntry> Files)>();
        foreach (var group in quickGroups)
        {
            if (!DuplicateFinder.NeedsFullHash(group[0].SizeBytes))
            {
                confirmed.Add((quick[group[0].Path].Hash!, group));
                continue;
            }

            foreach (var part in DuplicateFinder.SplitBy([group], f => full.GetValueOrDefault(f.Path)))
            {
                confirmed.Add((full[part[0].Path]!, part));
            }
        }

        progress?.Report(new ProgressUpdate("Done", 100));
        return new DuplicateScanResult(DuplicateFinder.ToGroups(confirmed), files.Count, unreadable, clock.Elapsed);
    }

    private static (string? Hash, string? Identity) QuickHash(DiskFileEntry file, Func<SafeFileHandle, string?>? identityOf, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            using var handle = File.OpenHandle(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (RandomAccess.GetLength(handle) != file.SizeBytes)
            {
                return (null, null);
            }

            string? identity = null;
            try
            {
                identity = identityOf?.Invoke(handle);
            }
            catch
            {
                identity = null;
            }

            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[DuplicateFinder.PartialBlockBytes];
            foreach (var (offset, length) in DuplicateFinder.PartialRanges(file.SizeBytes))
            {
                int read = 0;
                while (read < length)
                {
                    int n = RandomAccess.Read(handle, buffer.AsSpan(read, length - read), offset + read);
                    if (n == 0)
                    {
                        return (null, identity);
                    }

                    read += n;
                }

                sha.AppendData(buffer, 0, length);
            }

            return (Convert.ToHexString(sha.GetHashAndReset()), identity);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return (null, null);
        }
    }

    private static async Task<string?> FullHashAsync(DiskFileEntry file, CancellationToken ct, Action<int> onRead)
    {
        try
        {
            await using var stream = new FileStream(file.Path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                Options = FileOptions.SequentialScan | FileOptions.Asynchronous,
                BufferSize = 0,
            });
            if (stream.Length != file.SizeBytes)
            {
                return null;
            }

            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[FullHashBufferBytes];
            int n;
            while ((n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                sha.AppendData(buffer, 0, n);
                onRead(n);
            }

            return Convert.ToHexString(sha.GetHashAndReset());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }
}
