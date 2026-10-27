using PcChecker.Core.Common;
using PcChecker.Core.Optimization;

namespace PcChecker.Core.Memory;

/// <summary>
/// Memory-related actions for the Optimize page. Every action delegates to <see cref="IMemoryService"/>, whose
/// Windows implementation runs each change through <c>IChangeGuard</c> (restore point, journal, log); the demo
/// implementation only updates its sample state.
/// </summary>
public sealed class MemoryActionsProvider : IOptimizationActionProvider
{
    public const string CompressionOn = "memory.compression.on";
    public const string CompressionOff = "memory.compression.off";
    public const string SysMainOn = "memory.sysmain.on";
    public const string SysMainOff = "memory.sysmain.off";
    public const string StandbyClear = "memory.standby.clear";
    public const string PageFileAuto = "memory.pagefile.auto";

    private const string Category = "Memory";

    private readonly IMemoryService _memory;
    private readonly IPageFileSnapshotStore _pageFileStore;

    public MemoryActionsProvider(IMemoryService memory, IPageFileSnapshotStore? pageFileStore = null)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _pageFileStore = pageFileStore ?? new RegistryPageFileSnapshotStore();
    }

    public IReadOnlyList<OptimizationAction> Actions { get; } =
    [
        new(CompressionOn, "Turn memory compression on", "Keeps more programs in RAM by compressing pages Windows is not using right now. Recommended on every PC; the default on Windows 10 and 11.", Category, RiskLevel.Safe, true, false, false),
        new(CompressionOff, "Turn memory compression off", "Stops Windows compressing idle memory. Only useful on machines with lots of RAM where the small CPU cost matters more than fitting more in memory.", Category, RiskLevel.Moderate, true, false, true),
        new(SysMainOn, "Turn SysMain (Superfetch) on", "Lets Windows preload the programs you use most so they open faster. The default setting.", Category, RiskLevel.Safe, true, false, false),
        new(SysMainOff, "Turn SysMain (Superfetch) off", "Stops Windows preloading programs. Sometimes helps on very old hard-drive PCs that show constant disk activity; usually makes things slower.", Category, RiskLevel.Moderate, true, false, true),
        new(StandbyClear, "Clear the standby list", "Releases cached file data that Windows keeps in RAM \"just in case\". Harmless and instant; useful when a game stutters because the cache will not let go. Windows refills it over time.", Category, RiskLevel.Safe, false, false, false),
        new(PageFileAuto, "Let Windows manage the page file", "Returns the page file to the automatic size Windows chooses. Fixes most page file problems and is the safe default.", Category, RiskLevel.Safe, true, true, false),
    ];

    public bool Handles(string actionId) => Actions.Any(a => a.Id.Equals(actionId, StringComparison.OrdinalIgnoreCase));

    public async Task<ActionState> GetStateAsync(string id, CancellationToken cancellationToken)
    {
        var overview = _memory.Last ?? await SafeOverviewAsync(cancellationToken).ConfigureAwait(false);
        if (overview is null)
        {
            return new ActionState(id, null, null);
        }

        string lower = id.ToLowerInvariant();
        if (!overview.MemoryCompressionKnown && lower is CompressionOn or CompressionOff)
        {
            // Get-MMAgent failed (for example while SysMain is disabled): do not guess.
            return new ActionState(id, null, "Unknown");
        }

        return lower switch
        {
            CompressionOn => new ActionState(id, overview.MemoryCompressionEnabled, overview.MemoryCompressionEnabled ? "On" : "Off"),
            CompressionOff => new ActionState(id, !overview.MemoryCompressionEnabled, overview.MemoryCompressionEnabled ? "On" : "Off"),
            SysMainOn => new ActionState(id, overview.SysMainEnabled, overview.SysMainEnabled ? "Running" : "Stopped"),
            SysMainOff => new ActionState(id, !overview.SysMainEnabled, overview.SysMainEnabled ? "Running" : "Stopped"),
            StandbyClear => new ActionState(id, null, ByteSize.Format(overview.StandbyBytes) + " on standby"),
            PageFileAuto => new ActionState(id, overview.AutomaticPageFile, overview.AutomaticPageFile ? "Automatic" : "Custom size"),
            _ => new ActionState(id, null, null),
        };
    }

    public Task<OperationResult> ApplyAsync(string id, CancellationToken cancellationToken) => id.ToLowerInvariant() switch
    {
        CompressionOn => _memory.SetMemoryCompressionAsync(true, cancellationToken),
        CompressionOff => _memory.SetMemoryCompressionAsync(false, cancellationToken),
        SysMainOn => SetSysMainAsync(true, cancellationToken),
        SysMainOff => SetSysMainAsync(false, cancellationToken),
        StandbyClear => _memory.ClearStandbyListAsync(cancellationToken),
        PageFileAuto => ApplyAutomaticPageFileAsync(cancellationToken),
        _ => Task.FromResult(OperationResult.Fail($"Unknown memory action \"{id}\".")),
    };

    public Task<OperationResult> RevertAsync(string id, CancellationToken cancellationToken) => id.ToLowerInvariant() switch
    {
        CompressionOn => _memory.SetMemoryCompressionAsync(false, cancellationToken),
        CompressionOff => _memory.SetMemoryCompressionAsync(true, cancellationToken),
        SysMainOn => SetSysMainAsync(false, cancellationToken),
        SysMainOff => SetSysMainAsync(true, cancellationToken),
        StandbyClear => Task.FromResult(OperationResult.Ok("Nothing to undo: Windows refills the standby list on its own.")),
        PageFileAuto => RestorePreviousPageFilesAsync(cancellationToken),
        _ => Task.FromResult(OperationResult.Fail($"Unknown memory action \"{id}\".")),
    };

    private Task<OperationResult> SetSysMainAsync(bool enabled, CancellationToken cancellationToken) =>
        _memory is IMemoryTuning tuning
            ? tuning.SetSysMainAsync(enabled, cancellationToken)
            : Task.FromResult(OperationResult.Fail("SysMain cannot be changed with this memory service."));

    /// <summary>
    /// Remembers the page file configuration (automatic or not, and every page file with its sizes) before the first
    /// change, read fresh from Windows, so Revert can put back exactly that.
    /// </summary>
    private async Task<OperationResult> ApplyAutomaticPageFileAsync(CancellationToken cancellationToken)
    {
        bool remembered = false;
        if (PageFileSnapshot.Parse(_pageFileStore.Get()) is null && await SafeOverviewAsync(cancellationToken).ConfigureAwait(false) is { } current)
        {
            _pageFileStore.Set(PageFileSnapshot.From(current).Format());
            remembered = true;
        }

        var result = await _memory.SetPageFileAsync(SystemDrive(), true, null, null, cancellationToken).ConfigureAwait(false);
        if (!result.Success && remembered)
        {
            _pageFileStore.Remove();
        }

        return result;
    }

    /// <summary>
    /// Restores the remembered configuration. Without one nothing is changed: guessing a size would write a fixed page
    /// file of up to 32 GB to the Windows drive that the user never had.
    /// </summary>
    private async Task<OperationResult> RestorePreviousPageFilesAsync(CancellationToken cancellationToken)
    {
        var snapshot = PageFileSnapshot.Parse(_pageFileStore.Get());
        if (snapshot is null)
        {
            return OperationResult.Fail("The page file settings from before were not recorded, so there is nothing to restore and Windows keeps managing the page file. To use a custom size, set it on the Memory page or in System Properties → Performance → Advanced → Virtual memory.");
        }

        if (_memory is not IMemoryTuning tuning)
        {
            return OperationResult.Fail("The page file cannot be restored with this memory service.");
        }

        var result = await tuning.RestorePageFilesAsync(snapshot, cancellationToken).ConfigureAwait(false);
        if (result.Success)
        {
            _pageFileStore.Remove();
        }

        return result;
    }

    private async Task<MemoryOverview?> SafeOverviewAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _memory.GetOverviewAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private string SystemDrive()
    {
        var existing = _memory.Last?.PageFiles ?? [];
        string system = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
        return PageFileRules.ChooseDrive(existing, system);
    }
}
