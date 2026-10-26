using PcChecker.Core.Assistant;
using PcChecker.Core.Common;
using PcChecker.Core.Hardware;
using PcChecker.Core.Health;
using PcChecker.Core.Overclocking;
using PcChecker.Core.Performance;
using PcChecker.Core.Sensors;
using PcChecker.Core.Software;
using Xunit;

namespace PcChecker.Core.Tests;

public class AssistantContextBuilderTests
{
    private static HardwareInventory Inventory()
    {
        var props = HardwareItem.NoProperties;
        return new HardwareInventory
        {
            System = new ComputerSystemInfo("PC", "Contoso", "Model X", "x64", null, null, 32L * 1024 * 1024 * 1024, 1, 16, null, null, "Desktop", null, props),
            Processors = [new CpuInfo("Ryzen 7", "AMD", "CPU0", "OK", null, "x64", "AM5", 8, 16, 5000, 4000, null, null, null, null, null, true, null, 64, props)],
            Graphics = [new GpuInfo("GeForce RTX 4070", "NVIDIA", "GPU0", "OK", 12L * 1024 * 1024 * 1024, "551.23", null, null, null, 2560, 1440, 165, null, "NVIDIA", false, props)],
            PhysicalDisks = [new PhysicalDiskInfo("NVMe", "Samsung", "D0", "OK", "980 PRO", "S6B2NS0R123456", "1B2QGXA7", "NVMe", "SSD", "NVMe", 1_000_000_000_000, "Healthy", null, null, [], props)],
            NetworkAdapters = [new NetworkAdapterInfo("Ethernet", "Intel", "N0", "OK", "AA:BB:CC:DD:EE:FF", "Ethernet 802.3", 1_000_000_000, true, "Ethernet", "Connected", [], [], [], true, null, props)],
        };
    }

    [Fact]
    public void Builds_all_sections_from_data()
    {
        var hw = Inventory();
        var health = new HealthReport(85, [new HealthItem("Disk", HealthStatus.Warning, "Warm drive", "58 °C", null, HealthItem.NoMetrics)], [], [], [], [], [], [], DateTimeOffset.Now, []);
        var perf = new PerformanceReport(70, [new SlowdownFinding("x", FindingCategory.Memory, Severity.High, "RAM high", "90%", "", [], false)], [], [], [], 30, 90, 16L * 1024 * 1024 * 1024, "Balanced", [], DateTimeOffset.Now, []);
        var sensors = new SensorSnapshot(DateTimeOffset.Now, [new SensorReading("cpu/t", "CPU", HardwareKind.Cpu, SensorKind.Temperature, "Package", 65, 40, 80, "°C")]);
        var software = new SoftwareInventory([InstalledProgram.Create("a", "Big App", sizeBytes: 5_000_000_000)], [], DateTimeOffset.Now, []);

        var ctx = AssistantContextBuilder.Build(hw, health, perf, sensors, software, 12000, redactSerials: false);
        Assert.False(ctx.IsEmpty);
        Assert.Contains("Ryzen 7", ctx.HardwareSummary);
        Assert.Contains("S6B2NS0R123456", ctx.HardwareSummary);
        Assert.Contains("AA:BB:CC:DD:EE:FF", ctx.HardwareSummary);
        Assert.Contains("85/100", ctx.HealthSummary);
        Assert.Contains("Warm drive", ctx.HealthSummary);
        Assert.Contains("RAM high", ctx.PerformanceSummary);
        Assert.Contains("Package 65", ctx.SensorSummary);
        Assert.Contains("Big App", ctx.SoftwareSummary);
    }

    [Fact]
    public void Redacts_serials_and_mac_addresses()
    {
        var ctx = AssistantContextBuilder.Build(Inventory(), null, null, null, null, 12000, redactSerials: true);
        Assert.DoesNotContain("S6B2NS0R123456", ctx.HardwareSummary);
        Assert.DoesNotContain("AA:BB:CC:DD:EE:FF", ctx.HardwareSummary);
        Assert.Contains("980 PRO", ctx.HardwareSummary);
        Assert.Null(ctx.HealthSummary);
    }

