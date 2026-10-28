using PcChecker.Core.Common;
using PcChecker.Core.Drivers.Platform;
using PcChecker.Core.Localization;
using PcChecker.Core.Memory;
using PcChecker.Core.Sensors;

namespace PcChecker.Core.RamTuning;

/// <summary>
/// Demo RAM tuning for the sample desktop: the Memory page's G.Skill DDR5-6000 CL30 kit (2 × 16 GB in A2/B2) running
/// at 4800 with XMP off, an Intel Core i7-13700K on an ASUS ROG STRIX Z790-E with BIOS 2801, and the demo DIMM
/// temperatures, so every stage of the plan has something to show. The quick check runs the real engine on a small
/// buffer. Nothing touches the machine.
/// </summary>
public sealed class DemoRamTuningService : IRamTuningService
{
    /// <summary>The demo check never holds more than this, whatever share is chosen.</summary>
    public const long DemoBufferBytes = 64L * 1024 * 1024;

    /// <summary>The demo check runs at most this long.</summary>
    public static readonly TimeSpan DemoMaxDuration = TimeSpan.FromSeconds(20);

    private readonly IMemoryService _memory;
    private readonly ISensorService _sensors;
    private RamTuningJournal? _journal;

    public DemoRamTuningService(IMemoryService memory, ISensorService sensors)
    {
        _memory = memory;
        _sensors = sensors;
    }

    /// <summary>The demo PC's SMBIOS values (the same board and BIOS as the Hardware page).</summary>
    public static SmbiosFacts Facts { get; } = new()
    {
        SystemManufacturer = "ASUS",
        SystemModel = "System Product Name",
        BoardManufacturer = "ASUSTeK COMPUTER INC.",
        BoardProduct = "ROG STRIX Z790-E GAMING WIFI",
        BoardVersion = "Rev 1.xx",
        BiosManufacturer = "American Megatrends Inc.",
        BiosVersion = "2801",
        BiosReleaseDate = new DateTime(2025, 1, 20),
        ChassisTypes = "3",
        PcSystemType = "1",
        CpuName = DemoData.CpuName,
        CpuManufacturer = "GenuineIntel",
    };

    public RamFacts? Last { get; private set; }

    public async Task<RamFacts> DetectAsync(Translator? tr, CancellationToken cancellationToken)
    {
        var overview = _memory.Last ?? await _memory.GetOverviewAsync(cancellationToken).ConfigureAwait(false);
        await DemoData.DelayAsync(cancellationToken, 150, 400).ConfigureAwait(false);
        var info = SmbiosNormalizer.Normalize(Facts);
        var readings = _sensors.Latest?.Readings ?? DemoReadings();
        Last = RamFactsBuilder.Build(overview.Modules, overview.Slots > 0 ? overview.Slots : 4, DemoData.CpuName, info, readings, DateTimeOffset.Now, tr);
        return Last;
    }

    public long? GetFreeMemoryBytes() => _memory.Last?.AvailableBytes ?? (long)(DemoData.TotalMemoryBytes * 0.58);

    public Task<QuickCheckResult> RunQuickCheckAsync(int percent, TimeSpan duration, IProgress<QuickCheckProgress>? progress, Translator? tr, CancellationToken cancellationToken)
    {
        long planned = QuickMemoryCheck.PlanBytes(GetFreeMemoryBytes() ?? 0, percent);
        var shown = duration < DemoMaxDuration ? duration : DemoMaxDuration;

        // The real engine on a small buffer, so progress, timing and the result look genuine; the planned size is reported.
        return Task.Run(async () =>
        {
            var real = await QuickMemoryCheck.RunAsync(Math.Min(planned, DemoBufferBytes), shown, () => GetFreeMemoryBytes(), progress, cancellationToken, tr: tr)
                .ConfigureAwait(false);
            long bytes = real.BytesTested == 0 ? 0 : planned;
            return real with { BytesTested = bytes, Summary = QuickMemoryCheck.Summary(bytes, real.Elapsed, real.Rounds, real.Errors, real.Stop, tr) + " (demo)" };
        }, CancellationToken.None);
    }

    public Task<RamTuningJournal> LoadJournalAsync(CancellationToken cancellationToken)
    {
        if (_journal is null)
        {
            // The plan was started two days ago with XMP off; yesterday's check found it still off.
            var now = DateTimeOffset.Now;
            _journal = new RamTuningJournal
            {
                Baseline = new RamCheckRecord(now.AddDays(-2).AddHours(-3), 4800, "Disabled", ChannelMode.Dual, CheckOutcome.Ok, "Starting point", 0, 0),
            };
            _journal.Add(new RamCheckRecord(now.AddDays(-1).AddHours(-5), 4800, "Disabled", ChannelMode.Dual, CheckOutcome.Problem,
                "Not there yet; read the red items.", 0, 0));
        }

        return Task.FromResult(_journal);
    }

    public Task SaveJournalAsync(RamTuningJournal journal, CancellationToken cancellationToken)
    {
        _journal = journal;
        return Task.CompletedTask;
    }

    private static IReadOnlyList<SensorReading> DemoReadings() =>
    [
        new("ram.temp.a2", DemoData.MemoryKit, HardwareKind.Memory, SensorKind.Temperature, "DIMM A2", 41, 36, 44, "°C"),
        new("ram.temp.b2", DemoData.MemoryKit, HardwareKind.Memory, SensorKind.Temperature, "DIMM B2", 42, 37, 45, "°C"),
    ];
}
