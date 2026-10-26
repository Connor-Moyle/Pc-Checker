using PcChecker.Core.Common;

namespace PcChecker.Core.Software;

/// <summary>
/// Computes the on-disk size of install folders for programs whose installer did not record one. Runs in parallel,
/// is cancellable and never follows reparse points (junctions/symlinks) so a folder is counted once.
/// </summary>
public static class DirectorySizeCalculator
{
    public const int DefaultParallelism = 4;

    /// <summary>True when the program has a folder worth measuring. <paramref name="onlyMissing"/> skips entries that already have a size.</summary>
    public static bool ShouldCalculate(InstalledProgram program, bool onlyMissing)
    {
        ArgumentNullException.ThrowIfNull(program);
        if (string.IsNullOrWhiteSpace(program.InstallLocation) || program.Source == SoftwareSource.StoreApp)
        {
            return false;
        }

        return !onlyMissing || program.SizeBytes is null or 0;
    }

    /// <summary>Returns the programs with sizes filled in from <paramref name="sizes"/> (keyed by program id); untouched otherwise.</summary>
    public static IReadOnlyList<InstalledProgram> ApplySizes(IReadOnlyList<InstalledProgram> programs, IReadOnlyDictionary<string, long> sizes)
    {
        ArgumentNullException.ThrowIfNull(programs);
        ArgumentNullException.ThrowIfNull(sizes);
        return programs.Select(p => sizes.TryGetValue(p.Id, out var size) ? p with { SizeBytes = size } : p).ToList();
    }

    /// <summary>Measures every eligible program's folder and returns an updated list. Folders that cannot be read keep their old size.</summary>
    public static async Task<IReadOnlyList<InstalledProgram>> CalculateAsync(IReadOnlyList<InstalledProgram> programs, bool onlyMissing,
        IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken, int parallelism = DefaultParallelism)
    {
        ArgumentNullException.ThrowIfNull(programs);
        var targets = programs.Where(p => ShouldCalculate(p, onlyMissing)).ToList();
        if (targets.Count == 0)
        {
            progress?.Report(new ProgressUpdate("Nothing to measure", 100));
            return programs;
        }

        var sizes = new System.Collections.Concurrent.ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        int done = 0;
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallelism), CancellationToken = cancellationToken };
        await Parallel.ForEachAsync(targets, options, (program, ct) =>
        {
            var size = TryGetSize(program.InstallLocation!, ct);
            if (size.HasValue)
            {
                sizes[program.Id] = size.Value;
            }

            int n = Interlocked.Increment(ref done);
            progress?.Report(new ProgressUpdate("Measuring install folders", 100.0 * n / targets.Count, program.Name));
            return ValueTask.CompletedTask;
        }).ConfigureAwait(false);

        return ApplySizes(programs, sizes);
    }

    /// <summary>Sums file sizes under <paramref name="path"/>; null when the folder is missing or unreadable.</summary>
    public static long? TryGetSize(string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        DirectoryInfo root;
        try
        {
            root = new DirectoryInfo(path);
            if (!root.Exists || root.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return null;
            }
        }
        catch
        {
            return null;
        }

        long total = 0;
        var stack = new Stack<DirectoryInfo>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            FileSystemInfo[] entries;
            try
            {
                entries = dir.GetFileSystemInfos();
            }
            catch
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                if (entry is DirectoryInfo sub)
                {
                    stack.Push(sub);
                }
                else if (entry is FileInfo file)
                {
                    try
                    {
                        total += file.Length;
                    }
                    catch
                    {
                        // Locked or vanished files are skipped.
                    }
                }
            }
        }

        return total;
    }
}
