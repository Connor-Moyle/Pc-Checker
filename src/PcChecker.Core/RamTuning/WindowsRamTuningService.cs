using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Drivers.Platform;
using PcChecker.Core.Localization;
using PcChecker.Core.Memory;
using PcChecker.Core.Sensors;
using PcChecker.Core.Settings;

namespace PcChecker.Core.RamTuning;

/// <summary>
/// Windows implementation of <see cref="IRamTuningService"/>. Reads the modules and slots through the Memory page's
/// WMI helpers, the board and BIOS through the Drivers page's platform service (offline: nothing is downloaded), and
/// DIMM temperatures from the shared sensor service. Read-only: it changes no setting.
/// </summary>
public sealed class WindowsRamTuningService : IRamTuningService
{
    private readonly IPcPlatformService _platform;
    private readonly ISensorService _sensors;
    private readonly ILogger<WindowsRamTuningService> _logger;
    private readonly string _journalPath;

    public WindowsRamTuningService(IPcPlatformService platform, ISensorService sensors, ISettingsService settings, ILogger<WindowsRamTuningService> logger)
    {
        _platform = platform;
        _sensors = sensors;
        _logger = logger;
        _journalPath = Path.Combine(settings.SettingsDirectory, RamTuningJournalFile.FileName);
    }

    public RamFacts? Last { get; private set; }

    public async Task<RamFacts> DetectAsync(Translator? tr, CancellationToken cancellationToken)
    {
        tr ??= Translator.English;
        if (!PlatformGuard.IsWindows)
        {
            Last = new RamFacts { Warnings = [tr.T("Memory details are only available on Windows.")] };
            return Last;
        }

        var (modules, slots, cpu) = await Task.Run(() =>
        {
            var list = MemoryWmi.GetPhysicalMemory();
            int slotCount = MemoryWmi.GetSlotCount();
            string? name = MemoryWmi.Query("SELECT Name FROM Win32_Processor").Select(r => r.TryGetValue("Name", out var v) ? v?.ToString()?.Trim() : null)
                .FirstOrDefault(n => !string.IsNullOrEmpty(n));
            return (list, slotCount, name);
        }, cancellationToken).ConfigureAwait(false);

        PcPlatformInfo? info = _platform.Last?.Info;
        if (info is null)
        {
            try
            {
                info = (await _platform.CheckAsync(online: false, forceRefresh: false, null, cancellationToken).ConfigureAwait(false)).Info;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Board and BIOS information could not be read");
            }
        }

        IEnumerable<SensorReading>? readings = null;
        try
        {
            readings = _sensors.Latest?.Readings;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Sensor readings unavailable");
        }

        var facts = RamFactsBuilder.Build(modules, slots, cpu, info, readings, DateTimeOffset.Now, tr);
        if (info is null)
        {
            facts = facts with { Warnings = [.. facts.Warnings, tr.T("The board and BIOS could not be read, so the menu paths are general ones.")] };
        }

        Last = facts;
        _logger.LogInformation("RAM tuning: {Count} module(s), {Type}, platform {Platform}, XMP {Xmp}, {Configured}/{Rated} MT/s",
            facts.Modules.Count, facts.MemoryType ?? "unknown", facts.Platform.Kind, facts.Xmp.State, facts.ConfiguredMts, facts.RatedMts);
        return facts;
    }

    public long? GetFreeMemoryBytes() => MemoryNative.TryGetMemoryStatus(out _, out long available) ? available : null;

    public Task<QuickCheckResult> RunQuickCheckAsync(int percent, TimeSpan duration, IProgress<QuickCheckProgress>? progress, Translator? tr, CancellationToken cancellationToken)
    {
        long bytes = QuickMemoryCheck.PlanBytes(GetFreeMemoryBytes() ?? 0, percent);
        _logger.LogInformation("Quick memory check: {Bytes} bytes for {Duration}", bytes, duration);
        // Allocation and the watchdog run off the caller's (UI) thread.
        return Task.Run(() => QuickMemoryCheck.RunAsync(bytes, duration, GetFreeMemoryBytes, progress, cancellationToken, tr: tr), CancellationToken.None);
    }

    public Task<RamTuningJournal> LoadJournalAsync(CancellationToken cancellationToken) => RamTuningJournalFile.LoadAsync(_journalPath, cancellationToken);

    public async Task SaveJournalAsync(RamTuningJournal journal, CancellationToken cancellationToken)
    {
        try
        {
            await RamTuningJournalFile.SaveAsync(_journalPath, journal, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The RAM tuning history could not be saved");
        }
    }
}
