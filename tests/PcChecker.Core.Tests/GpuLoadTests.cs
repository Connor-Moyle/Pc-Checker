using PcChecker.Core.Common;
using PcChecker.Core.Overclocking;
using PcChecker.Core.Overclocking.GpuLoad;
using Xunit;

namespace PcChecker.Core.Tests;

public class GpuLoadPlannerTests
{
    private const long GiB = 1024L * 1024 * 1024;
    private const long MiB = 1024L * 1024;

    [Theory]
    [InlineData(GpuLoadIntensity.Light, 12 * GiB, 64 * MiB)]
    [InlineData(GpuLoadIntensity.Medium, 12 * GiB, 256 * MiB)]
    [InlineData(GpuLoadIntensity.Heavy, 12 * GiB, 1024 * MiB)]
    [InlineData(GpuLoadIntensity.Heavy, 2 * GiB, 512 * MiB)]
    [InlineData(GpuLoadIntensity.Medium, 512 * MiB, 128 * MiB)]
    [InlineData(GpuLoadIntensity.Heavy, 128 * MiB, 32 * MiB)]
    public void Buffer_size_scales_with_intensity_and_is_capped_at_a_quarter_of_vram(GpuLoadIntensity intensity, long vram, long expected)
    {
        Assert.Equal(expected, GpuLoadPlanner.BufferBytesFor(intensity, vram));
    }

    [Fact]
    public void Buffer_size_never_exceeds_one_gib_or_falls_below_the_floor()
    {
        Assert.Equal(GpuLoadPlanner.MaxBufferBytes, GpuLoadPlanner.BufferBytesFor(GpuLoadIntensity.Heavy, 48 * GiB));
        Assert.Equal(GpuLoadPlanner.MinBufferBytes, GpuLoadPlanner.BufferBytesFor(GpuLoadIntensity.Heavy, 0));
        Assert.Equal(GpuLoadPlanner.MinBufferBytes, GpuLoadPlanner.BufferBytesFor(GpuLoadIntensity.Light, 32 * MiB));
        long odd = GpuLoadPlanner.BufferBytesFor(GpuLoadIntensity.Heavy, 1_000_000_007);
        Assert.Equal(0, odd % (GpuLoadPlanner.ThreadsPerGroup * GpuLoadPlanner.ElementBytes));
    }

    [Fact]
    public void Plan_derives_elements_targets_and_in_flight_from_intensity()
    {
        var heavy = GpuLoadPlanner.Create(GpuLoadIntensity.Heavy, 12 * GiB);
        Assert.Equal(GiB / 16, heavy.Elements);
        Assert.Equal(100, heavy.TargetDispatchMs);
        Assert.Equal(1.0, heavy.DutyCycle);
        Assert.Equal(2, heavy.MaxInFlight);
        Assert.True(heavy.InitialIterations < 100, "Calibration must start small to stay clear of the driver timeout.");

        var light = GpuLoadPlanner.Create(GpuLoadIntensity.Light, 12 * GiB);
        Assert.Equal(1, light.MaxInFlight);
        Assert.Equal(0.5, light.DutyCycle);
        Assert.Equal(40, light.VerifyEveryDispatches); // about two seconds of 50 ms dispatches
        Assert.True(light.TargetDispatchMs < heavy.TargetDispatchMs);
    }

    [Fact]
    public void Group_counts_respect_the_65535_dispatch_limit()
    {
        Assert.Equal((1, 1), GpuLoadPlanner.GroupCounts(1));
        Assert.Equal((4, 1), GpuLoadPlanner.GroupCounts(1024));
        var (x, y) = GpuLoadPlanner.GroupCounts((int)(GiB / 16));
        Assert.True(x <= GpuLoadPlanner.MaxGroupsPerDimension);
        Assert.True((long)x * y * GpuLoadPlanner.ThreadsPerGroup >= GiB / 16);
        Assert.Equal(65535, x);
        Assert.Equal(5, y);
    }

    [Fact]
    public void Idle_time_matches_the_duty_cycle()
    {
        Assert.Equal(TimeSpan.Zero, GpuLoadPlanner.IdleAfterDispatch(100, 1.0));
        Assert.Equal(50, GpuLoadPlanner.IdleAfterDispatch(50, 0.5).TotalMilliseconds, 3);
        Assert.Equal(15, GpuLoadPlanner.IdleAfterDispatch(85, 0.85).TotalMilliseconds, 3);
        Assert.Equal(TimeSpan.Zero, GpuLoadPlanner.IdleAfterDispatch(double.NaN, 0.5));
    }