    [Fact]
    public void Redact_masks_mac_in_free_text()
    {
        var text = AssistantContextBuilder.Redact("MAC 00-11-22-33-44-55 and AA:BB:CC:DD:EE:FF");
        Assert.DoesNotContain("00-11-22-33-44-55", text);
        Assert.DoesNotContain("AA:BB:CC:DD:EE:FF", text);
        Assert.Contains("00-11", text);
    }

    [Fact]
    public void Respects_character_budget()
    {
        var hw = Inventory();
        for (int i = 0; i < 300; i++)
        {
            hw.PnpDevices.Add(new PnpDeviceInfo($"Device {i}", null, $"ID{i}", "OK", null, null, null, null, 0, true, [], HardwareItem.NoProperties));
            hw.UsbDevices.Add(new UsbDeviceInfo($"USB thing number {i} with a long descriptive name", "Vendor", $"USB{i}", "OK", null, null, null, null, HardwareItem.NoProperties));
        }

        var ctx = AssistantContextBuilder.Build(hw, null, null, null, null, 1000, false);
        Assert.NotNull(ctx.HardwareSummary);
        Assert.True(ctx.HardwareSummary.Length <= 1000 * 35 / 100 + 10);
    }

    [Fact]
    public void Empty_inputs_give_empty_context()
    {
        var ctx = AssistantContextBuilder.Build(null, null, null, null, null, 12000, true);
        Assert.True(ctx.IsEmpty);
        Assert.Null(AssistantContextBuilder.SummarizeSensors(SensorSnapshot.Empty));
    }

    [Fact]
    public void Hardware_profile_json_includes_gpu_cpu_and_capabilities()
    {
        var caps = new OverclockCapabilities(GpuVendor.Nvidia, "RTX", GpuControlMethod.NvApi, new GpuLimits(-200, 200, -1000, 1000, 50, 115, 60, 90, true), "CPU", CpuControlMethod.None, null, "Enabled", 6000, 6000, ["note"]);
        var json = AssistantContextBuilder.BuildHardwareProfileJson(Inventory(), null, caps);
        Assert.Contains("\"Name\":\"GeForce RTX 4070\"", json);
        Assert.Contains("\"Cores\":8", json);
        Assert.Contains("\"GpuMethod\":\"NvApi\"", json);
        Assert.Contains("\"XmpState\":\"Enabled\"", json);
        Assert.DoesNotContain("S6B2NS0R123456", json);
    }

    [Fact]
    public void Profile_json_handles_missing_data()
    {
        var json = AssistantContextBuilder.BuildHardwareProfileJson(null, null, null);
        Assert.Contains("\"gpus\":[]", json);
    }

    [Fact]
    public void Health_summary_lists_disks_batteries_and_redacts_serials()
    {
        var disk = new DiskHealth("980 PRO", "S6B2NS0R123456", "1B2Q", 1_000_000_000_000, "SSD", HealthStatus.Good, "OK", 41, 1200, 0, 0, 0, 3, 8000, []);
        var battery = new BatteryHealth("Main", 60000, 48000, 20, 310, "Li-ion", "Discharging", 77);
        var report = new HealthReport(90, [], [disk], [battery], [], [], [], [], DateTimeOffset.Now, []);

        var plain = AssistantContextBuilder.SummarizeHealth(report, redactSerials: false)!;
        Assert.Contains("90/100 (Good)", plain);
        Assert.Contains("S6B2NS0R123456", plain);
        Assert.Contains("wear 3%", plain);
        Assert.Contains("Battery Main: wear 20%, cycles 310", plain);

        var redacted = AssistantContextBuilder.SummarizeHealth(report, redactSerials: true)!;
        Assert.DoesNotContain("S6B2NS0R123456", redacted);
        Assert.Contains("980 PRO", redacted);
    }

