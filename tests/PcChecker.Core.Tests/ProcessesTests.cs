using PcChecker.Core.Common;
using PcChecker.Core.Processes;
using Xunit;

namespace PcChecker.Core.Tests;

/// <summary>Pure logic behind the Processes page: rates, catalogue, critical rules, flags, sorting and grouping.</summary>
public class ProcessesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private static ProcessInfo P(int pid, string name, double cpu = 0, long memoryMb = 10, string? path = null, double? gpu = null,
        SignatureInfo? signature = null, int parent = 1, DateTimeOffset? start = null, string? user = "Alex", double read = 0, double write = 0) =>
        new(pid, parent, name, path, user, start ?? T0.AddMinutes(-pid), cpu, memoryMb * ByteSize.Megabyte, read, write, gpu, ProcessPriority.Normal,
            signature ?? new SignatureInfo(SignatureState.Signed, "Contoso"));

    private static ProcessCounters C(int pid, TimeSpan cpu, long read = 0, long write = 0, DateTimeOffset? start = null) =>
        new(pid, 1, "app.exe", null, null, start ?? T0.AddHours(-1), cpu, 0, read, write, ProcessPriority.Normal);

    // ------------------------------------------------------------------ CPU and rates

    [Fact]
    public void Cpu_percent_is_a_share_of_all_logical_processors()
    {
        // 1 s of CPU time in 1 s on 4 logical processors = 25 % of the machine.
        Assert.Equal(25, ProcessMath.CpuPercent(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(11), TimeSpan.FromSeconds(1), 4), 3);
        Assert.Equal(100, ProcessMath.CpuPercent(TimeSpan.Zero, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(1), 4));
        Assert.Equal(0, ProcessMath.CpuPercent(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(1), 4));
        Assert.Equal(0, ProcessMath.CpuPercent(TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.Zero, 4));
    }

    [Fact]
    public void Rates_are_bytes_per_second_and_ignore_counter_resets()
    {
        Assert.Equal(1024, ProcessMath.RatePerSecond(1000, 1000 + 2048, TimeSpan.FromSeconds(2)), 3);
        Assert.Equal(0, ProcessMath.RatePerSecond(5000, 100, TimeSpan.FromSeconds(2)));
        Assert.Equal(0, ProcessMath.RatePerSecond(0, 100, TimeSpan.Zero));
        Assert.Equal("0 B/s", ProcessMath.FormatRate(0.4));
        Assert.Equal("1.50 MB/s", ProcessMath.FormatRate(1.5 * ByteSize.Megabyte));
    }

    [Fact]
    public void System_cpu_counts_idle_inside_kernel_time()
    {
        // kernel 600 (of which idle 500) + user 400 = 1000 total, busy 500 → 50 %.
        Assert.Equal(50, ProcessMath.SystemCpuPercent(500, 600, 400), 3);
        Assert.Equal(0, ProcessMath.SystemCpuPercent(0, 0, 0));
        Assert.Equal(100, ProcessMath.SystemCpuPercent(0, 300, 700), 3);
    }

    [Fact]
    public void Rate_tracker_reports_zero_first_then_deltas_and_treats_reused_ids_as_new()
    {
        var tracker = new ProcessRateTracker();
        var first = tracker.Update([C(10, TimeSpan.FromSeconds(1), read: 0, write: 0)], T0, 2);
        Assert.Equal(ProcessRates.Zero, first.Values.Single());

        var second = tracker.Update([C(10, TimeSpan.FromSeconds(2), read: 4096, write: 2048)], T0.AddSeconds(2), 2);
        var rates = second.Values.Single();
        Assert.Equal(25, rates.CpuPercent, 3);           // 1 s CPU over 2 s on 2 processors
        Assert.Equal(2048, rates.ReadBytesPerSecond, 3);
        Assert.Equal(1024, rates.WriteBytesPerSecond, 3);

        // Same pid, later start time: a different program; no delta against the old one.
        var reused = tracker.Update([C(10, TimeSpan.FromSeconds(50), start: T0)], T0.AddSeconds(4), 2);
        Assert.Equal(ProcessRates.Zero, reused.Values.Single());
        Assert.Equal(1, tracker.Count);
    }

    [Fact]
    public void Gpu_engines_are_summed_per_process_and_capped()
    {
        var sums = ProcessMath.SumGpuByPid(
        [
            new("pid_1234_luid_0x00000000_0x0000D1C5_phys_0_eng_0_engtype_3D", 40),
            new("pid_1234_luid_0x00000000_0x0000D1C5_phys_0_eng_5_engtype_VideoDecode", 5),
            new("pid_88_luid_0x00000000_0x0000D1C5_phys_0_eng_0_engtype_3D", 70),
            new("pid_88_luid_0x00000000_0x0000D1C5_phys_0_eng_2_engtype_Copy", 60),
            new("luid_0x00000000_0x0000D1C5_phys_0_eng_0_engtype_3D", 99),
            new("pid_7_luid_x_engtype_3D", double.NaN),
        ]);

        Assert.Equal(45, sums[1234], 3);
        Assert.Equal(100, sums[88], 3);
        Assert.False(sums.ContainsKey(7));
        Assert.Equal(2, sums.Count);
    }

    // ------------------------------------------------------------------ catalogue

    [Theory]
    [InlineData(@"C:\Windows\System32\svchost.exe", "svchost")]
    [InlineData("SvcHost.EXE", "svchost")]
    [InlineData("\"C:\\Program Files\\NVIDIA Corporation\\NVIDIA app\\CEF\\NVIDIA app.exe\"", "nvidia app")]
    [InlineData("MemCompression", "memcompression")]
    [InlineData("  ", "")]
    [InlineData(null, "")]
    public void Normalize_strips_folders_quotes_case_and_extension(string? input, string expected) =>
        Assert.Equal(expected, ProcessCatalog.Normalize(input));

    [Fact]
    public void Catalogue_is_large_unique_and_explains_well_known_processes()
    {
        Assert.True(ProcessCatalog.All.Count >= 80, $"Only {ProcessCatalog.All.Count} entries.");
        Assert.Equal(ProcessCatalog.All.Count, ProcessCatalog.All.Select(e => e.ExeName).Distinct().Count());
        Assert.All(ProcessCatalog.All, e =>
        {
            Assert.Equal(ProcessCatalog.Normalize(e.ExeName), e.ExeName);
            Assert.False(string.IsNullOrWhiteSpace(e.Description));
        });

        foreach (string name in new[] { "svchost.exe", "dwm.exe", "csrss.exe", "MsMpEng.exe", "explorer.exe", "SearchHost.exe", "steam.exe",
                     "nvcontainer.exe", "RadeonSoftware.exe", "igfxEM.exe", "Discord.exe", "chrome.exe", "firefox.exe", "msedge.exe" })
        {
            Assert.NotNull(ProcessCatalog.Lookup(name));
        }

        Assert.Equal("Memory compression", ProcessCatalog.Lookup("Memory Compression")?.Title);
        Assert.Equal("Opera", ProcessCatalog.Lookup("opera_gx.exe")?.Title);
        Assert.Null(ProcessCatalog.Lookup("totally-unknown-tool.exe"));
        Assert.Contains("Contoso", ProcessCatalog.Describe(null, "Contoso"));
    }

    // ------------------------------------------------------------------ critical processes

    [Theory]
    [InlineData("csrss.exe", 908)]
    [InlineData("wininit.exe", 1004)]
    [InlineData("smss.exe", 612)]
    [InlineData("LSASS.EXE", 1100)]
    [InlineData("services.exe", 1080)]
    [InlineData("winlogon.exe", 1144)]
    [InlineData("System", 4)]
    [InlineData("Registry", 140)]
    [InlineData("MemCompression", 2412)]
    [InlineData("anything.exe", 0)]
    public void Critical_processes_are_never_offered(string name, int pid) =>
        Assert.True(CriticalProcessRules.IsCritical(name, pid, ownPid: 9120));

    [Fact]
    public void The_app_itself_is_protected_but_ordinary_programs_are_not()
    {
        Assert.Contains("PC Checker", CriticalProcessRules.Reason("PcChecker.exe", 9120, ownPid: 9120));
        Assert.False(CriticalProcessRules.IsCritical("chrome.exe", 7020, ownPid: 9120));
        Assert.False(CriticalProcessRules.IsCritical("svchost.exe", 1300, ownPid: 9120));
        Assert.False(CriticalProcessRules.IsCritical(@"C:\Games\csrss-tool.exe", 5000, ownPid: 9120));
    }

    [Fact]
    public void Process_tree_is_deepest_first_and_skips_recycled_parent_ids()
    {
        var root = T0;
        var links = new[]
        {
            new ProcessLink(100, 1, root, "launcher.exe"),
            new ProcessLink(200, 100, root.AddSeconds(5), "child.exe"),
            new ProcessLink(300, 200, root.AddSeconds(9), "grandchild.exe"),
            new ProcessLink(400, 100, root.AddMinutes(-30), "orphan-with-reused-parent.exe"),
            new ProcessLink(500, 100, null, "unknown-start.exe"),
            new ProcessLink(600, 600, root, "self-parent.exe"),
        };

        var tree = ProcessTree.Descendants(100, root, links);

        Assert.Equal([300, 200], tree.Select(l => l.Pid));
    }

    // ------------------------------------------------------------------ suspicious heuristics

    [Fact]
    public void Unsigned_programs_in_temp_or_appdata_are_flagged()
    {
        var unsigned = new SignatureInfo(SignatureState.Unsigned, null);
        var temp = P(1, "dlhelper.exe", path: @"C:\Users\Alex\AppData\Local\Temp\dl_3f2a\dlhelper.exe", signature: unsigned);
        var signedInAppData = P(2, "Discord.exe", path: @"C:\Users\Alex\AppData\Local\Discord\app-1.0\Discord.exe");
        var pending = P(3, "new.exe", path: @"C:\Users\Alex\AppData\Local\Temp\new.exe", signature: SignatureInfo.Pending);
        var unsignedInProgramFiles = P(4, "tool.exe", path: @"C:\Program Files\Tool\tool.exe", signature: unsigned);

        Assert.Single(ProcessSuspicionRules.Evaluate(temp, null));
        Assert.Empty(ProcessSuspicionRules.Evaluate(signedInAppData, ProcessCatalog.Lookup("Discord.exe")));
        Assert.Empty(ProcessSuspicionRules.Evaluate(pending, null));
        Assert.Empty(ProcessSuspicionRules.Evaluate(unsignedInProgramFiles, null));
    }

    [Fact]
    public void Unknown_busy_programs_and_windows_lookalikes_are_flagged()
    {
        var unsigned = new SignatureInfo(SignatureState.Unsigned, null);
        var busyUnknown = P(1, "x.exe", cpu: 40, path: @"D:\Apps\x.exe", signature: unsigned);
        var busyGpu = P(2, "y.exe", gpu: 85, path: @"D:\Apps\y.exe", signature: unsigned);
        var busyKnownGame = P(3, "steam.exe", cpu: 40, path: @"C:\Program Files (x86)\Steam\steam.exe", signature: unsigned);
        var busySigned = P(4, "render.exe", cpu: 90, path: @"D:\Apps\render.exe");
        var fakeSvchost = P(5, "svchost.exe", path: @"C:\Users\Alex\Music\svchost.exe");
        var realSvchost = P(6, "svchost.exe", path: @"C:\Windows\System32\svchost.exe");
        var registry = P(7, "Registry", path: "Registry");

        Assert.Contains("processor", Assert.Single(ProcessSuspicionRules.Evaluate(busyUnknown, null)));
        Assert.Contains("graphics card", Assert.Single(ProcessSuspicionRules.Evaluate(busyGpu, null)));
        Assert.Empty(ProcessSuspicionRules.Evaluate(busyKnownGame, ProcessCatalog.Lookup("steam.exe")));
        Assert.Empty(ProcessSuspicionRules.Evaluate(busySigned, null));
        Assert.Contains("Windows program", Assert.Single(ProcessSuspicionRules.Evaluate(fakeSvchost, ProcessCatalog.Lookup("svchost"), @"C:\Windows")));
        Assert.Empty(ProcessSuspicionRules.Evaluate(realSvchost, ProcessCatalog.Lookup("svchost"), @"c:\windows\"));
        Assert.Empty(ProcessSuspicionRules.Evaluate(registry, ProcessCatalog.Lookup("Registry")));
    }

    // ------------------------------------------------------------------ sorting and grouping

    [Fact]
    public void Sorting_works_for_numbers_text_and_missing_values()
    {
        var list = new[]
        {
            P(30, "b.exe", cpu: 5, memoryMb: 300, gpu: null, user: null),
            P(10, "a.exe", cpu: 50, memoryMb: 100, gpu: 10),
            P(20, "c.exe", cpu: 5, memoryMb: 200, gpu: 80, user: "Zed"),
        };

        Assert.Equal([10, 30, 20], ProcessSorting.Sort(list, ProcessSortColumn.Cpu, true).Select(p => p.Pid)); // tie → name (b before c)
        Assert.Equal([30, 20, 10], ProcessSorting.Sort(list, ProcessSortColumn.Memory, true).Select(p => p.Pid));
        Assert.Equal([10, 30, 20], ProcessSorting.Sort(list, ProcessSortColumn.Name, false).Select(p => p.Pid));
        Assert.Equal([20, 10, 30], ProcessSorting.Sort(list, ProcessSortColumn.Gpu, true).Select(p => p.Pid));
        Assert.Equal(30, ProcessSorting.Sort(list, ProcessSortColumn.User, false).Last().Pid); // unknown user last
        Assert.True(ProcessSorting.TryParseColumn("memory", out var column) && column == ProcessSortColumn.Memory);
        Assert.False(ProcessSorting.TryParseColumn("Nonsense", out _));
        Assert.True(ProcessSorting.DefaultDescending(ProcessSortColumn.Cpu));
        Assert.False(ProcessSorting.DefaultDescending(ProcessSortColumn.Name));
    }

    [Fact]
    public void Grouping_by_app_sums_members_and_expands_on_request()
    {
        const string ChromePath = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
        var list = new[]
        {
            P(1, "chrome.exe", cpu: 2, memoryMb: 100, path: ChromePath, gpu: 1),
            P(2, "chrome.exe", cpu: 3, memoryMb: 200, path: ChromePath, gpu: 2),
            P(3, "chrome.exe", cpu: 1, memoryMb: 50, path: ChromePath.ToUpperInvariant()),
            P(4, "game.exe", cpu: 4, memoryMb: 5000, path: @"D:\Games\game.exe"),
            P(5, "Update.exe", cpu: 0, memoryMb: 5, path: @"C:\Users\Alex\AppData\Local\Discord\Update.exe"),
            P(6, "Update.exe", cpu: 0, memoryMb: 5, path: @"C:\Users\Alex\AppData\Local\Teams\Update.exe"),
        };

        var groups = ProcessSorting.GroupByApp(list);
        var chrome = Assert.Single(groups, g => g.Members.Count == 3);
        Assert.Equal("Google Chrome", chrome.Title);
        Assert.Equal(6, chrome.CpuPercent, 3);
        Assert.Equal(350 * ByteSize.Megabyte, chrome.PrivateBytes);
        Assert.Equal(3, chrome.GpuPercent);
        Assert.Equal(3, chrome.Lead.Pid); // started first (oldest start time)
        Assert.Equal(4, groups.Count);    // the two Update.exe files are different programs

        var collapsed = ProcessSorting.Arrange(list, groupByApp: true, ProcessSortColumn.Cpu, descending: true);
        Assert.True(collapsed[0].IsGroup);      // Chrome total 6 % beats game 4 %
        Assert.Equal(4, collapsed.Count);       // header + 3 lone processes

        var expanded = ProcessSorting.Arrange(list, true, ProcessSortColumn.Cpu, true, new HashSet<string> { chrome.Key });
        Assert.Equal(7, expanded.Count);
        Assert.Equal([2, 1, 3], expanded.Skip(1).Take(3).Select(e => e.Process!.Pid));
        Assert.All(expanded.Skip(1).Take(3), e => Assert.True(e.IsChild));

        var flat = ProcessSorting.Arrange(list, groupByApp: false, ProcessSortColumn.Memory, descending: true);
        Assert.Equal(6, flat.Count);
        Assert.Equal(4, flat[0].Process!.Pid);
    }

    // ------------------------------------------------------------------ priority, signatures, text

    [Fact]
    public void Realtime_priority_needs_advanced_mode_and_maps_to_windows_values()
    {
        Assert.DoesNotContain(ProcessPriority.Realtime, PriorityRules.Available(advancedMode: false));
        Assert.Contains(ProcessPriority.Realtime, PriorityRules.Available(advancedMode: true));
        Assert.False(PriorityRules.IsAllowed(ProcessPriority.Realtime, false));
        Assert.True(PriorityRules.IsAllowed(ProcessPriority.High, false));
        Assert.NotNull(PriorityRules.Warning(ProcessPriority.Realtime));
        Assert.Null(PriorityRules.Warning(ProcessPriority.Normal));
        foreach (var priority in Enum.GetValues<ProcessPriority>())
        {
            Assert.Equal(priority, PriorityRules.FromPriorityClass(PriorityRules.ToPriorityClass(priority)));
        }

        Assert.Null(PriorityRules.FromPriorityClass(0));
    }

    [Fact]
    public void Signature_results_are_classified_and_cached()
    {
        Assert.Equal(SignatureState.Signed, SignatureRules.Classify(0));
        Assert.Equal(SignatureState.Unsigned, SignatureRules.Classify(SignatureRules.TrustENoSignature));
        Assert.Equal(SignatureState.Invalid, SignatureRules.Classify(SignatureRules.TrustEBadDigest));
        Assert.Equal(SignatureState.Unknown, SignatureRules.Classify(SignatureRules.CryptEFileError));
        Assert.Equal("Valve Corp.", SignatureRules.CommonName("CN=Valve Corp., O=Valve Corp., L=Bellevue, C=US"));
        Assert.Equal("Acme, Inc.", SignatureRules.CommonName("O=Acme, CN=\"Acme, Inc.\", C=US"));

        int calls = 0;
        var cache = new SignatureCache(path =>
        {
            calls++;
            return path.Contains("bad") ? throw new IOException("locked") : new SignatureInfo(SignatureState.Signed, "Contoso");
        });
        string[] paths = [@"C:\a.exe", @"C:\A.EXE", @"C:\b.exe", @"C:\bad.exe", "Registry", @"C:\c.exe"];

        Assert.Equal(SignatureState.Pending, cache.Get(@"C:\a.exe").State);
        Assert.Equal(SignatureState.Unknown, cache.Get("Registry").State);
        Assert.Equal(2, cache.CheckPending(paths, maxChecks: 2));
        Assert.Equal(SignatureState.Signed, cache.Get(@"c:\A.exe").State);
        Assert.Equal(SignatureState.Pending, cache.Get(@"C:\bad.exe").State);
        Assert.Equal(2, cache.CheckPending(paths, maxChecks: 10));
        Assert.Equal(SignatureState.Unknown, cache.Get(@"C:\bad.exe").State);
        Assert.Equal(4, calls);
    }

    [Fact]
    public void Summary_details_and_search_link_are_plain_text()
    {
        var game = P(8840, "Cyberpunk2077.exe", cpu: 21.4, memoryMb: 7860, path: @"D:\Games\Cyberpunk2077.exe", gpu: 92);
        var self = P(9120, "PcChecker.exe", cpu: 60);
        var kernel = P(4, "System", cpu: 80);
        var snapshot = new ProcessSnapshot(T0, [game, self, kernel, P(10, "idle-ish.exe")], 35, 16 * ByteSize.Gigabyte, 32 * ByteSize.Gigabyte, true, []);

        var summary = ProcessText.Summarize(snapshot, ownPid: 9120);
        Assert.Equal(4, summary.Count);
        Assert.Equal(8840, summary.TopConsumer?.Pid);
        Assert.Equal(50, summary.MemoryPercent, 3);

        string details = ProcessText.Details(game, null, ["It is busy."]);
        Assert.Contains("Process ID: 8840", details);
        Assert.Contains(@"File: D:\Games\Cyberpunk2077.exe", details);
        Assert.Contains("Contoso (signed)", details);
        Assert.Contains("Note: It is busy.", details);

        Assert.Equal("https://duckduckgo.com/?q=%22steamwebhelper.exe%22%20process", ProcessText.SearchUrl(@"C:\Steam\steamwebhelper.exe"));
        Assert.Equal("Checking…", ProcessText.SignatureLabel(SignatureInfo.Pending));
        Assert.Equal("2 h 5 min", ProcessText.Age(T0.AddMinutes(-125), T0));
        Assert.Equal("3 days", ProcessText.Age(T0.AddDays(-3), T0));
    }

    [Fact]
    public async Task Demo_service_lists_processes_and_refuses_critical_ones()
    {
        var demo = new DemoProcessService();
        var snapshot = await demo.SampleAsync(CancellationToken.None);
        Assert.True(snapshot.Processes.Count >= 40);
        Assert.True(snapshot.GpuAvailable);
        Assert.Contains(snapshot.Processes, p => ProcessSuspicionRules.Evaluate(p, ProcessCatalog.Lookup(p.Name)).Count > 0);

        var csrss = snapshot.Processes.First(p => p.Name == "csrss.exe");
        Assert.False((await demo.EndProcessAsync(csrss, false, CancellationToken.None)).Success);
        var self = snapshot.Processes.First(p => p.Pid == demo.OwnProcessId);
        Assert.False((await demo.EndProcessAsync(self, false, CancellationToken.None)).Success);

        var steam = snapshot.Processes.First(p => p.Name == "steam.exe");
        Assert.True((await demo.EndProcessAsync(steam, includeChildren: true, CancellationToken.None)).Success);
        var after = await demo.SampleAsync(CancellationToken.None);
        Assert.DoesNotContain(after.Processes, p => p.Name is "steam.exe" or "steamwebhelper.exe");

        var chrome = after.Processes.First(p => p.Name == "chrome.exe");
        Assert.False((await demo.SetPriorityAsync(chrome, ProcessPriority.Realtime, allowRealtime: false, CancellationToken.None)).Success);
        Assert.True((await demo.SetPriorityAsync(chrome, ProcessPriority.BelowNormal, allowRealtime: false, CancellationToken.None)).Success);
    }
}
