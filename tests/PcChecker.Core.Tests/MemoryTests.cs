using PcChecker.Core.Common;
using PcChecker.Core.Hardware;
using PcChecker.Core.Memory;
using PcChecker.Core.Optimization;
using Xunit;

namespace PcChecker.Core.Tests;

public class MemoryRulesTests
{
    private const long Gb = 1024L * 1024 * 1024;

    private static MemoryModuleInfo Module(long capacity = 16 * Gb, int? rated = 6000, int? configured = 4800, string type = "DDR5", string vendor = "G.Skill") =>
        new("DIMM", vendor, null, "OK", "BANK 0", "DIMM_A2", capacity, rated, configured, type, "DIMM", "F5-6000", "0001", 1100, 64, 64, new Dictionary<string, string>());

    [Fact]
    public void Xmp_is_disabled_when_configured_speed_is_below_rating()
    {
        var xmp = MemoryRules.EvaluateXmp([Module(), Module()], "Intel");
        Assert.Equal("Disabled", xmp.State);
        Assert.True(xmp.IsDisabled);
        Assert.Equal("XMP", xmp.ProfileName);
        Assert.Equal(4800, xmp.ConfiguredMhz);
        Assert.Equal(6000, xmp.RatedMhz);
        Assert.Contains("rated for 6000", xmp.Title);
        Assert.Contains("Enable XMP", xmp.Guidance);
        Assert.Equal(Severity.Medium, xmp.Severity);
    }

    [Fact]
    public void Xmp_is_enabled_at_or_above_rated_speed_within_tolerance()
    {
        Assert.Equal("Enabled", MemoryRules.EvaluateXmp([Module(configured: 6000)]).State);
        Assert.Equal("Enabled", MemoryRules.EvaluateXmp([Module(configured: 5900)]).State);
        var above = MemoryRules.EvaluateXmp([Module(configured: 6400)]);
        Assert.Equal("Enabled", above.State);
        Assert.Contains("above", above.Title);
        Assert.Equal(Severity.Info, above.Severity);
    }

    [Fact]
    public void Xmp_is_unknown_without_speeds_and_names_the_platform_profile()
    {
        var unknown = MemoryRules.EvaluateXmp([Module(rated: null, configured: null)], "AMD");
        Assert.Equal("Unknown", unknown.State);
        Assert.Equal("EXPO", unknown.ProfileName);
        Assert.Equal("Unknown", MemoryRules.EvaluateXmp([], null).State);
        Assert.Equal("XMP", MemoryRules.ProfileNameFor("AMD", "DDR4"));
        Assert.Equal("XMP/EXPO", MemoryRules.ProfileNameFor(null, "DDR5"));
    }

    [Fact]
    public void Channel_heuristic_needs_matching_pairs()
    {
        Assert.True(MemoryRules.EvaluateChannels([Module(), Module()], 4).DualChannelLikely);
        var single = MemoryRules.EvaluateChannels([Module()], 4);
        Assert.False(single.DualChannelLikely);
        Assert.Contains("Single-channel", single.Title);
        Assert.Contains("second matching module", single.Detail);
        Assert.False(MemoryRules.EvaluateChannels([Module(), Module(capacity: 8 * Gb)], 4).DualChannelLikely);
        Assert.False(MemoryRules.EvaluateChannels([Module(), Module(), Module()], 4).DualChannelLikely);
        Assert.True(MemoryRules.EvaluateChannels([Module(), Module(), Module(), Module()], 4).DualChannelLikely);
        Assert.False(MemoryRules.EvaluateChannels([], 4).DualChannelLikely);
    }

