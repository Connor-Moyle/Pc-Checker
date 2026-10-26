using PcChecker.Core.Common;
using PcChecker.Core.Hardware;

namespace PcChecker.Core.Memory;

/// <summary>
/// Demo memory service for the sample gaming desktop: a 32 GB DDR5-6000 kit running at 4800 (XMP off) so the page
/// has something to advise. Pure .NET; remembers the changes it is asked to make so the page reacts realistically.
/// </summary>
public sealed class DemoMemoryService : IMemoryService, IMemoryTuning
{
    private const long DemoTestCapBytes = 256L * 1024 * 1024;

    private bool _compression = true;
    private bool _sysMain = true;
    private bool _automaticPageFile = true;
    private bool _diagnosticScheduled;
    private PageFileInfo _pageFile = new("C:", 4864, 19456, true);
    private double _standbyFraction = 0.17;

    public IReadOnlyList<MemoryProfile> Profiles => MemoryProfileCatalog.All;
    public MemoryOverview? Last { get; private set; }
    public string? PlatformVendor => "Intel";

    public async Task<MemoryOverview> GetOverviewAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        var random = DemoData.CreateRandom(Environment.TickCount / 2000);
        long total = DemoData.TotalMemoryBytes;
        long available = (long)(total * DemoData.Jitter(random, 0.58, 0.03));
        var modules = new List<MemoryModuleInfo>
        {
            Module("DIMM_A2", "F5-6000J3636F16GX2-TZ5RK", "F5A2"),
            Module("DIMM_B2", "F5-6000J3636F16GX2-TZ5RK", "F5B2"),
        };

        var xmp = MemoryRules.EvaluateXmp(modules, "Intel");
        var channels = MemoryRules.EvaluateChannels(modules, 4);
        string diagnostic = _diagnosticScheduled
            ? "Windows Memory Diagnostic is scheduled for the next restart (last run: no errors, 12 days ago)"
            : MemoryRules.DescribeDiagnostic(new MemoryDiagnosticEvent(1201, DateTimeOffset.Now.AddDays(-12), "no errors"), DateTimeOffset.Now);
        long commitTotal = (long)(total * 0.52);
        long commitLimit = total + (long)_pageFile.MaximumMb * 1024 * 1024;
        var facts = new MemoryFacts(total, available, commitTotal, commitLimit, _compression, _sysMain, _automaticPageFile, [_pageFile], modules, xmp, diagnostic);
        var warnings = MemoryRules.BuildWarnings(facts);

