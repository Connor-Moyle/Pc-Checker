using System.Globalization;

namespace PcChecker.Core.Overclocking.GpuLoad;

/// <summary>Pure one-line status text for a running stress test ("GPU 71 °C @ 2730 MHz · 13.3 passes/s, 0 errors · …").</summary>
public static class StressText
{
    public static string Detail(StressTestRequest request, StressLiveStatus s)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(s);
        var parts = new List<string>();
        if (s.CpuTempC is { } c)
        {
            parts.Add($"CPU {c:0} °C" + (s.CpuClockMhz is { } cc ? $" @ {cc} MHz" : string.Empty));
        }

        if (s.GpuTempC is { } g)
        {
            parts.Add($"GPU {g:0} °C" + (s.GpuClockMhz is { } gc ? $" @ {gc} MHz" : string.Empty));
        }

        if (request.LoadsGpu)
        {
            parts.Add($"{s.DispatchesPerSecond.ToString("0.0", CultureInfo.InvariantCulture)} GPU passes/s, {s.GpuErrors} GPU errors");
        }

        if (request.LoadsCpu)
        {
            parts.Add($"{s.CpuChecks.ToString("N0", CultureInfo.InvariantCulture)} CPU checks, {s.CpuErrors} errors");
        }

        if (request.WatchesGpu && request.GpuMode == GpuStressMode.Guided)
        {
            parts.Add("run your game or benchmark now");
        }

        return parts.Count == 0 ? "No sensor data yet." : string.Join(" · ", parts);
    }

    /// <summary>Average of the samples, or null when there are none.</summary>
    public static double? Average(IReadOnlyCollection<double> samples) => samples.Count == 0 ? null : Math.Round(samples.Average());
}

/// <summary>Pure assembly of <see cref="StressTestOutcome"/>s, shared by the Windows and demo services.</summary>
public static class StressOutcomes
{
    /// <summary>Builds the contract result and the outcome from the facts.</summary>
    public static StressTestOutcome From(StressTarget target, StressFacts facts, GpuLoadResult? gpu, double? maxCpu, double? maxGpu, bool throttle, bool aborted)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var verdict = StressVerdictBuilder.Build(facts);
        int errors = (int)Math.Min(int.MaxValue, facts.TotalErrors);
        var result = new StressTestResult(target, facts.Seconds, maxCpu, maxGpu, throttle, errors, verdict.Passed, verdict.Summary, aborted);
        return new StressTestOutcome(result, verdict, gpu, facts.AverageCpuClockMhz, facts.AverageGpuClockMhz, facts.CpuChecks, facts.OverclockReverted);
    }

    /// <summary>The outcome for a test that was refused before it started (for example on a low battery).</summary>
    public static StressTestOutcome NotStarted(StressTestRequest request, string message)
    {
        ArgumentNullException.ThrowIfNull(request);
        var verdict = new StressVerdict(false, "Not started", message, [message]);
        var result = new StressTestResult(request.Target, 0, null, null, false, 0, false, message, false);
        return new StressTestOutcome(result, verdict, null, null, null, 0, false);
    }
}

/// <summary>An <see cref="IProgress{T}"/> that runs the handler on the reporting thread (no synchronisation context).</summary>
internal sealed class InlineProgress<T> : IProgress<T>
{
    private readonly Action<T> _handler;

    public InlineProgress(Action<T> handler)
    {
        _handler = handler;
    }

    public void Report(T value) => _handler(value);
}