    [Fact]
    public void Verification_windows_rotate_and_stay_inside_the_buffer()
    {
        int elements = 16 * 1024 * 1024;
        int window = GpuLoadPlanner.VerifyWindowElements;
        var offsets = Enumerable.Range(0, GpuLoadPlanner.VerifyWindows).Select(i => GpuLoadPlanner.WindowOffset(i, elements, window)).ToList();
        Assert.Equal(0, offsets[0]);
        Assert.Equal(GpuLoadPlanner.VerifyWindows, offsets.Distinct().Count());
        Assert.All(offsets, o => Assert.InRange(o, 0, elements - window));
        Assert.All(offsets, o => Assert.Equal(0, o % 4));
        Assert.Equal(offsets[1], GpuLoadPlanner.WindowOffset(GpuLoadPlanner.VerifyWindows + 1, elements, window));
        Assert.Equal(0, GpuLoadPlanner.WindowOffset(3, window, window));
    }
}

public class DispatchCalibratorTests
{
    [Fact]
    public void Scales_iterations_toward_the_target_time()
    {
        Assert.Equal(400, DispatchCalibrator.NextIterations(100, 25, 100));
        Assert.Equal(50, DispatchCalibrator.NextIterations(100, 200, 100));
        // Growth is limited to x8 per step and shrinking to /4 unless dangerously slow.
        Assert.Equal(800, DispatchCalibrator.NextIterations(100, 1, 100));
        Assert.Equal(25, DispatchCalibrator.NextIterations(100, 450, 100));
        Assert.Equal(10, DispatchCalibrator.NextIterations(100, 1000, 100));
        Assert.Equal(1, DispatchCalibrator.NextIterations(1, 5000, 100));
    }

    [Fact]
    public void Ignores_the_warm_up_dispatch_and_converges()
    {
        var c = new DispatchCalibrator(16, 100);
        Assert.Equal(16, c.Observe(400)); // warm-up (shader set-up) ignored
        Assert.False(c.IsCalibrated);

        // Simulate a card where time is proportional to iterations: 1 ms per 4 iterations.
        for (int i = 0; i < 10 && !c.IsCalibrated; i++)
        {
            c.Observe(c.Iterations / 4.0);
        }

        Assert.True(c.IsCalibrated);
        Assert.InRange(c.Iterations / 4.0, 75, 125);
    }

    [Fact]
    public void A_dangerously_slow_dispatch_cuts_work_even_after_calibration()
    {
        var c = new DispatchCalibrator(1000, 100);
        c.Observe(100);
        c.Observe(100);
        Assert.True(c.IsCalibrated);
        Assert.Equal(1000, c.Observe(110));
        int next = c.Observe(1500);
        Assert.True(next < 100, $"Expected a hard cut, got {next}.");
    }

    [Fact]
    public void Rate_meter_reports_events_per_second_over_its_window()
    {
        var meter = new RateMeter(2);
        Assert.Equal(0, meter.Rate);
        for (int i = 0; i <= 40; i++)
        {
            meter.Add(i * 0.1, i);
        }

        Assert.Equal(10, meter.Rate, 3);
    }
}

public class GpuResultComparerTests
{
    [Fact]
    public void Counts_bitwise_mismatches_and_length_differences()
    {
        float[] expected = [0.5f, 1.25f, -2f, 0.1f];
        Assert.Equal(0, GpuResultComparer.CountMismatches(expected, [0.5f, 1.25f, -2f, 0.1f]));
        Assert.Equal(1, GpuResultComparer.CountMismatches(expected, [0.5f, 1.2500001f, -2f, 0.1f]));
        Assert.Equal(2, GpuResultComparer.CountMismatches(expected, [0.5f, 1.25f]));
        Assert.Equal(1, GpuResultComparer.CountMismatches([0f], [-0f]));
    }

    [Fact]
    public void Invalid_values_are_nan_infinity_or_out_of_range()
    {
        Assert.Equal(0, GpuResultComparer.CountInvalid([0f, 2.9f, -3f]));
        Assert.Equal(3, GpuResultComparer.CountInvalid([float.NaN, float.PositiveInfinity, 12f, 1f]));
    }

    [Fact]
    public void First_read_of_a_window_becomes_the_reference()
    {
        var references = new float[]?[2];
        Assert.Equal(1, GpuResultComparer.Check(references, 0, [1f, float.NaN]));
        Assert.NotNull(references[0]);
        Assert.Equal(0, GpuResultComparer.Check(references, 1, [0.25f, 0.5f]));
        Assert.Equal(0, GpuResultComparer.Check(references, 1, [0.25f, 0.5f]));
        Assert.Equal(1, GpuResultComparer.Check(references, 1, [0.25f, 0.75f]));
    }
}