        Last = new MemoryOverview(total, available, commitTotal, commitLimit, (long)(total * 0.21), (long)(total * _standbyFraction),
            _compression, _sysMain, _automaticPageFile, [_pageFile], xmp.State, xmp.ConfiguredMhz, xmp.RatedMhz, modules,
            diagnostic, 4, 2, channels.DualChannelLikely, warnings);
        return Last;
    }

    public async Task<MemoryTestResult> RunMemoryTestAsync(int percentOfAvailable, int passes, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        long available = Last?.AvailableBytes ?? (long)(DemoData.TotalMemoryBytes * 0.58);
        long planned = MemoryTestRunner.PlanBytes(available, percentOfAvailable);
        passes = Math.Max(1, passes);

        // Run the real engine on a small buffer so progress and timing look genuine, then report the planned size.
        var started = DateTimeOffset.Now;
        var real = await MemoryTestRunner.RunAsync(Math.Min(planned, DemoTestCapBytes), passes, progress, cancellationToken).ConfigureAwait(false);
        for (int i = 0; i < passes && real.Completed; i++)
        {
            await Task.Delay(300, cancellationToken).ConfigureAwait(false);
        }

        var elapsed = DateTimeOffset.Now - started;
        long reported = real.Completed ? planned : Math.Min(planned, real.BytesTested);
        string summary = MemoryTestRunner.BuildSummary(reported, passes, real.Errors, !real.Completed, elapsed, "(demo)");
        return new MemoryTestResult(reported, passes, real.Errors, real.ErrorDetails, elapsed, real.Completed, summary);
    }

    public async Task<OperationResult> ScheduleWindowsMemoryDiagnosticAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        _diagnosticScheduled = true;
        return OperationResult.Ok("Windows Memory Diagnostic will run the next time the PC restarts (demo). It takes a few minutes and shows the result after Windows starts again.", rebootRequired: true);
    }

    public async Task<OperationResult> ApplyProfileAsync(MemoryProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        bool compressionChanged = _compression != profile.MemoryCompression;
        _compression = profile.MemoryCompression;
        _sysMain = profile.SysMain;
        _automaticPageFile = profile.AutomaticPageFile;
        var size = PageFileRules.SizeForProfile(profile, DemoData.TotalMemoryBytes);
        _pageFile = size is null
            ? new PageFileInfo("C:", 4864, 19456, true)
            : new PageFileInfo("C:", size.Value.InitialMb, size.Value.MaximumMb, false);

        string pageFileText = size is null ? "Windows manages the page file." : $"Page file on C: fixed at {size.Value.MaximumMb} MB.";
        return OperationResult.Ok($"Profile \"{profile.Name}\" applied (demo). Memory compression {(profile.MemoryCompression ? "on" : "off")}, SysMain {(profile.SysMain ? "on" : "off")}. {pageFileText}"
            + (size is null && !compressionChanged ? string.Empty : " Restart the PC for these changes to take effect."), rebootRequired: size is not null || compressionChanged);
    }

    public async Task<OperationResult> ClearStandbyListAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        long freed = (long)(DemoData.TotalMemoryBytes * _standbyFraction);
        _standbyFraction = 0.02;
        return OperationResult.Ok($"Standby list cleared: {ByteSize.Format(freed)} returned to free memory (demo).");
    }

    public async Task<OperationResult> SetPageFileAsync(string drive, bool systemManaged, int? initialMb, int? maxMb, CancellationToken cancellationToken)
    {
        string? problem = PageFileRules.Validate(drive, systemManaged, initialMb, maxMb, DemoData.TotalMemoryBytes);
        if (problem is not null)
        {
            return OperationResult.Fail(problem);
        }

        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        string normalized = PageFileRules.ChooseDrive([new PageFileInfo(drive, 0, 0, true)], drive);
        _automaticPageFile = systemManaged;
        _pageFile = systemManaged ? new PageFileInfo(normalized, 4864, 19456, true) : new PageFileInfo(normalized, initialMb ?? 4864, maxMb ?? 19456, false);
        return OperationResult.Ok(systemManaged
            ? "Windows now manages the page file (demo). Restart the PC to apply."
            : $"Page file on {normalized} set to {initialMb}–{maxMb} MB (demo). Restart the PC to apply.", rebootRequired: true);
    }

    public async Task<OperationResult> SetMemoryCompressionAsync(bool enabled, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        bool before = _compression;
        _compression = enabled;
        var result = MemoryCompressionChange.Evaluate(enabled, before, enabled);
        return result with { Message = result.Message + " (demo)" };
    }

    public async Task<OperationResult> SetSysMainAsync(bool enabled, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        _sysMain = enabled;
        return OperationResult.Ok(enabled ? "SysMain (Superfetch) is running and starts with Windows (demo)." : "SysMain (Superfetch) is stopped and disabled (demo).");
    }

    private static MemoryModuleInfo Module(string slot, string part, string serialSuffix) =>
        new($"{slot} 16 GB DDR5", "G.Skill", slot, "OK", "BANK 0", slot, DemoData.MemoryModuleBytes, 6000, 4800, "DDR5", "DIMM", part,
            "04C" + serialSuffix + "71", 1100, 64, 64, new Dictionary<string, string>
            {
                ["Capacity"] = DemoData.MemoryModuleBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Speed"] = "6000",
                ["ConfiguredClockSpeed"] = "4800",
                ["SMBIOSMemoryType"] = "34",
                ["Manufacturer"] = "G.Skill",
                ["PartNumber"] = part,
            });
}