    [Fact]
    public void Performance_summary_includes_startup_items_and_top_processes()
    {
        var report = new PerformanceReport(55,
            [new SlowdownFinding("f", FindingCategory.Startup, Severity.Medium, "Many startup apps", "12 enabled", "", [], false)],
            [new StartupItem("Updater", "u.exe", "HKCU\\Run", true, "Contoso", "Registry"), new StartupItem("Off", "o.exe", "Folder", false, null, "Folder")],
            [],
            [new ProcessInfo(1, "chrome", 12.4, 2_000_000_000, null, "Google")],
            42, 75, 16L * 1024 * 1024 * 1024, "Balanced", [], DateTimeOffset.Now, []);

        var text = AssistantContextBuilder.SummarizePerformance(report)!;
        Assert.Contains("Performance score: 55/100", text);
        Assert.Contains("Last boot 42 s", text);
        Assert.Contains("Many startup apps", text);
        Assert.Contains("Startup programs (1): Updater", text);
        Assert.DoesNotContain("Off", text.Split("Startup programs")[1].Split('\n')[0]);
        Assert.Contains("chrome 12%", text);
    }

    [Fact]
    public void Software_summary_skips_system_components_and_lists_bundles()
    {
        var parent = InstalledProgram.Create("p", "Parent Suite", sizeBytes: 100);
        var child = InstalledProgram.Create("c", "Toolbar", sizeBytes: 10);
        var system = InstalledProgram.Create("s", "Hidden Runtime", sizeBytes: 999) with { IsSystemComponent = true };
        var inventory = new SoftwareInventory([parent, child, system], [new BundleGroup(parent, [child], "installed together")], DateTimeOffset.Now, []);

        var text = AssistantContextBuilder.SummarizeSoftware(inventory)!;
        Assert.Contains("3 programs installed", text);
        Assert.DoesNotContain("Hidden Runtime", text);
        Assert.Contains("Parent Suite → Toolbar", text);
    }

    [Fact]
    public void Sensor_summary_groups_by_hardware_and_marks_temperature_max()
    {
        var snapshot = new SensorSnapshot(DateTimeOffset.Now,
        [
            new SensorReading("g/t", "GPU", HardwareKind.Gpu, SensorKind.Temperature, "Core", 61.5, 40, 78, "°C"),
            new SensorReading("g/l", "GPU", HardwareKind.Gpu, SensorKind.Load, "Core", 97, 0, 100, "%"),
            new SensorReading("g/v", "GPU", HardwareKind.Gpu, SensorKind.Voltage, "Core", 1.05, null, null, "V"),
            new SensorReading("c/t", "CPU", HardwareKind.Cpu, SensorKind.Temperature, "Package", null, null, null, "°C"),
        ]);

        var text = AssistantContextBuilder.SummarizeSensors(snapshot)!;
        Assert.Contains("GPU: Core 61.5°C (max 78), Core 97%", text);
        Assert.DoesNotContain("1.05", text);
        Assert.DoesNotContain("CPU", text);
    }
}

public class AssistantPromptsTests
{
    [Fact]
    public void Prompt_constants_are_non_empty_and_free_of_secrets()
    {
        Assert.False(string.IsNullOrWhiteSpace(PcChecker.Claude.AssistantPrompts.System));
        Assert.False(string.IsNullOrWhiteSpace(PcChecker.Claude.AssistantPrompts.OverclockAdvisorSystem));
        Assert.DoesNotContain("sk-ant-", PcChecker.Claude.AssistantPrompts.System);
        Assert.DoesNotContain("sk-ant-", PcChecker.Claude.AssistantPrompts.OverclockAdvisorSystem);
        Assert.Contains("PC Checker", PcChecker.Claude.AssistantPrompts.System);
        Assert.Contains("JSON", PcChecker.Claude.AssistantPrompts.OverclockAdvisorSystem);
    }

