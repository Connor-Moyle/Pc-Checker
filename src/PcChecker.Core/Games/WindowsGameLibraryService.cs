using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using PcChecker.Core.Common;
using PcChecker.Core.Restore;
using PcChecker.Core.Software;

namespace PcChecker.Core.Games;

/// <summary>
/// Finds installed games from each launcher's own files and registry entries, on a background thread. No launcher is
/// started and no game file is changed while looking. Each launcher is read on its own: a problem with one becomes a
/// warning and the others still count. The launcher readers live in the other parts of this class.
/// </summary>
public sealed partial class WindowsGameLibraryService : IGameLibraryService
{
    private const string Module = "Games";
    private const int MaxExeFiles = 400;
    private const int MaxExeDepth = 3;

    private readonly IChangeGuard _guard;
    private readonly ILogger<WindowsGameLibraryService> _logger;
    private readonly object _gate = new();
    private IReadOnlyList<string> _steamLibraries = [];

    public WindowsGameLibraryService(IChangeGuard guard, ILogger<WindowsGameLibraryService> logger)
    {
        _guard = guard;
        _logger = logger;
    }

    public Task<GameLibraryScan> ScanAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken) =>
        Task.Run(() => ScanCore(progress, cancellationToken), cancellationToken);

    private GameLibraryScan ScanCore(IProgress<ProgressUpdate>? progress, CancellationToken ct)
    {
        var warnings = new List<string>();
        var games = new List<InstalledGame>();
        var launchers = new List<LauncherSummary>();
        if (!PlatformGuard.IsWindows)
        {
            return new GameLibraryScan([], [], ["Games can only be found on Windows."], DateTimeOffset.Now);
        }

        var uninstall = ReadUninstallEntries(warnings, ct);
        var readers = new (GameLauncher Launcher, string Stage, Func<IReadOnlyList<UninstallEntry>, CancellationToken, LauncherResult> Read)[]
        {
            (GameLauncher.Steam, "Reading Steam libraries", (_, c) => ReadSteam(c)),
            (GameLauncher.Epic, "Reading Epic Games", (_, c) => ReadEpic(c)),
            (GameLauncher.Gog, "Reading GOG games", ReadGog),
            (GameLauncher.Ea, "Reading EA app games", ReadEa),
            (GameLauncher.Ubisoft, "Reading Ubisoft Connect games", ReadUbisoft),
            (GameLauncher.BattleNet, "Reading Battle.net games", ReadBattleNet),
            (GameLauncher.Riot, "Reading Riot games", ReadRiot),
            (GameLauncher.Xbox, "Reading Xbox and Store games", (_, c) => ReadXbox(c)),
        };

        for (int i = 0; i < readers.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (launcher, stage, read) = readers[i];
            progress?.Report(new ProgressUpdate(stage, 100.0 * i / readers.Length));
            try
            {
                var result = read(uninstall, ct);
                var found = result.Games.Select(g => FillExe(g, ct)).ToList();
                games.AddRange(found);
                launchers.Add(new LauncherSummary(launcher, result.IsInstalled, found.Count, result.Note));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Reading {Launcher} games failed.", launcher);
                warnings.Add($"{GameLibraryMath.LauncherName(launcher)} games could not be read: {ex.Message}");
                launchers.Add(new LauncherSummary(launcher, false, 0, "Could not be read."));
            }
        }

        progress?.Report(new ProgressUpdate("Done", 100));
        var merged = GameLibraryMath.MergeDuplicates(games);
        return new GameLibraryScan(merged, launchers, warnings, DateTimeOffset.Now);
    }

    public Task<IReadOnlyList<InstalledGame>> MeasureMissingSizesAsync(IReadOnlyList<InstalledGame> games, IProgress<ProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(games);
        return Task.Run(async () =>
        {
            var targets = games.Where(g => g.SizeBytes is null && !string.IsNullOrWhiteSpace(g.InstallPath)).ToList();
            if (targets.Count == 0)
            {
                return games;
            }

            var sizes = new System.Collections.Concurrent.ConcurrentDictionary<string, long>(StringComparer.Ordinal);
            int done = 0;
            var options = new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = cancellationToken };
            await Parallel.ForEachAsync(targets, options, (game, ct) =>
            {
                if (DirectorySizeCalculator.TryGetSize(game.InstallPath!, ct) is { } size)
                {
                    sizes[game.Id] = size;
                }

                int n = Interlocked.Increment(ref done);
                progress?.Report(new ProgressUpdate("Measuring game folders", 100.0 * n / targets.Count, game.Name));
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);

            return (IReadOnlyList<InstalledGame>)games.Select(g => sizes.TryGetValue(g.Id, out var s) ? g with { SizeBytes = s } : g).ToList();
        }, cancellationToken);
    }

    /// <summary>What one launcher reader found.</summary>
    private sealed record LauncherResult(bool IsInstalled, IReadOnlyList<InstalledGame> Games, string? Note = null)
    {
        public static LauncherResult NotInstalled { get; } = new(false, []);
    }

    /// <summary>Fills in the main program (when the launcher did not name one) so the card can show its icon.</summary>
    private InstalledGame FillExe(InstalledGame game, CancellationToken ct)
    {
        if ((game.ExePath is not null && SafeFileExists(game.ExePath)) || string.IsNullOrWhiteSpace(game.InstallPath))
        {
            return game;
        }

        var candidates = FindExeCandidates(game.InstallPath, ct);
        var pick = GameExeGuesser.Pick(game.Name, GameLibraryMath.LastSegment(game.InstallPath), candidates);
        return game with { ExePath = pick is null ? null : GameLibraryMath.JoinPath(game.InstallPath, pick) };
    }

    /// <summary>Program files in the top few levels of a game folder; links are not followed.</summary>
    private static List<ExeCandidate> FindExeCandidates(string folder, CancellationToken ct)
    {
        var result = new List<ExeCandidate>();
        var root = new DirectoryInfo(folder);
        if (!SafeDirectoryExists(folder))
        {
            return result;
        }

        var queue = new Queue<(DirectoryInfo Dir, int Depth, string Relative)>();
        queue.Enqueue((root, 0, string.Empty));
        while (queue.Count > 0 && result.Count < MaxExeFiles)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, depth, relative) = queue.Dequeue();
            FileSystemInfo[] entries;
            try
            {
                entries = dir.GetFileSystemInfos();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                var rel = relative.Length == 0 ? entry.Name : relative + "\\" + entry.Name;
                if (entry is DirectoryInfo sub && depth < MaxExeDepth)
                {
                    queue.Enqueue((sub, depth + 1, rel));
                }
                else if (entry is FileInfo file && file.Extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(new ExeCandidate(rel, SafeLength(file)));
                }
            }
        }

        return result;
    }

    private static IReadOnlyList<UninstallEntry> ReadUninstallEntries(List<string> warnings, CancellationToken ct)
    {
        var entries = new List<UninstallEntry>();
        foreach (var (hive, view, subKey, source, label) in RegistryUninstallReader.Hives)
        {
            entries.AddRange(RegistryUninstallReader.ReadHive(hive, view, subKey, source, label, false, warnings, ct));
        }

        return entries;
    }

    private static string? Value(UninstallEntry entry, string name) =>
        entry.Values.TryGetValue(name, out var v) && v is not null && !string.IsNullOrWhiteSpace(v.ToString()) ? v.ToString()!.Trim() : null;

    /// <summary>EstimatedSize is written in kilobytes; zero or nonsense means "not recorded".</summary>
    private static long? EstimatedBytes(UninstallEntry entry) =>
        entry.Values.TryGetValue("EstimatedSize", out var v) && v is int kb && kb > 0 ? kb * 1024L : null;

    private static string? ReadRegistryString(RegistryHive hive, RegistryView view, string subKey, string name)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(subKey, writable: false);
            return key?.GetValue(name) is string s && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static bool RegistryKeyExists(RegistryHive hive, RegistryView view, string subKey)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(subKey, writable: false);
            return key is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static string? ReadTextFile(string path, long maxBytes = 32 * 1024 * 1024)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length <= maxBytes ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static bool SafeFileExists(string? path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    private static bool SafeDirectoryExists(string? path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    private static long SafeLength(FileInfo file)
    {
        try
        {
            return file.Length;
        }
        catch
        {
            return 0;
        }
    }
}
