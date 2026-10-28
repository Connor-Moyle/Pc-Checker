using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Displays;
using PcChecker.Core.Games;
using PcChecker.Core.Hardware;
using PcChecker.Core.Memory;
using PcChecker.Core.Optimization;
using PcChecker.Core.Processes;
using PcChecker.Core.Security;
using PcChecker.Core.Storage;

namespace PcChecker.Core.GameReady;

/// <summary>
/// Default <see cref="IGameReadyService"/>. It owns almost no system access: it reads through the other modules'
/// interfaces (so demo mode, with their Demo implementations, and normal mode share it) and through
/// <see cref="IGameReadySystem"/> for the rest.
/// </summary>
public sealed class GameReadyService : IGameReadyService
{
    /// <summary>A part that takes longer than this is skipped and listed as a warning.</summary>
    public static readonly TimeSpan PartTimeout = TimeSpan.FromSeconds(90);

    private const int Parts = 9;

    private readonly IDisplayService _displays;
    private readonly IOptimizationService _optimization;
    private readonly IMemoryService _memory;
    private readonly IHardwareInventoryService _hardware;
    private readonly IGameLibraryService _games;
    private readonly IDriveInventoryService _drives;
    private readonly IProcessService _processes;
    private readonly ISecurityStatusService _security;
    private readonly IGameReadySystem _system;
    private readonly ILogger<GameReadyService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public GameReadyService(IDisplayService displays, IOptimizationService optimization, IMemoryService memory, IHardwareInventoryService hardware,
        IGameLibraryService games, IDriveInventoryService drives, IProcessService processes, ISecurityStatusService security, IGameReadySystem system,
        ILogger<GameReadyService> logger)
    {
        _displays = displays;
        _optimization = optimization;
        _memory = memory;
        _hardware = hardware;
        _games = games;
        _drives = drives;
        _processes = processes;
        _security = security;
        _system = system;
        _logger = logger;
    }

    /// <summary>Time between the two process samples (disk rates need two readings).</summary>
    public TimeSpan ProcessSampleGap { get; set; } = TimeSpan.FromSeconds(1);

    public GameReadyFacts? LastFacts { get; private set; }

    public async Task<GameReadyFacts> CheckAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var warnings = new List<string>();
            int done = 0;
            void Step(string what)
            {
                int now = Interlocked.Increment(ref done);
                progress?.Report(new ProgressUpdate(what, Math.Min(100, now * 100.0 / Parts)));
            }

            progress?.Report(new ProgressUpdate("Reading monitors, settings and hardware", 0));
            var displays = Part("Monitors", ct => _displays.GetDisplaysAsync(ct), warnings, Step, cancellationToken);
            var states = Part("Windows settings", ReadStatesAsync, warnings, Step, cancellationToken);
            var inventory = Part("Hardware", async ct => _hardware.Last ?? await _hardware.ScanAsync(null, ct).ConfigureAwait(false), warnings, Step, cancellationToken);
            var memory = Part("Memory", async ct => _memory.Last ?? await _memory.GetOverviewAsync(ct).ConfigureAwait(false), warnings, Step, cancellationToken);
            var library = Part("Games", ct => _games.ScanAsync(null, ct), warnings, Step, cancellationToken);
            var drives = Part("Drives", async ct => _drives.Last.Count > 0 ? _drives.Last : await _drives.GetDrivesAsync(DriveReadMode.Routine, ct).ConfigureAwait(false),
                warnings, Step, cancellationToken);
            var running = Part("Running programs", SampleProcessesAsync, warnings, Step, cancellationToken);
            var security = Part("Security", ct => _security.ReadAsync(ct), warnings, Step, cancellationToken);
            await Task.WhenAll(displays, states, inventory, memory, library, drives, running, security).ConfigureAwait(false);

            var gpus = (inventory.Result?.Graphics ?? []).Where(g => !GameReadyFactsBuilder.IsVirtualAdapter(g.Name)).ToList();
            var gameList = library.Result?.Games ?? [];
            var system = await Part("Graphics and power",
                ct => _system.ReadAsync(gpus.Where(g => !g.IsIntegrated && g.PnpDeviceId is not null).Select(g => g.PnpDeviceId!).ToList(),
                    GameReadyFactsBuilder.ProgramPaths(gameList), ct), warnings, Step, cancellationToken).ConfigureAwait(false);
            warnings.AddRange(system?.Warnings ?? []);