public class GpuAdapterRankingTests
{
    private static readonly GpuAdapterInfo Rtx = new(1, "NVIDIA GeForce RTX 4070", 0x10DE, 0x2786, 12L << 30, 16L << 30, false);
    private static readonly GpuAdapterInfo Uhd = new(0, "Intel(R) UHD Graphics 770", 0x8086, 0xA780, 128L << 20, 16L << 30, false);
    private static readonly GpuAdapterInfo Basic = new(2, "Microsoft Basic Render Driver", 0x1414, 0x8C, 0, 16L << 30, true);

    [Fact]
    public void Discrete_card_with_most_vram_is_recommended_and_software_is_skipped()
    {
        var ranked = GpuAdapterRanking.Rank([Uhd, Basic, Rtx]);
        Assert.Equal(2, ranked.Count);
        Assert.Equal(1, ranked[0].Index);
        Assert.True(ranked[0].IsRecommended);
        Assert.False(ranked[1].IsRecommended);
        Assert.Equal("NVIDIA", ranked[0].Vendor);
        Assert.Equal("Intel", ranked[1].Vendor);
        Assert.Contains("12 GB", ranked[0].DisplayName);
        Assert.Contains("recommended", ranked[0].DisplayName);
    }

    [Fact]
    public void Basic_render_driver_is_detected_even_without_the_software_flag()
    {
        Assert.True(GpuAdapterRanking.IsSoftwareAdapter(Basic with { IsSoftware = false }));
        Assert.True(GpuAdapterRanking.IsSoftwareAdapter(new GpuAdapterInfo(0, "Microsoft Basic Display Adapter", 0x1414, 0x1, 0, 0, false)));
        Assert.False(GpuAdapterRanking.IsSoftwareAdapter(Rtx));
        Assert.Empty(GpuAdapterRanking.Rank([Basic]));
    }

    [Fact]
    public void Pick_honours_the_request_and_falls_back_to_the_recommended_adapter()
    {
        var ranked = GpuAdapterRanking.Rank([Uhd, Rtx]);
        Assert.Equal(0, GpuAdapterRanking.Pick(ranked, 0)!.Index);
        Assert.Equal(1, GpuAdapterRanking.Pick(ranked, -1)!.Index);
        Assert.Equal(1, GpuAdapterRanking.Pick(ranked, 7)!.Index);
        Assert.Null(GpuAdapterRanking.Pick([], -1));
        Assert.Equal("AMD", GpuAdapterRanking.VendorName(0x1002));
        Assert.Equal("Unknown", GpuAdapterRanking.VendorName(0xABCD));
    }
}

public class GpuLoadShaderTests
{
    [Fact]
    public void Shader_source_declares_the_entry_point_thread_group_and_buffers()
    {
        Assert.Equal("main", GpuLoadShader.EntryPoint);
        Assert.Equal("cs_5_0", GpuLoadShader.Profile);
        Assert.Contains("void main(", GpuLoadShader.Source);
        Assert.Contains($"[numthreads({GpuLoadPlanner.ThreadsPerGroup}, 1, 1)]", GpuLoadShader.Source);
        Assert.Contains("RWStructuredBuffer<float4>", GpuLoadShader.Source);
        Assert.Contains("cbuffer LoadParams", GpuLoadShader.Source);
        Assert.Contains("Iterations", GpuLoadShader.Source);
        Assert.Equal(GpuLoadShader.Source.Count(c => c == '{'), GpuLoadShader.Source.Count(c => c == '}'));
    }

    [Fact]
    public void Device_loss_codes_map_to_the_driver_reset_message()
    {
        Assert.True(GpuDeviceLoss.IsDeviceLost(unchecked((int)0x887A0005)));
        Assert.True(GpuDeviceLoss.IsDeviceLost(GpuDeviceLoss.DeviceHung));
        Assert.True(GpuDeviceLoss.IsDeviceLost(GpuDeviceLoss.DeviceReset));
        Assert.False(GpuDeviceLoss.IsDeviceLost(unchecked((int)0x80070057)));
        Assert.StartsWith(GpuDeviceLoss.DriverResetMessage, GpuDeviceLoss.Describe(GpuDeviceLoss.DeviceHung));
        Assert.Contains("887A0006", GpuDeviceLoss.Describe(GpuDeviceLoss.DeviceHung));
        Assert.Equal(GpuDeviceLoss.DeviceRemoved, new GpuDeviceLostException(GpuDeviceLoss.DeviceRemoved).HResult);
    }
}