    [Fact]
    public void Warnings_cover_xmp_single_module_low_memory_and_diagnostic_problems()
    {
        var xmp = MemoryRules.EvaluateXmp([Module()], "Intel");
        var facts = new MemoryFacts(32 * Gb, 2 * Gb, 30 * Gb, 31 * Gb, false, false, true, [], [Module()], xmp,
            "Hardware problems were detected (Windows Memory Diagnostic, yesterday)");
        var warnings = MemoryRules.BuildWarnings(facts);
        Assert.Contains(warnings, w => w.Contains("hardware problems", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(warnings, w => w.Contains("free right now"));
        Assert.Contains(warnings, w => w.Contains("commit limit"));
        Assert.Contains(warnings, w => w.Contains("Enable XMP"));
        Assert.Contains(warnings, w => w.Contains("single-channel"));
        Assert.Contains(warnings, w => w.Contains("SysMain"));
        Assert.DoesNotContain(warnings, w => w.Contains("Memory compression is off"));
        Assert.Equal(warnings[0], warnings.First(w => w.Contains("hardware problems", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Healthy_system_has_no_warnings()
    {
        var modules = new[] { Module(configured: 6000), Module(configured: 6000) };
        var xmp = MemoryRules.EvaluateXmp(modules, "Intel");
        var facts = new MemoryFacts(32 * Gb, 20 * Gb, 10 * Gb, 48 * Gb, true, true, true, [], modules, xmp, "No errors were detected");
        Assert.Empty(MemoryRules.BuildWarnings(facts));
    }

    [Fact]
    public void Small_systems_warn_about_ram_size_mixed_modules_and_compression()
    {
        var modules = new[] { Module(capacity: 4 * Gb, configured: 3200, rated: 3200), Module(capacity: 2 * Gb, configured: 3200, rated: 3200) };
        var xmp = MemoryRules.EvaluateXmp(modules, "AMD");
        var facts = new MemoryFacts(6 * Gb, 3 * Gb, 4 * Gb, 12 * Gb, false, true, true, [], modules, xmp, null, LowMemoryPercent: 10);
        var warnings = MemoryRules.BuildWarnings(facts);
        Assert.Contains(warnings, w => w.Contains("different sizes"));
        Assert.Contains(warnings, w => w.Contains("tight for Windows 11"));
        Assert.Contains(warnings, w => w.Contains("Memory compression is off"));
    }

    [Fact]
    public void Diagnostic_events_are_described_in_plain_english()
    {
        var now = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
        Assert.Contains("not been run", MemoryRules.DescribeDiagnostic(null, now));
        Assert.Equal("No errors were detected (Windows Memory Diagnostic, 12 days ago)", MemoryRules.DescribeDiagnostic(new MemoryDiagnosticEvent(1201, now.AddDays(-12), null), now));
        Assert.Contains("Hardware problems were detected", MemoryRules.DescribeDiagnostic(new MemoryDiagnosticEvent(1202, now.AddDays(-1), null), now));
        Assert.Contains("yesterday", MemoryRules.DescribeDiagnostic(new MemoryDiagnosticEvent(1202, now.AddDays(-1), null), now));
        Assert.Equal("today", MemoryRules.DescribeAge(TimeSpan.FromHours(3)));
        Assert.Equal("2 months ago", MemoryRules.DescribeAge(TimeSpan.FromDays(70)));
        Assert.Equal("1 year ago", MemoryRules.DescribeAge(TimeSpan.FromDays(400)));
    }

    [Fact]
    public void Kit_description_and_type_names()
    {
        Assert.Equal("2 × 16 GB DDR5-6000 (G.Skill)", MemoryRules.DescribeKit([Module(), Module()]));
        Assert.Equal("1 × 16 GB + 1 × 8 GB DDR5-6000 (G.Skill)", MemoryRules.DescribeKit([Module(), Module(capacity: 8 * Gb)]));
        Assert.Equal("No module details available", MemoryRules.DescribeKit([]));
        Assert.Equal("DDR5", MemoryRules.MemoryTypeName(34));
        Assert.Equal("DDR4", MemoryRules.MemoryTypeName(26));
        Assert.Equal("SODIMM", MemoryRules.FormFactorName(12));
        Assert.Null(MemoryRules.CleanVendor("Unknown"));
        Assert.Null(MemoryRules.CleanVendor("0000"));
        Assert.Equal("Kingston", MemoryRules.CleanVendor(" Kingston "));
        Assert.Equal("16 GB", MemoryRules.FormatCapacity(16 * Gb));
        Assert.Equal("512 MB", MemoryRules.FormatCapacity(512L * 1024 * 1024));
        Assert.Equal("1.50 GB", MemoryRules.FormatCapacity(Gb + Gb / 2));
        Assert.Equal("6000 MT/s", MemoryRules.FormatSpeed(6000));
        Assert.Equal("—", MemoryRules.FormatSpeed(null));
    }
}

public class MemoryTestRunnerTests
{
    [Fact]
    public void Plan_bytes_keeps_headroom_and_clamps_percent()
    {
        long available = 16L * 1024 * 1024 * 1024;
        Assert.Equal(4L * 1024 * 1024 * 1024, MemoryTestRunner.PlanBytes(available, 25));
        Assert.Equal(14745L * 1024 * 1024, MemoryTestRunner.PlanBytes(available, 100)); // clamped to 90 %, whole MB
        Assert.Equal(0, MemoryTestRunner.PlanBytes(0, 50));
        Assert.True(MemoryTestRunner.PlanBytes(512L * 1024 * 1024, 90) <= 512L * 1024 * 1024 * 9 / 10);
        Assert.Equal(0, MemoryTestRunner.PlanBytes(16L * 1024 * 1024 * 1024, 25) % (1024 * 1024));
    }

    [Fact]
    public void Plan_chunks_splits_into_half_gigabyte_pieces()
    {
        var chunks = MemoryTestRunner.PlanChunks(1100L * 1024 * 1024);
        Assert.Equal([MemoryTestRunner.MaxChunkBytes, MemoryTestRunner.MaxChunkBytes, 76 * 1024 * 1024], chunks);
        Assert.Empty(MemoryTestRunner.PlanChunks(0));
    }

    [Fact]
    public async Task Clean_run_reports_no_errors_and_progress()
    {
        var updates = new List<ProgressUpdate>();
        var progress = new SynchronousProgress(updates.Add);
        var result = await MemoryTestRunner.RunAsync(4 * 1024 * 1024, 2, progress, CancellationToken.None);
        Assert.True(result.Completed);
        Assert.Equal(0, result.Errors);
        Assert.Equal(2, result.Passes);
        Assert.Equal(4 * 1024 * 1024, result.BytesTested);
        Assert.Contains("no errors", result.Summary);
        Assert.Contains("Windows Memory Diagnostic", result.Summary);
        Assert.Contains(updates, u => u.Stage.Contains("Pass 2 of 2"));
        Assert.Equal(100, updates.Last().Percent);
    }

    [Fact]
    public async Task Injected_corruption_is_found_with_offsets()
    {
        var result = await MemoryTestRunner.RunAsync(2 * 1024 * 1024, 1, null, CancellationToken.None,
            faultInjector: (block, chunk, pattern) =>
            {
                if (pattern == 3)
                {
                    block[10] ^= 0xFF;
                    block[20] = ~block[20];
                }
            });
        Assert.True(result.Completed);
        Assert.Equal(2, result.Errors);
        Assert.Equal(2, result.ErrorDetails.Count);
        Assert.Contains("offset 0x50", result.ErrorDetails[0]);
        Assert.Contains("Random (seeded)", result.ErrorDetails[0]);
        Assert.Contains("errors found", result.Summary);
        Assert.Contains("XMP/EXPO", result.Summary);
    }

    [Fact]
    public async Task Cancellation_returns_a_partial_result()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var result = await MemoryTestRunner.RunAsync(1024 * 1024, 3, null, cts.Token);
        Assert.False(result.Completed);
        Assert.Contains("Stopped early", result.Summary);
    }

    [Fact]
    public async Task Zero_bytes_is_a_graceful_failure()
    {
        var result = await MemoryTestRunner.RunAsync(0, 1, null, CancellationToken.None);
        Assert.False(result.Completed);
        Assert.Equal(0, result.BytesTested);
        Assert.Contains("not enough free memory", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class SynchronousProgress(Action<ProgressUpdate> handler) : IProgress<ProgressUpdate>
    {
        public void Report(ProgressUpdate value) => handler(value);
    }
}

public class PageFileRulesTests
{
    private const long Gb = 1024L * 1024 * 1024;

    [Fact]
    public void Profile_sizes_follow_the_catalog_rules()
    {
        Assert.Null(PageFileRules.SizeForProfile(MemoryProfileCatalog.Find(MemoryProfileCatalog.BalancedId)!, 32 * Gb));
        Assert.Equal((24576, 24576), PageFileRules.SizeForProfile(MemoryProfileCatalog.Find(MemoryProfileCatalog.GamingId)!, 16 * Gb));
        // 1.5× of 32 GB would be 48 GB: capped, because a big-RAM machine pages very little and the file lives on the system SSD.
        Assert.Equal((PageFileRules.MaximumRecommendedMb, PageFileRules.MaximumRecommendedMb), PageFileRules.SizeForProfile(MemoryProfileCatalog.Find(MemoryProfileCatalog.GamingId)!, 32 * Gb));
        Assert.Equal((PageFileRules.MaximumRecommendedMb, PageFileRules.MaximumRecommendedMb), PageFileRules.SizeForProfile(MemoryProfileCatalog.Find(MemoryProfileCatalog.GamingId)!, 64 * Gb));
        Assert.Equal((8192, 8192), PageFileRules.SizeForProfile(MemoryProfileCatalog.Find(MemoryProfileCatalog.GamingId)!, 4 * Gb));
        Assert.Equal((16384, 16384), PageFileRules.SizeForProfile(MemoryProfileCatalog.Find(MemoryProfileCatalog.LowMemoryId)!, 8 * Gb));
        Assert.Equal((16384, 16384), PageFileRules.SizeForProfile(MemoryProfileCatalog.Find(MemoryProfileCatalog.WorkstationId)!, 16 * Gb));
        Assert.Equal((32768, 32768), PageFileRules.SizeForProfile(MemoryProfileCatalog.Find(MemoryProfileCatalog.WorkstationId)!, 64 * Gb));
        Assert.Equal((32768, 32768), PageFileRules.SizeForProfile(MemoryProfileCatalog.Find(MemoryProfileCatalog.WorkstationId)!, 128 * Gb));
        var explicitProfile = new MemoryProfile("x", "x", "x", true, true, false, 1024, 2048, false);
        Assert.Equal((1024, 2048), PageFileRules.SizeForProfile(explicitProfile, 32 * Gb));
    }

    [Fact]
    public void Validation_catches_bad_manual_entries()
    {
        Assert.Null(PageFileRules.Validate("C:", true, null, null, 16 * Gb));
        Assert.Null(PageFileRules.Validate("D:", false, 8192, 16384, 16 * Gb));
        Assert.Contains("drive letter", PageFileRules.Validate("", false, 8192, 8192, 16 * Gb));
        Assert.Contains("both", PageFileRules.Validate("C:", false, null, 8192, 16 * Gb));
        Assert.Contains("at least 16 MB", PageFileRules.Validate("C:", false, 4, 8192, 16 * Gb));
        Assert.Contains("at least as large", PageFileRules.Validate("C:", false, 8192, 4096, 16 * Gb));
        Assert.Contains("four times", PageFileRules.Validate("C:", false, 8192, 100000, 16 * Gb));
    }

    [Fact]
    public void Drive_choice_prefers_the_existing_page_file_then_the_system_drive()
    {
        Assert.Equal("D:", PageFileRules.ChooseDrive([new PageFileInfo("d:", 0, 0, true)], "C:"));
        Assert.Equal("E:", PageFileRules.ChooseDrive([], "e:\\"));
        Assert.Equal("C:", PageFileRules.ChooseDrive([], ""));
    }
}

public class MemoryProfileCatalogTests
{
    private const long Gb = 1024L * 1024 * 1024;

    [Fact]
    public void Catalog_is_sane()
    {
        Assert.Equal(4, MemoryProfileCatalog.All.Count);
        Assert.Equal(MemoryProfileCatalog.All.Count, MemoryProfileCatalog.All.Select(p => p.Id).Distinct().Count());
        Assert.All(MemoryProfileCatalog.All, p => Assert.False(string.IsNullOrWhiteSpace(p.Description)));
        Assert.Single(MemoryProfileCatalog.All, p => p.AdvancedOnly);
        Assert.Single(MemoryProfileCatalog.All, p => p.AutomaticPageFile);
        Assert.Equal(MemoryProfileCatalog.BalancedId, MemoryProfileCatalog.Recommended(32 * Gb).Id);
        Assert.Equal(MemoryProfileCatalog.LowMemoryId, MemoryProfileCatalog.Recommended(8 * Gb).Id);
        Assert.NotNull(MemoryProfileCatalog.Find("MEMORY.PROFILE.GAMING"));
        Assert.Null(MemoryProfileCatalog.Find("memory.profile.nope"));
    }

    [Fact]
    public void Matching_compares_compression_sysmain_and_page_file()
    {
        var balanced = MemoryProfileCatalog.Find(MemoryProfileCatalog.BalancedId)!;
        var gaming = MemoryProfileCatalog.Find(MemoryProfileCatalog.GamingId)!;
        var auto = MemoryOverview.Empty() with { TotalBytes = 32 * Gb, MemoryCompressionEnabled = true, SysMainEnabled = true, AutomaticPageFile = true };
        Assert.True(MemoryProfileCatalog.Matches(balanced, auto));
        Assert.False(MemoryProfileCatalog.Matches(gaming, auto));
        var fixedFile = auto with { AutomaticPageFile = false, PageFiles = [new PageFileInfo("C:", PageFileRules.MaximumRecommendedMb, PageFileRules.MaximumRecommendedMb, false)] };
        Assert.True(MemoryProfileCatalog.Matches(gaming, fixedFile));
        Assert.False(MemoryProfileCatalog.Matches(balanced, fixedFile));
        Assert.False(MemoryProfileCatalog.Matches(balanced, auto with { SysMainEnabled = false }));
        // Compression could not be read (Get-MMAgent fails while SysMain is disabled): never claim a match.
        Assert.False(MemoryProfileCatalog.Matches(balanced, auto with { MemoryCompressionKnown = false }));
    }
}

public class MemoryActionsProviderTests
{
    [Fact]
    public void Provider_lists_the_required_actions()
    {
        var provider = new MemoryActionsProvider(new DemoMemoryService());
        string[] expected = ["memory.compression.on", "memory.compression.off", "memory.sysmain.on", "memory.sysmain.off", "memory.standby.clear", "memory.pagefile.auto"];
        Assert.Equal(expected, provider.Actions.Select(a => a.Id));
        Assert.All(expected, id => Assert.True(provider.Handles(id)));
        Assert.True(provider.Handles("MEMORY.SYSMAIN.ON"));
        Assert.False(provider.Handles("latency.hags.on"));
        Assert.All(provider.Actions, a => Assert.Equal("Memory", a.Category));
        Assert.Contains(provider.Actions, a => a.Id == "memory.pagefile.auto" && a.RequiresRestart);
    }

    [Fact]
    public async Task Provider_applies_and_reverts_through_the_memory_service()
    {
        var memory = new DemoMemoryService();
        var provider = new MemoryActionsProvider(memory, new MemoryPageFileStore());

        Assert.True((await provider.GetStateAsync("memory.compression.on", CancellationToken.None)).IsApplied);
        Assert.True((await provider.ApplyAsync("memory.compression.off", CancellationToken.None)).Success);
        await memory.GetOverviewAsync(CancellationToken.None);
        Assert.True((await provider.GetStateAsync("memory.compression.off", CancellationToken.None)).IsApplied);
        Assert.False((await provider.GetStateAsync("memory.compression.on", CancellationToken.None)).IsApplied);

        Assert.True((await provider.ApplyAsync("memory.sysmain.off", CancellationToken.None)).Success);
        var overview = await memory.GetOverviewAsync(CancellationToken.None);
        Assert.False(overview.SysMainEnabled);
        Assert.True((await provider.RevertAsync("memory.sysmain.off", CancellationToken.None)).Success);
        Assert.True((await memory.GetOverviewAsync(CancellationToken.None)).SysMainEnabled);

        Assert.True((await provider.ApplyAsync("memory.standby.clear", CancellationToken.None)).Success);
        Assert.True((await provider.RevertAsync("memory.standby.clear", CancellationToken.None)).Success);

        Assert.True((await memory.SetPageFileAsync("D:", false, 4096, 8192, CancellationToken.None)).Success);
        Assert.True((await provider.ApplyAsync("memory.pagefile.auto", CancellationToken.None)).Success);
        Assert.True((await memory.GetOverviewAsync(CancellationToken.None)).AutomaticPageFile);
        Assert.True((await provider.RevertAsync("memory.pagefile.auto", CancellationToken.None)).Success);
        var restored = await memory.GetOverviewAsync(CancellationToken.None);
        Assert.False(restored.AutomaticPageFile);
        Assert.Equal(new PageFileInfo("D:", 4096, 8192, false), restored.PageFiles[0]);

        Assert.False((await provider.ApplyAsync("memory.unknown", CancellationToken.None)).Success);
        Assert.Null((await provider.GetStateAsync("memory.standby.clear", CancellationToken.None)).IsApplied);
    }

    [Fact]
    public async Task Page_file_revert_restores_the_remembered_configuration_only()
    {
        var memory = new DemoMemoryService();
        var store = new MemoryPageFileStore();
        var provider = new MemoryActionsProvider(memory, store);

        // Nothing remembered: Revert changes nothing instead of writing a fixed page file of up to 32 GB.
        var unknown = await provider.RevertAsync("memory.pagefile.auto", CancellationToken.None);
        Assert.False(unknown.Success);
        Assert.Contains("not recorded", unknown.Message);
        Assert.True((await memory.GetOverviewAsync(CancellationToken.None)).AutomaticPageFile);

        Assert.True((await memory.SetPageFileAsync("C:", false, 2048, 4096, CancellationToken.None)).Success);
        Assert.True((await provider.ApplyAsync("memory.pagefile.auto", CancellationToken.None)).Success);
        Assert.Equal("auto=0|C:=2048-4096", store.Value);

        // A second Apply keeps the first snapshot.
        Assert.True((await provider.ApplyAsync("memory.pagefile.auto", CancellationToken.None)).Success);
        Assert.Equal("auto=0|C:=2048-4096", store.Value);

        var revert = await provider.RevertAsync("memory.pagefile.auto", CancellationToken.None);
        Assert.True(revert.Success);
        Assert.Contains("C: 2048–4096 MB", revert.Message);
        Assert.Null(store.Value);
        var o = await memory.GetOverviewAsync(CancellationToken.None);
        Assert.False(o.AutomaticPageFile);
        Assert.Equal(new PageFileInfo("C:", 2048, 4096, false), o.PageFiles[0]);
    }

    [Fact]
    public void Page_file_snapshot_round_trips_and_rejects_unknown_text()
    {
        var overview = MemoryOverview.Empty() with
        {
            AutomaticPageFile = false,
            PageFiles = [new PageFileInfo("c:", 0, 0, true), new PageFileInfo("D:", 4096, 8192, false), new PageFileInfo("?:", 0, 0, true)],
        };
        var snapshot = PageFileSnapshot.From(overview);
        Assert.Equal("auto=0|C:=0-0|D:=4096-8192", snapshot.Format());
        Assert.Equal("C: system managed, D: 4096–8192 MB", snapshot.Describe());

        var parsed = PageFileSnapshot.Parse(snapshot.Format())!;
        Assert.False(parsed.Automatic);
        Assert.Equal([new PageFileInfo("C:", 0, 0, true), new PageFileInfo("D:", 4096, 8192, false)], parsed.PageFiles);

        // No page file at all is a real configuration and is restored as such.
        var none = PageFileSnapshot.Parse("auto=0")!;
        Assert.Empty(none.PageFiles);
        Assert.Equal("no page file", none.Describe());

        var automatic = PageFileSnapshot.From(MemoryOverview.Empty() with { AutomaticPageFile = true, PageFiles = [new PageFileInfo("C:", 0, 0, true)] });
        Assert.Equal("auto=1", automatic.Format());
        Assert.True(PageFileSnapshot.Parse("auto=1")!.Automatic);

        Assert.Null(PageFileSnapshot.Parse(null));
        Assert.Null(PageFileSnapshot.Parse(""));
        Assert.Null(PageFileSnapshot.Parse("auto=2"));
        Assert.Null(PageFileSnapshot.Parse("auto=0|C:=x-1"));
        Assert.Null(PageFileSnapshot.Parse("auto=0|CC=1-2"));
        Assert.Null(PageFileSnapshot.Parse("auto=0|C:=-1-2"));
    }

    private sealed class MemoryPageFileStore : IPageFileSnapshotStore
    {
        public string? Value { get; private set; }

        public string? Get() => Value;

        public void Set(string value) => Value = value;

        public void Remove() => Value = null;
    }

    [Fact]
    public async Task Provider_reports_unknown_compression_instead_of_guessing()
    {
        var overview = MemoryOverview.Empty() with { MemoryCompressionEnabled = true, MemoryCompressionKnown = false, SysMainEnabled = false };
        var provider = new MemoryActionsProvider(new FixedMemoryService(overview));
        var on = await provider.GetStateAsync("memory.compression.on", CancellationToken.None);
        var off = await provider.GetStateAsync("memory.compression.off", CancellationToken.None);
        Assert.Null(on.IsApplied);
        Assert.Null(off.IsApplied);
        Assert.Equal("Unknown", on.CurrentValue);
        Assert.False((await provider.GetStateAsync("memory.sysmain.on", CancellationToken.None)).IsApplied);
        Assert.True(MemoryOverview.Empty().MemoryCompressionKnown);
    }

    /// <summary>Returns one fixed overview; changes are not supported.</summary>
    private sealed class FixedMemoryService(MemoryOverview overview) : IMemoryService
    {
        public IReadOnlyList<MemoryProfile> Profiles => MemoryProfileCatalog.All;

        public MemoryOverview? Last => overview;

        public Task<MemoryOverview> GetOverviewAsync(CancellationToken cancellationToken) => Task.FromResult(overview);

        public Task<MemoryTestResult> RunMemoryTestAsync(int percentOfAvailable, int passes, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<OperationResult> ScheduleWindowsMemoryDiagnosticAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<OperationResult> ApplyProfileAsync(MemoryProfile profile, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<OperationResult> ClearStandbyListAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<OperationResult> SetPageFileAsync(string drive, bool systemManaged, int? initialMb, int? maxMb, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<OperationResult> SetMemoryCompressionAsync(bool enabled, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Provider_fits_the_optimization_service()
    {
        var provider = new MemoryActionsProvider(new DemoMemoryService());
        var svc = new DemoOptimizationService([provider]);
        Assert.Contains(svc.Actions, a => a.Id == "memory.standby.clear");
        Assert.True((await svc.ApplyAsync("memory.compression.on", CancellationToken.None)).Success);
    }
}

public class DemoMemoryServiceTests
{
    [Fact]
    public async Task Overview_describes_the_sample_kit_with_xmp_off()
    {
        var svc = new DemoMemoryService();
        var o = await svc.GetOverviewAsync(CancellationToken.None);
        Assert.Same(o, svc.Last);
        Assert.Equal(32L * 1024 * 1024 * 1024, o.TotalBytes);
        Assert.Equal(2, o.Modules.Count);
        Assert.Equal("Disabled", o.XmpState);
        Assert.Equal(4800, o.ConfiguredMhz);
        Assert.Equal(6000, o.RatedMhz);
        Assert.True(o.DualChannelLikely);
        Assert.Equal(4, o.Slots);
        Assert.Contains(o.Warnings, w => w.Contains("Enable XMP"));
        Assert.Contains("No errors", o.LastMemoryDiagnosticResult);
        Assert.InRange(o.UsedPercent, 30, 60);
    }

    [Fact]
    public async Task Profiles_and_page_file_changes_are_remembered()
    {
        var svc = new DemoMemoryService();
        var gaming = svc.Profiles.First(p => p.Id == MemoryProfileCatalog.GamingId);
        var result = await svc.ApplyProfileAsync(gaming, CancellationToken.None);
        Assert.True(result.Success);
        Assert.True(result.RebootRequired);
        var o = await svc.GetOverviewAsync(CancellationToken.None);
        Assert.False(o.AutomaticPageFile);
        Assert.Equal(PageFileRules.MaximumRecommendedMb, o.PageFiles[0].MaximumMb); // 1.5× of 32 GB is capped
        Assert.True(MemoryProfileCatalog.Matches(gaming, o));

        Assert.False((await svc.SetPageFileAsync("C:", false, 8192, 4096, CancellationToken.None)).Success);
        Assert.True((await svc.SetPageFileAsync("c:", true, null, null, CancellationToken.None)).Success);
        Assert.True((await svc.GetOverviewAsync(CancellationToken.None)).AutomaticPageFile);

        var scheduled = await svc.ScheduleWindowsMemoryDiagnosticAsync(CancellationToken.None);
        Assert.True(scheduled.RebootRequired);
        Assert.Contains("scheduled", (await svc.GetOverviewAsync(CancellationToken.None)).LastMemoryDiagnosticResult);
    }

    [Fact]
    public void Compression_change_asks_for_a_restart_only_when_the_state_changes()
    {
        var on = MemoryCompressionChange.Evaluate(true, before: false, after: true);
        Assert.True(on.Success);
        Assert.True(on.RebootRequired);
        Assert.Contains("restart", on.Message);

        var unknownBefore = MemoryCompressionChange.Evaluate(false, before: null, after: false);
        Assert.True(unknownBefore.Success);
        Assert.True(unknownBefore.RebootRequired);

        var already = MemoryCompressionChange.Evaluate(true, before: true, after: true);
        Assert.True(already.Success);
        Assert.False(already.RebootRequired);

        var refused = MemoryCompressionChange.Evaluate(false, before: true, after: true);
        Assert.False(refused.Success);
        Assert.Contains("still on", refused.Message);
    }

    [Theory]
    [InlineData("False\r\nTrue\r\n", false, true)]
    [InlineData("True\nTrue", true, true)]
    [InlineData("True", null, true)]
    [InlineData("", null, null)]
    [InlineData(null, null, null)]
    [InlineData("\nFalse\n", null, false)]
    public void Compression_change_output_is_parsed(string? output, bool? before, bool? after)
    {
        Assert.Equal((before, after), MemoryCompressionChange.ParseStates(output));
    }

    [Fact]
    public async Task Demo_compression_toggle_reports_restart_needed()
    {
        var svc = new DemoMemoryService();
        bool current = (await svc.GetOverviewAsync(CancellationToken.None)).MemoryCompressionEnabled;
        var changed = await svc.SetMemoryCompressionAsync(!current, CancellationToken.None);
        Assert.True(changed.Success);
        Assert.True(changed.RebootRequired);
        var same = await svc.SetMemoryCompressionAsync(!current, CancellationToken.None);
        Assert.True(same.Success);
        Assert.False(same.RebootRequired);
    }

    [Fact]
    public async Task Quick_test_runs_the_real_engine_on_a_small_buffer()
    {
        var svc = new DemoMemoryService();
        await svc.GetOverviewAsync(CancellationToken.None);
        var updates = new List<ProgressUpdate>();
        var result = await svc.RunMemoryTestAsync(10, 1, new ListProgress(updates), CancellationToken.None);
        Assert.True(result.Completed);
        Assert.Equal(0, result.Errors);
        Assert.True(result.BytesTested > 256L * 1024 * 1024);
        Assert.Contains("(demo)", result.Summary);
        Assert.NotEmpty(updates);
    }

    private sealed class ListProgress(List<ProgressUpdate> list) : IProgress<ProgressUpdate>
    {
        public void Report(ProgressUpdate value) => list.Add(value);
    }
}