            var stateMap = states.Result ?? new Dictionary<string, ActionState>();
            var facts = new GameReadyFacts
            {
                Displays = GameReadyFactsBuilder.Displays(displays.Result ?? []),
                DirectXGlobalSettings = system?.DirectXGlobalSettings,
                DirectXReadable = system?.DirectXReadable ?? false,
                HagsOn = Applied(stateMap, GameReadyRules.HagsAction),
                GameModeOn = Applied(stateMap, GameReadyRules.GameModeAction),
                GameDvrOn = Applied(stateMap, GameReadyRules.GameBarAction) is { } barOff ? !barOff : null,
                PointerPrecisionOn = Applied(stateMap, GameReadyRules.PointerPrecisionAction) is { } accelOff ? !accelOff : null,
                HighPerformancePlan = Applied(stateMap, GameReadyRules.HighPerformanceAction) is true || Applied(stateMap, "power.ultimate") is true
                    ? true
                    : Applied(stateMap, GameReadyRules.HighPerformanceAction),
                PowerPlanText = stateMap.TryGetValue(GameReadyRules.HighPerformanceAction, out var plan) ? plan.CurrentValue : null,
                HasBattery = system?.HasBattery ?? (inventory.Result?.Batteries.Count > 0),
                OnBattery = system?.OnBattery,
                BatterySaverOn = system?.BatterySaverOn,
                PowerMode = system?.PowerMode ?? PowerModeOverlay.Unknown,
                Gpus = GameReadyFactsBuilder.Gpus(gpus, system?.Links ?? new Dictionary<string, PcieLink>(), system?.ResizableBar ?? new Dictionary<string, ResizableBarState>()),
                Memory = memory.Result is { TotalBytes: > 0 } overview ? GameReadyFactsBuilder.Memory(overview) : null,
                Games = GameReadyFactsBuilder.Games(gameList, system?.GpuPreferences ?? new Dictionary<string, string>()),
                GameDrives = GameReadyFactsBuilder.Drives(gameList, drives.Result ?? [], inventory.Result?.LogicalDrives ?? []),
                Apps = running.Result ?? GameReadyApps.None,
                MemoryIntegrityOn = security.Result?.MemoryIntegrity,
                PendingRestartReason = security.Result?.PendingRestartReason,
                MousePollingHz = LastFacts?.MousePollingHz,
                Now = DateTimeOffset.Now,
                Warnings = warnings,
            };
            LastFacts = facts;
            _logger.LogInformation("Game ready check finished with {Warnings} warning(s).", warnings.Count);
            return facts;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Remembers a mouse polling measurement so the next check keeps it.</summary>
    public void RememberPolling(int hz)
    {
        if (LastFacts is { } facts)
        {
            LastFacts = facts with { MousePollingHz = hz };
        }
    }

    public async Task<GameReadyChange> ApplyAsync(GameReadyFix fix, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fix);
        try
        {
            return fix.Kind switch
            {
                GameReadyFixKind.DirectXSetting when IsKnownGlobalSwitch(fix.Target) => await TurnOnGlobalSwitchAsync(fix.Target!, cancellationToken).ConfigureAwait(false),
                GameReadyFixKind.GpuPreference when fix.Paths.Count > 0 => await UseGraphicsCardAsync(fix.Paths, cancellationToken).ConfigureAwait(false),
                _ => new GameReadyChange(OperationResult.Fail("This fix is applied by the page that owns it."), null),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Game ready fix {Kind} failed.", fix.Kind);
            return new GameReadyChange(OperationResult.FromException(ex), null);
        }
    }

    /// <summary>Only the two documented switches are ever written.</summary>
    public static bool IsKnownGlobalSwitch(string? name) =>
        string.Equals(name, DirectXSettingsText.WindowedOptimizations, StringComparison.Ordinal)
        || string.Equals(name, DirectXSettingsText.VariableRefreshRate, StringComparison.Ordinal);

