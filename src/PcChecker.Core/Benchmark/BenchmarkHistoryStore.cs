using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PcChecker.Core.Settings;

namespace PcChecker.Core.Benchmark;

/// <summary>Every benchmark run, kept on this PC so runs can be compared later. Lists are newest first.</summary>
public interface IBenchmarkHistoryStore
{
    /// <summary>Where the history is kept (a file path, or a description in demo mode).</summary>
    string Location { get; }

    Task<IReadOnlyList<BenchmarkRun>> LoadAsync(CancellationToken cancellationToken);

    /// <summary>Saves <paramref name="run"/> and returns the updated history.</summary>
    Task<IReadOnlyList<BenchmarkRun>> AddAsync(BenchmarkRun run, CancellationToken cancellationToken);

    /// <summary>Removes the run with <paramref name="id"/> from the history and returns the updated history.</summary>
    Task<IReadOnlyList<BenchmarkRun>> DeleteAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Adds the runs the history does not have yet (matched by id), for a setup import, and returns the ids it added.
    /// Raises <see cref="Changed"/>.
    /// </summary>
    Task<IReadOnlyList<Guid>> MergeAsync(IEnumerable<BenchmarkRun> runs, CancellationToken cancellationToken);

    /// <summary>Removes the runs with these ids (undoing a setup import) and returns the updated history. Raises <see cref="Changed"/>.</summary>
    Task<IReadOnlyList<BenchmarkRun>> RemoveAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>Raised after <see cref="MergeAsync"/> or <see cref="RemoveAsync"/> with the new history, so an open Benchmark page can reload.</summary>
    event EventHandler<IReadOnlyList<BenchmarkRun>>? Changed;
}

/// <summary>
/// Keeps the history as JSON in <c>benchmarks.json</c> next to the settings file (so demo mode never touches the real
/// history), newest <see cref="BenchmarkHistoryRules.MaxRuns"/> runs only. An unreadable file is renamed to
/// <c>benchmarks.json.bad</c> and the history starts again rather than failing the page.
/// </summary>
public sealed class JsonBenchmarkHistoryStore : IBenchmarkHistoryStore
{
    public const string FileName = "benchmarks.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger _logger;

    public JsonBenchmarkHistoryStore(ISettingsService settings, ILogger<JsonBenchmarkHistoryStore> logger)
        : this(Path.Combine(settings.SettingsDirectory, FileName), logger)
    {
    }

    public JsonBenchmarkHistoryStore(string path, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Location = path;
        _logger = logger ?? NullLogger.Instance;
    }

    public string Location { get; }

    public event EventHandler<IReadOnlyList<BenchmarkRun>>? Changed;

