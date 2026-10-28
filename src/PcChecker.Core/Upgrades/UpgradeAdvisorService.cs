using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Displays;
using PcChecker.Core.Games;
using PcChecker.Core.Hardware;
using PcChecker.Core.History;
using PcChecker.Core.Localization;
using PcChecker.Core.Security;
using PcChecker.Core.Storage;

namespace PcChecker.Core.Upgrades;

/// <summary>The Upgrade advice page's source: gathers the facts from the other modules and builds the report. Read only.</summary>
public interface IUpgradeAdvisorService
{
    /// <summary>The last report, or null before the first check.</summary>
    UpgradeReport? Last { get; }

    /// <summary>The facts behind <see cref="Last"/>.</summary>
    UpgradeFacts? LastFacts { get; }

    /// <summary>Reads hardware, drives, games, the last 30 days of History, Security and Displays, then builds the advice.</summary>
    Task<UpgradeReport> AnalyzeAsync(Translator tr, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);
}

/// <summary>
/// Gathers the advisor's facts through the other modules' interfaces only, so the same class serves both modes: in
/// demo mode those are the Demo* services (and <see cref="DemoPcieLinkReader"/>), on a real PC the Windows ones.
/// Every source is read in parallel; one that fails becomes a warning and the rest of the advice still appears.
/// Nothing on the PC is changed.
/// </summary>
public sealed class UpgradeAdvisorService : IUpgradeAdvisorService
{
    /// <summary>How much History the gaming balance and memory pressure look at.</summary>
    public static readonly TimeSpan HistoryWindow = TimeSpan.FromDays(30);

    private readonly IHardwareInventoryService _hardware;
    private readonly IDriveInventoryService _drives;
    private readonly IGameLibraryService _games;
    private readonly IHistoryService _history;
    private readonly ISecurityStatusService _security;
    private readonly IDisplayService _displays;
    private readonly IPcieLinkReader _pcie;
    private readonly ILogger<UpgradeAdvisorService> _logger;

    public UpgradeAdvisorService(IHardwareInventoryService hardware, IDriveInventoryService drives, IGameLibraryService games,
        IHistoryService history, ISecurityStatusService security, IDisplayService displays, IPcieLinkReader pcie,
        ILogger<UpgradeAdvisorService> logger)
    {
        _hardware = hardware;
        _drives = drives;
        _games = games;
        _history = history;
        _security = security;
        _displays = displays;
        _pcie = pcie;
        _logger = logger;
    }

    public UpgradeReport? Last { get; private set; }

    public UpgradeFacts? LastFacts { get; private set; }

    public async Task<UpgradeReport> AnalyzeAsync(Translator tr, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tr);
        var warnings = new List<string>();
        var now = DateTimeOffset.Now;
        progress?.Report(new ProgressUpdate(tr.T("Reading your hardware"), 10));

        var hardwareTask = Read(() => _hardware.Last is { } last ? Task.FromResult(last) : _hardware.ScanAsync(null, cancellationToken),
            tr.T("The hardware could not be read."), warnings);
        var drivesTask = Read(() => _drives.GetDrivesAsync(DriveReadMode.Routine, cancellationToken), tr.T("The drives could not be read."), warnings);
        var gamesTask = Read(() => _games.ScanAsync(null, cancellationToken), tr.T("The game library could not be read."), warnings);
        var historyTask = Read(() => _history.LoadAsync(now - HistoryWindow, now, cancellationToken), tr.T("The History could not be read."), warnings);
        var securityTask = Read(() => _security.ReadAsync(cancellationToken), tr.T("The security status could not be read."), warnings);
        var displaysTask = Read(() => _displays.GetDisplaysAsync(cancellationToken), tr.T("The displays could not be read."), warnings);

        var hardware = await hardwareTask.ConfigureAwait(false);
        progress?.Report(new ProgressUpdate(tr.T("Reading drives, games and History"), 45));
        var gpu = hardware is null ? null : HardwareSummary.PrimaryGpu(hardware);
        var link = gpu is null || gpu.IsIntegrated ? null
            : await Read(() => _pcie.ReadAsync(gpu.PnpDeviceId, cancellationToken), null, warnings).ConfigureAwait(false);

        var drives = await drivesTask.ConfigureAwait(false);
        var games = await gamesTask.ConfigureAwait(false);
        var history = await historyTask.ConfigureAwait(false);
        var security = await securityTask.ConfigureAwait(false);
        var displays = await displaysTask.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new ProgressUpdate(tr.T("Working out the advice"), 90));

        var facts = new UpgradeFacts
        {
            Now = now,
            Hardware = hardware,
            Drives = drives ?? [],
            Games = games?.Games ?? [],
            History = history?.Samples ?? [],
            HistoryRecording = _history.IsRecording,
            Security = security,
            Displays = displays ?? [],
            GpuLink = link,
            Warnings = warnings,
        };
        var report = UpgradeAdvisor.Build(facts, tr);
        LastFacts = facts;
        Last = report;
        return report;
    }

    private async Task<T?> Read<T>(Func<Task<T>> read, string? warning, List<string> warnings)
    {
        try
        {
            return await read().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Upgrade advice: a source could not be read.");
            if (warning is not null)
            {
                lock (warnings)
                {
                    warnings.Add(warning);
                }
            }

            return default;
        }
    }
}