    private async Task<GameReadyChange> TurnOnGlobalSwitchAsync(string name, CancellationToken cancellationToken)
    {
        string? before = await _system.ReadGlobalSettingsAsync(cancellationToken).ConfigureAwait(false);
        string after = DirectXSettingsText.Set(before, name, "1");
        string what = name == DirectXSettingsText.WindowedOptimizations ? "Optimizations for windowed games" : "Variable refresh rate";
        if (DirectXSettingsText.Switch(before, name) == true)
        {
            return new GameReadyChange(OperationResult.Ok($"{what} was already on."), null);
        }

        var result = await _system.WriteGlobalSettingsAsync(after, $"Turn on {what}", cancellationToken).ConfigureAwait(false);
        return new GameReadyChange(
            result.Success ? OperationResult.Ok($"{what} is on. Games started from now on use it.") : result,
            result.Success ? ct => _system.WriteGlobalSettingsAsync(before, $"Undo: turn on {what}", ct) : null);
    }

    private async Task<GameReadyChange> UseGraphicsCardAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        var before = await _system.ReadGpuPreferencesAsync(paths, cancellationToken).ConfigureAwait(false);
        var beforeByPath = new Dictionary<string, string>(before, StringComparer.OrdinalIgnoreCase);
        var after = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var undo = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            beforeByPath.TryGetValue(path, out var raw);
            after[path] = DirectXSettingsText.WithGpuPreference(raw, DirectXSettingsText.GpuHighPerformance);
            undo[path] = raw;
        }

        string description = paths.Count == 1
            ? $"Set graphics preference of {Path.GetFileName(paths[0])} to High performance"
            : $"Set graphics preference of {paths.Count} games to High performance";
        var result = await _system.WriteGpuPreferencesAsync(after, description, cancellationToken).ConfigureAwait(false);
        return new GameReadyChange(
            result.Success ? OperationResult.Ok(paths.Count == 1 ? "The game uses the graphics card from its next start." : $"{paths.Count} games use the graphics card from their next start.") : result,
            result.Success ? ct => _system.WriteGpuPreferencesAsync(undo, "Undo: " + description, ct) : null);
    }

    private async Task<IReadOnlyDictionary<string, ActionState>> ReadStatesAsync(CancellationToken ct)
    {
        string[] ids = [GameReadyRules.HagsAction, GameReadyRules.GameModeAction, GameReadyRules.GameBarAction, GameReadyRules.PointerPrecisionAction,
            GameReadyRules.HighPerformanceAction, "power.ultimate"];
        var known = _optimization.Actions.Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var map = new Dictionary<string, ActionState>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids.Where(known.Contains))
        {
            try
            {
                map[id] = await _optimization.GetStateAsync(id, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Could not read the state of {Action}.", id);
            }
        }

        return map;
    }

    private async Task<GameReadyApps> SampleProcessesAsync(CancellationToken ct)
    {
        await _processes.SampleAsync(ct).ConfigureAwait(false);
        await Task.Delay(ProcessSampleGap, ct).ConfigureAwait(false);
        var snapshot = await _processes.SampleAsync(ct).ConfigureAwait(false);
        return BackgroundAppCatalog.Classify(snapshot.Processes.Select(p => new RunningProcess(p.Name, p.PrivateBytes, p.DiskBytesPerSecond)));
    }

    private static bool? Applied(IReadOnlyDictionary<string, ActionState> states, string id) =>
        states.TryGetValue(id, out var state) ? state.IsApplied : null;

    private async Task<T?> Part<T>(string name, Func<CancellationToken, Task<T>> read, List<string> warnings, Action<string> step, CancellationToken cancellationToken)
        where T : class
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PartTimeout);
        try
        {
            return await Task.Run(() => read(timeout.Token), timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            lock (warnings)
            {
                warnings.Add($"{name}: took too long and was skipped.");
            }

            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Game ready part {Part} failed.", name);
            lock (warnings)
            {
                warnings.Add($"{name}: could not be read ({ex.Message}).");
            }

            return null;
        }
        finally
        {
            step(name);
        }
    }
}