    [Fact]
    public void Context_block_lists_only_sections_with_text()
    {
        var ctx = new AssistantContext("CPU: Ryzen 7", null, "Score 70", null, null);
        var block = PcChecker.Claude.AssistantPrompts.BuildContextBlock(ctx);
        Assert.Contains("### Hardware", block);
        Assert.Contains("CPU: Ryzen 7", block);
        Assert.Contains("### Performance", block);
        Assert.DoesNotContain("### Health", block);
        Assert.DoesNotContain("### Software", block);
        Assert.Equal(string.Empty, PcChecker.Claude.AssistantPrompts.BuildContextBlock(null));
        Assert.Equal(string.Empty, PcChecker.Claude.AssistantPrompts.BuildContextBlock(new AssistantContext(null, null, null, null, null)));
    }

    [Fact]
    public void Chat_system_text_appends_context_after_prompt()
    {
        var plain = PcChecker.Claude.AssistantPrompts.BuildChatSystemText(null);
        Assert.Equal(PcChecker.Claude.AssistantPrompts.System, plain);
        var withContext = PcChecker.Claude.AssistantPrompts.BuildChatSystemText(new AssistantContext("hw", null, null, null, null));
        Assert.StartsWith(PcChecker.Claude.AssistantPrompts.System, withContext);
        Assert.EndsWith("hw", withContext);
    }

    [Fact]
    public void Explain_prompt_contains_subject_details_and_word_limit()
    {
        var text = PcChecker.Claude.AssistantPrompts.BuildExplainPrompt("Reallocated sectors", "Raw value 12");
        Assert.Contains("Reallocated sectors", text);
        Assert.Contains("Raw value 12", text);
        Assert.Contains("150 words", text);
        Assert.Contains("this finding", PcChecker.Claude.AssistantPrompts.BuildExplainPrompt("", ""));
    }

    [Fact]
    public void Overclock_request_includes_profile_limits_goal_and_previous_results()
    {
        var caps = new OverclockCapabilities(GpuVendor.Nvidia, "RTX 4070", GpuControlMethod.NvApi, new GpuLimits(-200, 200, -1000, 1000, 50, 115, 60, 90, true), "i7", CpuControlMethod.None, null, "Disabled", 4800, 6000, ["XMP off"]);
        var limits = new PcChecker.Core.Settings.OverclockSettings { MaxGpuCoreOffsetMhz = 150, MaxGpuTempLimitC = 85 };
        var request = new OverclockAdvisorRequest("{\"gpu\":\"RTX 4070\"}", caps, limits, "quiet gaming", "Step 1 passed 300 s at 71 °C");
        var text = PcChecker.Claude.AssistantPrompts.BuildOverclockRequest(request);
        Assert.Contains("quiet gaming", text);
        Assert.Contains("{\"gpu\":\"RTX 4070\"}", text);
        Assert.Contains("\"maxGpuCoreOffsetMhz\":150", text);
        Assert.Contains("\"maxGpuTempLimitC\":85", text);
        Assert.Contains("\"GpuMethod\":\"NvApi\"", text);
        Assert.Contains("Step 1 passed", text);
        Assert.Contains("JSON only", text);
    }

    [Fact]
    public void Refusal_message_is_friendly_with_or_without_explanation()
    {
        Assert.Contains("declined", PcChecker.Claude.AssistantPrompts.RefusalMessage(null));
        Assert.Contains("because of policy", PcChecker.Claude.AssistantPrompts.RefusalMessage("because of policy"));
    }
}