    public async Task<IReadOnlyList<BenchmarkRun>> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadAsync(throwOnIoError: false, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<BenchmarkRun>> AddAsync(BenchmarkRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var runs = BenchmarkHistoryRules.Normalize((await ReadAsync(throwOnIoError: true, cancellationToken).ConfigureAwait(false)).Append(run));
            await WriteAsync(runs).ConfigureAwait(false);
            return runs;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<BenchmarkRun>> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var runs = (await ReadAsync(throwOnIoError: true, cancellationToken).ConfigureAwait(false)).Where(r => r.Id != id).ToList();
            await WriteAsync(runs).ConfigureAwait(false);
            return runs;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<Guid>> MergeAsync(IEnumerable<BenchmarkRun> runs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runs);
        IReadOnlyList<BenchmarkRun> saved;
        List<Guid> added;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await ReadAsync(throwOnIoError: true, cancellationToken).ConfigureAwait(false);
            var known = existing.Select(r => r.Id).ToHashSet();
            var incoming = runs.Where(r => IsUsable(r) && !known.Contains(r.Id)).ToList();
            saved = BenchmarkHistoryRules.Normalize(existing.Concat(incoming));
            added = incoming.Select(r => r.Id).Where(id => saved.Any(r => r.Id == id)).Distinct().ToList();
            if (added.Count > 0)
            {
                await WriteAsync(saved).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }

        if (added.Count > 0)
        {
            Changed?.Invoke(this, saved);
        }

        return added;
    }

    public async Task<IReadOnlyList<BenchmarkRun>> RemoveAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        IReadOnlyList<BenchmarkRun> runs;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            runs = (await ReadAsync(throwOnIoError: true, cancellationToken).ConfigureAwait(false)).Where(r => !ids.Contains(r.Id)).ToList();
            await WriteAsync(runs).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, runs);
        return runs;
    }

    /// <summary>
    /// Reads the history. When the file exists but cannot be opened (for example a scanner holds it for a moment),
    /// <paramref name="throwOnIoError"/> decides between an empty list (fine for display) and an
    /// <see cref="IOException"/> (needed before a write, so the saved history is never overwritten with less).
    /// </summary>
    private async Task<IReadOnlyList<BenchmarkRun>> ReadAsync(bool throwOnIoError, CancellationToken cancellationToken)
    {
        if (!File.Exists(Location))
        {
            return [];
        }

        try
        {
            await using var stream = File.OpenRead(Location);
            var file = await JsonSerializer.DeserializeAsync<BenchmarkHistoryFile>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            return BenchmarkHistoryRules.Normalize((file?.Runs ?? []).Where(IsUsable));
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            _logger.LogWarning(ex, "The benchmark history {Path} could not be read; it was set aside and a new history started.", Location);
            SetAside();
            return [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The benchmark history {Path} could not be opened.", Location);
            if (throwOnIoError)
            {
                throw new IOException("The benchmark history could not be opened: " + ex.Message, ex);
            }

            return [];
        }
    }

    private async Task WriteAsync(IReadOnlyList<BenchmarkRun> runs)
    {
        try
        {
            var dir = Path.GetDirectoryName(Location);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var temp = Location + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(new BenchmarkHistoryFile { Runs = runs.ToList() }, JsonOptions)).ConfigureAwait(false);
            File.Move(temp, Location, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The benchmark history could not be saved to {Path}.", Location);
            throw new IOException("The benchmark history could not be saved: " + ex.Message, ex);
        }
    }

    private void SetAside()
    {
        try
        {
            File.Move(Location, Location + ".bad", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not rename the unreadable benchmark history {Path}.", Location);
        }
    }

    private static bool IsUsable(BenchmarkRun? run) =>
        run is { Tests: not null, Warnings: not null, System: not null } && run.Id != Guid.Empty && run.Tests.All(t => t is { Metrics: not null });

    private sealed class BenchmarkHistoryFile
    {
        public int Version { get; set; } = 1;
        public List<BenchmarkRun> Runs { get; set; } = [];
    }
}

/// <summary>Demo mode: three sample runs (before and after Optimize, after a GPU overclock), kept in memory only.</summary>
public sealed class DemoBenchmarkHistoryStore : IBenchmarkHistoryStore
{
    private readonly object _lock = new();
    private IReadOnlyList<BenchmarkRun> _runs = DemoBenchmarkData.SampleRuns(DateTimeOffset.Now);

    public string Location => "Demo mode: sample runs, not saved";

    public event EventHandler<IReadOnlyList<BenchmarkRun>>? Changed;

    public async Task<IReadOnlyList<BenchmarkRun>> LoadAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(60, cancellationToken).ConfigureAwait(false);
        lock (_lock)
        {
            return _runs;
        }
    }

    public Task<IReadOnlyList<BenchmarkRun>> AddAsync(BenchmarkRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        lock (_lock)
        {
            _runs = BenchmarkHistoryRules.Normalize(_runs.Append(run));
            return Task.FromResult(_runs);
        }
    }

    public Task<IReadOnlyList<BenchmarkRun>> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _runs = _runs.Where(r => r.Id != id).ToList();
            return Task.FromResult(_runs);
        }
    }

    public Task<IReadOnlyList<Guid>> MergeAsync(IEnumerable<BenchmarkRun> runs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runs);
        IReadOnlyList<BenchmarkRun> saved;
        List<Guid> added;
        lock (_lock)
        {
            var known = _runs.Select(r => r.Id).ToHashSet();
            var incoming = runs.Where(r => !known.Contains(r.Id)).ToList();
            _runs = saved = BenchmarkHistoryRules.Normalize(_runs.Concat(incoming));
            added = incoming.Select(r => r.Id).Where(id => saved.Any(r => r.Id == id)).Distinct().ToList();
        }

        if (added.Count > 0)
        {
            Changed?.Invoke(this, saved);
        }

        return Task.FromResult<IReadOnlyList<Guid>>(added);
    }

    public Task<IReadOnlyList<BenchmarkRun>> RemoveAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        IReadOnlyList<BenchmarkRun> saved;
        lock (_lock)
        {
            _runs = saved = _runs.Where(r => !ids.Contains(r.Id)).ToList();
        }

        Changed?.Invoke(this, saved);
        return Task.FromResult(saved);
    }
}
