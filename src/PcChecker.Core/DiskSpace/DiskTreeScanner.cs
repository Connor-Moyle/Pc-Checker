using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;
using PcChecker.Core.Common;

namespace PcChecker.Core.DiskSpace;

/// <summary>
/// Reads a folder tree with several threads at once: each thread takes a folder from a shared queue, lists it once
/// (names, sizes, times and attributes come with the listing, so there is no extra call per file), queues its
/// sub-folders and records its files. Links are not followed, unreadable folders are counted and the run stops
/// promptly on cancel. Uses only .NET file APIs, so it also runs in the tests on Linux.
/// </summary>
public static class DiskTreeScanner
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Scans <paramref name="rootPath"/> and returns the aggregated tree and file lists.</summary>
    public static DiskScanResult Scan(string rootPath, DiskScanOptions options, DriveSpaceInfo? drive, string? windowsDirectory,
        IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(options);
        var run = new ScanRun(rootPath, options, drive, windowsDirectory, progress, cancellationToken);
        return run.Execute();
    }

    /// <summary>True when a folder with the reparse-point attribute is a link (junction, symbolic link, mounted volume).</summary>
    internal static bool IsLink(string fullPath)
    {
        try
        {
            return new DirectoryInfo(fullPath).LinkTarget is not null;
        }
        catch
        {
            // When Windows cannot say what it is, treat it as a link: never risk following it.
            return true;
        }
    }

    private sealed class ScanRun
    {
        private static readonly EnumerationOptions ListingOptions = new()
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = false,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
            BufferSize = 64 * 1024,
        };

        private readonly string _rootPath;
        private readonly DiskScanOptions _options;
        private readonly DriveSpaceInfo? _drive;
        private readonly string? _windowsDirectory;
        private readonly IProgress<ProgressUpdate>? _progress;
        private readonly CancellationToken _ct;
        private readonly ConcurrentQueue<FolderNode> _queue = new();
        private readonly SemaphoreSlim _available = new(0);
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private int _pending;
        private long _bytesSoFar;
        private long _filesSoFar;
        private long _foldersSoFar;
        private long _inaccessible;
        private long _links;
        private long _lastReportTicks;

        public ScanRun(string rootPath, DiskScanOptions options, DriveSpaceInfo? drive, string? windowsDirectory,
            IProgress<ProgressUpdate>? progress, CancellationToken ct)
        {
            _rootPath = DiskSpacePaths.Normalize(rootPath);
            _options = options;
            _drive = drive;
            _windowsDirectory = windowsDirectory;
            _progress = progress;
            _ct = ct;
        }

        public DiskScanResult Execute()
        {
            if (!Directory.Exists(_rootPath))
            {
                throw new DirectoryNotFoundException($"{_rootPath} does not exist or cannot be opened.");
            }

            var now = DateTime.Now;
            var root = new FolderNode(_rootPath);
            int workers = Math.Clamp(Environment.ProcessorCount, 2, 8);
            var collectors = Enumerable.Range(0, workers).Select(_ => new DiskScanCollector(_options, now)).ToArray();

            _pending = 1;
            _queue.Enqueue(root);
            _available.Release();
            _progress?.Report(new ProgressUpdate("Reading " + _rootPath, _drive is null ? null : 0));

            try
            {
                var threads = collectors.Select(c => Task.Factory.StartNew(() => Work(c), _ct, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
                Task.WaitAll(threads);
            }
            catch (AggregateException ex)
            {
                // Report the real failure, not the wrapper; a cancel shows up as one or more cancellations.
                var failure = ex.Flatten().InnerExceptions.FirstOrDefault(e => e is not OperationCanceledException);
                if (failure is null)
                {
                    throw new OperationCanceledException(_ct);
                }

                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
                throw;
            }
            finally
            {
                _available.Dispose();
            }

            _ct.ThrowIfCancellationRequested();
            _progress?.Report(new ProgressUpdate("Adding up folder sizes", 99));
            DiskSpaceAggregation.Aggregate(root);

            var all = collectors[0];
            for (int i = 1; i < collectors.Length; i++)
            {
                all.Merge(collectors[i]);
            }

            var stats = new DiskScanStats(all.Files, root.TotalFolders, root.InaccessibleFolders, Interlocked.Read(ref _links), all.CloudOnlyFiles,
                _clock.Elapsed, all.CandidatesTruncated);
            _progress?.Report(new ProgressUpdate("Done", 100));
            return new DiskScanResult(_rootPath, root, _drive, all.LargestFiles(), all.OldFiles(), all.Types(), all.DuplicateCandidates(), stats,
                DateTimeOffset.Now);
        }

        private void Work(DiskScanCollector collector)
        {
            while (true)
            {
                _available.Wait(_ct);
                if (Volatile.Read(ref _pending) == 0)
                {
                    // Everything has been read: pass the wake-up on so every other thread stops too.
                    _available.Release();
                    return;
                }

                if (!_queue.TryDequeue(out var node))
                {
                    continue;
                }

                try
                {
                    ReadFolder(node, collector);
                }
                finally
                {
                    if (Interlocked.Decrement(ref _pending) == 0)
                    {
                        _available.Release();
                    }
                }
            }
        }

        private void ReadFolder(FolderNode node, DiskScanCollector collector)
        {
            _ct.ThrowIfCancellationRequested();
            var kind = FileTypeClassifier.ClassifyFolder(node.FullPath, _windowsDirectory);
            long bytesBefore = collector.Bytes;
            long filesBefore = collector.Files;
            int seen = 0;
            try
            {
                var listing = new FileSystemEnumerable<byte>(EnumerationPath(node.FullPath), static (ref FileSystemEntry _) => 0, ListingOptions)
                {
                    ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                    {
                        if ((++seen & 1023) == 0)
                        {
                            _ct.ThrowIfCancellationRequested();
                        }

                        if (entry.IsDirectory)
                        {
                            AddFolder(node, ref entry);
                        }
                        else
                        {
                            long counted = collector.AddFile(node.FullPath, entry.FileName, entry.Length, entry.Attributes,
                                entry.LastWriteTimeUtc.LocalDateTime, LastAccess(ref entry), kind);
                            node.AddFile(counted);
                        }

                        return false;
                    },
                };

                foreach (var _ in listing)
                {
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (DirectoryNotFoundException)
            {
                // Removed while the scan was running.
            }
            catch (Exception)
            {
                // Access denied, a path Windows cannot open, or anything else odd about this one folder: count it and
                // carry on with the rest of the drive.
                node.IsInaccessible = true;
                Interlocked.Increment(ref _inaccessible);
            }

            Interlocked.Add(ref _bytesSoFar, collector.Bytes - bytesBefore);
            Interlocked.Add(ref _filesSoFar, collector.Files - filesBefore);
            Interlocked.Increment(ref _foldersSoFar);
            MaybeReport(node.FullPath);
        }

        private void AddFolder(FolderNode node, ref FileSystemEntry entry)
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 && IsLink(entry.ToFullPath()))
            {
                Interlocked.Increment(ref _links);
                return;
            }

            var child = node.AddChild(entry.FileName.ToString());
            Interlocked.Increment(ref _pending);
            _queue.Enqueue(child);
            _available.Release();
        }

        private void MaybeReport(string currentFolder)
        {
            if (_progress is null)
            {
                return;
            }

            long now = _clock.ElapsedTicks;
            long last = Interlocked.Read(ref _lastReportTicks);
            if (now - last < ProgressInterval.TotalSeconds * Stopwatch.Frequency || Interlocked.CompareExchange(ref _lastReportTicks, now, last) != last)
            {
                return;
            }

            long bytes = Interlocked.Read(ref _bytesSoFar);
            double? percent = _drive is { UsedBytes: > 0 } d ? Math.Min(98, 100.0 * bytes / d.UsedBytes) : null;
            var detail = $"{DiskSpaceFormat.Count(Interlocked.Read(ref _filesSoFar), "file", "files")} · {ByteSize.Format(bytes)}"
                + (Interlocked.Read(ref _inaccessible) is > 0 and var n ? $" · {DiskSpaceFormat.Count(n, "folder", "folders")} not readable" : string.Empty)
                + "\n" + DiskSpaceFormat.ShortenPath(currentFolder);
            _progress.Report(new ProgressUpdate("Reading " + _rootPath, percent, detail));
        }

        private static DateTime? LastAccess(ref FileSystemEntry entry)
        {
            var access = entry.LastAccessTimeUtc;
            return access.Year < 1990 ? null : access.LocalDateTime;
        }

        // A drive root needs its trailing separator ("C:" alone means the current folder on drive C).
        private static string EnumerationPath(string path) =>
            path.Length == 2 && path[1] == ':' ? path + Path.DirectorySeparatorChar : path;
    }
}