public class StressVerdictTests
{
    private static StressFacts Gpu(Func<StressFacts, StressFacts>? change = null)
    {
        var f = new StressFacts(StressTarget.Gpu, GpuStressMode.BuiltIn, 120)
        {
            GpuDispatches = 1200,
            MaxGpuTempC = 71,
            AverageGpuClockMhz = 2715,
            GpuAdapterName = "NVIDIA GeForce RTX 4070",
        };
        return change is null ? f : change(f);
    }

    [Fact]
    public void Clean_built_in_gpu_run_passes_with_plain_reasons()
    {
        var v = StressVerdictBuilder.Build(Gpu());
        Assert.True(v.Passed);
        Assert.Equal("Passed", v.Title);
        Assert.StartsWith("Stable: the built-in GPU load", v.Summary);
        Assert.Contains(v.Reasons, r => r.Contains("0 wrong values in 1,200 compute passes"));
        Assert.Contains(v.Reasons, r => r.Contains("Average GPU clock 2,715 MHz"));
        Assert.DoesNotContain(v.Reasons, r => r.StartsWith("CPU:"));
    }

    [Fact]
    public void Driver_reset_beats_every_other_outcome()
    {
        var v = StressVerdictBuilder.Build(Gpu(f => f with { DriverReset = true, GpuErrors = 4, ThrottleSuspected = true, OverclockReverted = true }));
        Assert.False(v.Passed);
        Assert.Equal("Failed: the graphics driver reset", v.Title);
        Assert.Contains("not stable at these settings", v.Summary);
        Assert.Contains("reset to defaults", v.Summary);
    }

    [Fact]
    public void Gpu_errors_watchdog_and_throttling_map_to_failures_and_warnings()
    {
        Assert.Equal("Failed: GPU errors", StressVerdictBuilder.Build(Gpu(f => f with { GpuErrors = 3 })).Title);
        Assert.Contains("3 wrong values", StressVerdictBuilder.Build(Gpu(f => f with { GpuErrors = 3 })).Summary);
        Assert.Equal("Stopped by the watchdog", StressVerdictBuilder.Build(Gpu(f => f with { WatchdogAborted = true })).Title);
        var throttled = StressVerdictBuilder.Build(Gpu(f => f with { ThrottleSuspected = true }));
        Assert.False(throttled.Passed);
        Assert.Equal("Completed with warnings", throttled.Title);
    }

    [Fact]
    public void Gpu_load_that_cannot_start_points_to_the_guided_option()
    {
        var v = StressVerdictBuilder.Build(Gpu(f => f with { GpuNotStarted = true, GpuFailure = "No DirectX 11 graphics card was found.", GpuDispatches = 0 }));
        Assert.False(v.Passed);
        Assert.Equal("GPU load could not start", v.Title);
        Assert.Contains("own game or benchmark", v.Summary);
    }

    [Fact]
    public void Guided_and_combined_runs_describe_themselves()
    {
        var guided = StressVerdictBuilder.Build(new StressFacts(StressTarget.Gpu, GpuStressMode.Guided, 60) { MaxGpuTempC = 70 });
        Assert.True(guided.Passed);
        Assert.Contains("guided GPU test", guided.Summary);
        Assert.Contains(guided.Reasons, r => r.Contains("cannot see rendering errors"));

        var both = StressVerdictBuilder.Build(new StressFacts(StressTarget.Both, GpuStressMode.BuiltIn, 60) { CpuErrors = 2, CpuChecks = 5000, MaxCpuTempC = 80, MaxGpuTempC = 70 });
        Assert.Equal("Failed: calculation errors", both.Title);
        Assert.Contains(both.Reasons, r => r.Contains("2 errors in 5,000 checked calculations"));
        Assert.Equal("combined CPU and GPU test", StressVerdictBuilder.TestName(new StressFacts(StressTarget.Both, GpuStressMode.BuiltIn, 1)));
    }

    [Fact]
    public void Outcome_carries_the_total_error_count_into_the_contract_result()
    {
        var facts = new StressFacts(StressTarget.Both, GpuStressMode.BuiltIn, 90) { CpuErrors = 1, GpuErrors = 2, MaxCpuTempC = 80, MaxGpuTempC = 70 };
        var outcome = StressOutcomes.From(StressTarget.Both, facts, null, 80, 70, false, false);
        Assert.Equal(3, outcome.Result.Errors);
        Assert.False(outcome.Result.Passed);
        Assert.Equal(outcome.Verdict.Summary, outcome.Result.Summary);

        var refused = StressOutcomes.NotStarted(new StressTestRequest(StressTarget.Gpu, 60, 90, 87), "Plug in first.");
        Assert.Equal("Not started", refused.Verdict.Title);
        Assert.Equal(0, refused.Result.DurationSeconds);
    }
}