public class OverclockPlanSchemaTests
{
    [Fact]
    public void Schema_is_valid_json_with_required_top_level_fields()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(PcChecker.Claude.OverclockPlanSchema.SchemaJson);
        var root = doc.RootElement;
        Assert.Equal("object", root.GetProperty("type").GetString());
        var required = root.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("steps", required);
        Assert.Contains("disclaimer", required);
        var target = root.GetProperty("properties").GetProperty("steps").GetProperty("items").GetProperty("properties").GetProperty("target");
        Assert.False(target.GetProperty("additionalProperties").GetBoolean());
        Assert.True(target.GetProperty("properties").TryGetProperty("gpuCoreOffsetMhz", out _));
    }

    [Fact]
    public void Schema_dictionary_has_every_top_level_key()
    {
        var dict = PcChecker.Claude.OverclockPlanSchema.AsDictionary();
        Assert.Contains("type", dict.Keys);
        Assert.Contains("properties", dict.Keys);
        Assert.Contains("required", dict.Keys);
        Assert.Equal(System.Text.Json.JsonValueKind.Object, dict["properties"].ValueKind);
    }

    [Fact]
    public void Plan_parser_accepts_output_shaped_like_the_schema()
    {
        const string json = """{"summary":"s","steps":[{"order":1,"title":"t","rationale":"r","target":{"gpuCoreOffsetMhz":50,"gpuMemoryOffsetMhz":0,"gpuPowerLimitPercent":105,"gpuTempLimitC":null,"gpuFanPercent":null,"cpuAllCoreRatio":null,"cpuVoltageOffsetMv":null,"cpuPowerLimitW":null,"notes":null},"stressSeconds":300,"abortIf":"crash","expectedGain":"2%"}],"warnings":[],"assumptions":[],"disclaimer":"d"}""";
        var plan = OverclockPlanParser.TryParse(json);
        Assert.NotNull(plan);
        Assert.Equal(50, plan.Steps[0].Target.GpuCoreOffsetMhz);
        Assert.Null(plan.Steps[0].Target.GpuTempLimitC);
    }
}

public class AssistantErrorMapperTests
{
    private static System.Net.Http.HttpRequestException Http(System.Net.HttpStatusCode code) => new("boom", null, code);

    [Fact]
    public void Maps_unauthorized_and_rate_limit()
    {
        Assert.Equal(PcChecker.Claude.AssistantErrorMapper.ApiKeyRejected,
            PcChecker.Claude.AssistantErrorMapper.Describe(new Anthropic.Exceptions.AnthropicUnauthorizedException(Http(System.Net.HttpStatusCode.Unauthorized)) { StatusCode = System.Net.HttpStatusCode.Unauthorized, ResponseBody = "" }));
        Assert.Equal(PcChecker.Claude.AssistantErrorMapper.RateLimited,
            PcChecker.Claude.AssistantErrorMapper.Describe(new Anthropic.Exceptions.AnthropicRateLimitException(Http(System.Net.HttpStatusCode.TooManyRequests)) { StatusCode = System.Net.HttpStatusCode.TooManyRequests, ResponseBody = "" }));
    }

    [Fact]
    public void Maps_other_4xx_and_5xx_with_status()
    {
        var notFound = new Anthropic.Exceptions.Anthropic4xxException(Http(System.Net.HttpStatusCode.NotFound)) { StatusCode = System.Net.HttpStatusCode.NotFound, ResponseBody = "" };
        Assert.Contains("404", PcChecker.Claude.AssistantErrorMapper.Describe(notFound));
        var server = new Anthropic.Exceptions.Anthropic5xxException(Http(System.Net.HttpStatusCode.BadGateway)) { StatusCode = System.Net.HttpStatusCode.BadGateway, ResponseBody = "" };
        Assert.Contains("502", PcChecker.Claude.AssistantErrorMapper.Describe(server));
    }

    [Fact]
    public void Network_errors_mean_offline()
    {
        var socket = new System.Net.Sockets.SocketException(10061);
        var http = new System.Net.Http.HttpRequestException("no route", socket);
        Assert.True(PcChecker.Claude.AssistantErrorMapper.IsOffline(http));
        Assert.Equal(PcChecker.Claude.AssistantErrorMapper.Offline, PcChecker.Claude.AssistantErrorMapper.Describe(http));
        Assert.Equal(PcChecker.Claude.AssistantErrorMapper.Offline, PcChecker.Claude.AssistantErrorMapper.Describe(new Anthropic.Exceptions.AnthropicIOException("io", http)));
        Assert.False(PcChecker.Claude.AssistantErrorMapper.IsOffline(new InvalidOperationException("x")));
    }

