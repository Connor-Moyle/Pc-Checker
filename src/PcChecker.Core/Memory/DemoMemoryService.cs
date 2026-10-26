using PcChecker.Core.Common;
using PcChecker.Core.Hardware;

namespace PcChecker.Core.Memory;

/// <summary>Demo memory service: 32 GB DDR5-6000 kit running at 4800 (XMP off) so the page has something to advise.</summary>
public sealed class DemoMemoryService : IMemoryService
{
    private bool _compression = true;
    private bool _automaticPageFile = true;
    private PageFileInfo _pageFile = new("C:", 4864, 19456, true);

    public IReadOnlyList<MemoryProfile> Profiles => MemoryProfileCatalog.All;
    public MemoryOverview? Last { get; private set; }

    public async Task<MemoryOverview> GetOverviewAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        var random = DemoData.CreateRandom(Environment.TickCount / 2000);
        long total = DemoData.TotalMemoryBytes;
        long available = (long)(total * DemoData.Jitter(random, 0.58, 0.03));
        var modules = new List<MemoryModuleInfo>
        {
            Module("DIMM_A2", "F5-6000J3636F16G"),
            Module("DIMM_B2", "F5-6000J3636F16G"),
        };
        var warnings = new List<string>
        {
            "Your RAM is rated for 6000 MT/s but runs at 4800. Enable XMP (Intel) or EXPO (AMD) in the BIOS to get the speed you paid for.",
        };
        warnings.AddRange(PageFileRules.Evaluate(total, _automaticPageFile, [_pageFile]));
        Last = new MemoryOverview(total, available, (long)(total * 0.52), total + 19456L * 1024 * 1024, (long)(total * 0.21), (long)(total * 0.17),
            _compression, true, _automaticPageFile, [_pageFile], "Disabled", 4800, 6000, modules,
            "No errors were detected (Windows Memory Diagnostic, 12 days ago)", 4, 2, true, warnings);
        return Last;
    }

    public async Task<MemoryTestResult> RunMemoryTestAsync(int percentOfAvailable, int passes, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.Now;
        int totalSteps = Math.Max(1, passes) * MemoryTestPatterns.Names.Count;
        int step = 0;
        for (int pass = 1; pass <= Math.Max(1, passes); pass++)
        {
            for (int p = 0; p < MemoryTestPatterns.Names.Count; p++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new ProgressUpdate($"Pass {pass}: {MemoryTestPatterns.Names[p]}", 100.0 * step++ / totalSteps));
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
        }

        long bytes = (long)(DemoData.TotalMemoryBytes * 0.58 * Math.Clamp(percentOfAvailable, 1, 100) / 100);
        progress?.Report(new ProgressUpdate("Done", 100));
        return new MemoryTestResult(bytes, Math.Max(1, passes), 0, [], DateTimeOffset.Now - started, true,
            $"Tested {ByteSize.Format(bytes)} of free memory in {Math.Max(1, passes)} pass(es): no errors (demo). This is a quick user-mode check; run the Windows Memory Diagnostic for a full test.");
    }

    public async Task<OperationResult> ScheduleWindowsMemoryDiagnosticAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok("Windows Memory Diagnostic will run the next time the PC restarts (demo).", rebootRequired: true);
    }

    public async Task<OperationResult> ApplyProfileAsync(MemoryProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        _compression = profile.MemoryCompression;
        _automaticPageFile = profile.AutomaticPageFile;
        if (!profile.AutomaticPageFile)
        {
            var (initial, max) = PageFileRules.Recommend(DemoData.TotalMemoryBytes);
            _pageFile = new PageFileInfo("C:", profile.PageFileInitialMb ?? initial, profile.PageFileMaxMb ?? max, false);
        }
        else
        {
            _pageFile = new PageFileInfo("C:", 4864, 19456, true);
        }

        return OperationResult.Ok($"Profile \"{profile.Name}\" applied (demo).", rebootRequired: !profile.AutomaticPageFile);
    }

    public async Task<OperationResult> ClearStandbyListAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok("Standby list cleared: 5.4 GB returned to free memory (demo).");
    }

    public async Task<OperationResult> SetPageFileAsync(string drive, bool systemManaged, int? initialMb, int? maxMb, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        _automaticPageFile = systemManaged;
        _pageFile = new PageFileInfo(drive, initialMb ?? 4864, maxMb ?? 19456, systemManaged);
        return OperationResult.Ok($"Page file on {drive} updated (demo).", rebootRequired: true);
    }

    public async Task<OperationResult> SetMemoryCompressionAsync(bool enabled, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        _compression = enabled;
        return OperationResult.Ok($"Memory compression {(enabled ? "enabled" : "disabled")} (demo).");
    }

    private static MemoryModuleInfo Module(string slot, string part) =>
        new($"{slot} 16 GB DDR5", "G.Skill", slot, "OK", "BANK 0", slot, DemoData.MemoryModuleBytes, 6000, 4800, "DDR5", "DIMM", part,
            "0000" + slot[^2..], 1100, 64, 64, new Dictionary<string, string>());
}