public class StressSafetyTests
{
    [Fact]
    public void Battery_below_twenty_percent_refuses_and_above_warns()
    {
        Assert.True(StressSafety.Evaluate(false, 10).CanRun);
        Assert.False(StressSafety.Evaluate(false, 10).HasMessage);
        Assert.True(StressSafety.Evaluate(null, null).CanRun);

        var low = StressSafety.Evaluate(true, 19);
        Assert.False(low.CanRun);
        Assert.Contains("19 %", low.Message);

        var unknown = StressSafety.Evaluate(true, null);
        Assert.False(unknown.CanRun);

        var ok = StressSafety.Evaluate(true, 20);
        Assert.True(ok.CanRun);
        Assert.True(ok.OnBattery);
        Assert.True(ok.HasMessage);
    }

    [Fact]
    public void Power_status_bytes_are_decoded()
    {
        Assert.Equal((true, 45), PowerStatusProbe.Interpret(0, 0, 45));
        Assert.Equal((false, 100), PowerStatusProbe.Interpret(1, 8, 100));
        Assert.Equal((false, (int?)null), PowerStatusProbe.Interpret(1, 128, 255));
        Assert.Equal(((bool?)null, (int?)null), PowerStatusProbe.Interpret(255, 0, 255));
    }

    [Fact]
    public void Live_detail_text_names_what_is_under_test()
    {
        var request = new StressTestRequest(StressTarget.Both, 60, 90, 87);
        var status = new StressLiveStatus(TimeSpan.FromSeconds(10), 16, 78, 70, 5200, 2730, 99, 13.4, 0, 12345, 0, "RTX");
        string text = StressText.Detail(request, status);
        Assert.Contains("CPU 78 °C @ 5200 MHz", text);
        Assert.Contains("GPU 70 °C @ 2730 MHz", text);
        Assert.Contains("13.4 GPU passes/s", text);
        Assert.Contains("12,345 CPU checks", text);

        string guided = StressText.Detail(request with { Target = StressTarget.Gpu, GpuMode = GpuStressMode.Guided }, status);
        Assert.Contains("run your game or benchmark now", guided);
        Assert.DoesNotContain("passes/s", guided);
    }
}

public class DemoGpuLoadTests
{
    [Fact]
    public async Task Demo_generator_lists_the_sample_adapters_and_reports_progress()
    {
        var demo = new DemoGpuLoadGenerator(TimeSpan.FromMilliseconds(5));
        var adapters = demo.ListAdapters();
        Assert.Equal(2, adapters.Count);
        Assert.Equal(DemoData.GpuName, adapters[0].Name);

        var reports = new List<GpuLoadProgress>();
        var result = await demo.RunAsync(new GpuLoadOptions(-1, GpuLoadIntensity.Heavy, TimeSpan.FromSeconds(10), true), new InlineTestProgress<GpuLoadProgress>(reports.Add), CancellationToken.None);
        Assert.True(result.Completed);
        Assert.Equal(0, result.Errors);
        Assert.True(result.Dispatches > 50);
        Assert.NotEmpty(reports);
        Assert.Equal(GpuLoadPlanner.MaxBufferBytes, result.BufferBytes);
    }

    [Fact]
    public async Task Demo_service_runs_the_built_in_gpu_load_with_live_readouts()
    {
        var service = new DemoOverclockService();
        Assert.True(service.CheckPreflight().CanRun);
        Assert.Equal(2, (await service.ListGpuAdaptersAsync(CancellationToken.None)).Count);

        var live = new List<StressLiveStatus>();
        var outcome = await service.RunStressTestAsync(new StressTestRequest(StressTarget.Gpu, 5, 95, 95) { Intensity = GpuLoadIntensity.Heavy },
            new InlineTestProgress<StressLiveStatus>(live.Add), null, CancellationToken.None);
        Assert.True(outcome.Result.Passed);
        Assert.NotNull(outcome.Gpu);
        Assert.True(outcome.Gpu!.Dispatches > 0);
        Assert.NotEmpty(live);
        Assert.Contains(live, s => s.DispatchesPerSecond > 5);
        Assert.Contains("built-in GPU load", outcome.Verdict.Summary);
    }

    private sealed class InlineTestProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