    [Fact]
    public void Cancellation_and_timeouts_are_distinguished()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Equal(PcChecker.Claude.AssistantErrorMapper.Cancelled, PcChecker.Claude.AssistantErrorMapper.Describe(new OperationCanceledException(cts.Token)));
        Assert.Equal(PcChecker.Claude.AssistantErrorMapper.TimedOut, PcChecker.Claude.AssistantErrorMapper.Describe(new TaskCanceledException("timeout")));
    }

    [Fact]
    public void Unknown_exceptions_keep_their_message_but_are_trimmed()
    {
        var text = PcChecker.Claude.AssistantErrorMapper.Describe(new InvalidOperationException(new string('x', 500)));
        Assert.Contains("Something went wrong", text);
        Assert.True(text.Length < 320);
        Assert.NotNull(PcChecker.Claude.AssistantErrorMapper.Describe(null));
    }
}

public class ClaudeRequestBuilderTests
{
    [Fact]
    public void Merges_same_role_messages_and_drops_blanks()
    {
        var turns = PcChecker.Claude.ClaudeRequestBuilder.NormalizeHistory(
        [
            AssistantMessage.User("first"),
            AssistantMessage.User("  "),
            AssistantMessage.User("second"),
            AssistantMessage.Assistant("reply"),
            AssistantMessage.User("third"),
        ]);
        Assert.Equal(3, turns.Count);
        Assert.Equal("first\n\nsecond", turns[0].Text);
        Assert.False(turns[1].IsUser);
        Assert.True(PcChecker.Claude.ClaudeRequestBuilder.CanSend(turns));
    }

    [Fact]
    public void Leading_assistant_messages_are_removed_and_assistant_last_cannot_send()
    {
        var turns = PcChecker.Claude.ClaudeRequestBuilder.NormalizeHistory([AssistantMessage.Assistant("hello"), AssistantMessage.User("q"), AssistantMessage.Assistant("a")]);
        Assert.Equal(2, turns.Count);
        Assert.True(turns[0].IsUser);
        Assert.False(PcChecker.Claude.ClaudeRequestBuilder.CanSend(turns));
        Assert.Empty(PcChecker.Claude.ClaudeRequestBuilder.NormalizeHistory(null));
        Assert.False(PcChecker.Claude.ClaudeRequestBuilder.CanSend([]));
    }

    [Fact]
    public void Long_history_is_trimmed_from_the_start_keeping_alternation()
    {
        var history = new List<AssistantMessage>();
        for (int i = 0; i < 20; i++)
        {
            history.Add(AssistantMessage.User(new string('u', 500) + i));
            history.Add(AssistantMessage.Assistant(new string('a', 500) + i));
        }

        history.Add(AssistantMessage.User("latest"));
        var turns = PcChecker.Claude.ClaudeRequestBuilder.NormalizeHistory(history, maxChars: 3000);
        Assert.True(turns.Count < 41);
        Assert.True(turns[0].IsUser);
        Assert.Equal("latest", turns[^1].Text);
        Assert.True(turns.Sum(t => t.Text.Length) <= 3000 + 1100);
    }

    [Fact]
    public void Clamp_plan_reduces_out_of_range_steps_and_adds_warning()
    {
        var caps = new OverclockCapabilities(GpuVendor.Nvidia, "RTX", GpuControlMethod.NvApi, null, null, CpuControlMethod.None, null, "Unknown", null, null, []);
        var limits = new PcChecker.Core.Settings.OverclockSettings { MaxGpuCoreOffsetMhz = 100 };
        var request = new OverclockAdvisorRequest("{}", caps, limits, null, null);
        var plan = new OverclockPlan("s", [new(2, "big", "r", new OverclockProfile { GpuCoreOffsetMhz = 400 }, 10, "a", "g"), new(1, "ok", "r", new OverclockProfile { GpuCoreOffsetMhz = 50 }, 300, "a", "g")], ["keep"], [], "d");
        var clamped = PcChecker.Claude.ClaudeAssistantService.ClampPlan(plan, request);
        Assert.Equal("ok", clamped.Steps[0].Title);
        Assert.Equal(100, clamped.Steps[1].Target.GpuCoreOffsetMhz);
        Assert.Equal(60, clamped.Steps[1].StressSeconds);
        Assert.Equal(2, clamped.Warnings.Count);
        Assert.Contains("reduced", clamped.Warnings[0]);
        Assert.Equal("keep", clamped.Warnings[1]);

        var untouched = PcChecker.Claude.ClaudeAssistantService.ClampPlan(new OverclockPlan("s", [plan.Steps[1]], [], [], "d"), request);
        Assert.Empty(untouched.Warnings);
    }
}

public class AssistantCatalogAndFormatterTests
{
    [Fact]
    public void Catalog_resolves_known_and_custom_ids()
    {
        Assert.Equal("Claude Opus 5.5", AssistantModelCatalog.Resolve("claude-opus-5-5").Name);
        Assert.Equal(AssistantModelCatalog.DefaultModelId, AssistantModelCatalog.Resolve(null).Id);
        var custom = AssistantModelCatalog.Resolve("claude-custom-9");
        Assert.Equal("claude-custom-9", custom.Id);
        Assert.True(AssistantModelCatalog.IsPlausibleModelId("claude-sonnet-5-5"));
        Assert.False(AssistantModelCatalog.IsPlausibleModelId("not a model"));
        Assert.True(AssistantModelCatalog.SuggestedQuestions.Count >= 4);
    }

    [Fact]
    public void Formatter_strips_markdown_markers_and_fences()
    {
        const string md = "## Summary\n\nYour **disk** is `healthy`. See [docs](https://example.com/x).\n\n- item one\n* item two\n\n```\ncode here\n```\n\n\n\nDone _now_.";
        var text = AssistantTextFormatter.ToPlainText(md);
        Assert.StartsWith("Summary", text);
        Assert.Contains("Your disk is healthy. See docs (https://example.com/x).", text);
        Assert.Contains("• item one\n• item two", text);
        Assert.DoesNotContain("```", text);
        Assert.Contains("code here", text);
        Assert.DoesNotContain("\n\n\n", text);
        Assert.EndsWith("Done now.", text);
        Assert.Equal(string.Empty, AssistantTextFormatter.ToPlainText(null));
    }

    [Fact]
    public void Formatter_keeps_arithmetic_asterisks_and_snake_case()
    {
        var text = AssistantTextFormatter.ToPlainText("2 * 3 = 6 and file_name_here stays.");
        Assert.Equal("2 * 3 = 6 and file_name_here stays.", text);
    }

    [Fact]
    public void Demo_assistant_routes_questions_and_streams()
    {
        Assert.Contains("Startup", DemoPcAssistantService.PickReply("Why is my PC slow?", null));
        Assert.Contains("990 PRO", DemoPcAssistantService.PickReply("Is my disk healthy?", null));
        Assert.Contains("NVIDIA", DemoPcAssistantService.PickReply("What should I update first?", null));
        Assert.Contains("86", DemoPcAssistantService.PickReply("Explain my health report", null));
        Assert.Contains("demo profile", DemoPcAssistantService.PickReply("hello", null));
        Assert.Contains("latest scan results", DemoPcAssistantService.PickReply("hello", new AssistantContext("hw", null, null, null, null)));
    }

    [Fact]
    public async Task Demo_assistant_streams_whole_answer_and_honours_cancellation()
    {
        var svc = new DemoPcAssistantService();
        Assert.True(svc.IsConfigured);
        var chunks = new List<string>();
        await foreach (var c in svc.StreamChatAsync([AssistantMessage.User("Is my disk healthy?")], null, CancellationToken.None))
        {
            chunks.Add(c);
        }

        Assert.Contains("990", string.Concat(chunks));
        Assert.True(chunks.Count > 20);

        using var cts = new CancellationTokenSource();
        int seen = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in svc.StreamChatAsync([AssistantMessage.User("hello")], null, cts.Token))
            {
                if (++seen == 3)
                {
                    cts.Cancel();
                }
            }
        });
        Assert.Equal(3, seen);
    }
}
