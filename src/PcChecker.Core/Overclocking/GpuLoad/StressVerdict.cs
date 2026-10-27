using System.Globalization;

namespace PcChecker.Core.Overclocking.GpuLoad;

/// <summary>What happened during a stress test, in numbers; input to <see cref="StressVerdictBuilder"/>.</summary>
public sealed record StressFacts(StressTarget Target, GpuStressMode GpuMode, int Seconds)
{
    public int CpuErrors { get; init; }
    public long CpuChecks { get; init; }
    public long GpuErrors { get; init; }
    public long GpuDispatches { get; init; }
    public bool DriverReset { get; init; }
    public string? GpuFailure { get; init; }
    public bool GpuNotStarted { get; init; }
    public bool WatchdogAborted { get; init; }
    public bool ThrottleSuspected { get; init; }
    public bool NoTemperatureData { get; init; }
    public double? MaxCpuTempC { get; init; }
    public double? MaxGpuTempC { get; init; }
    public double AbortCpuTempC { get; init; } = 90;
    public double AbortGpuTempC { get; init; } = 87;
    public double? AverageCpuClockMhz { get; init; }
    public double? AverageGpuClockMhz { get; init; }
    public bool OverclockReverted { get; init; }
    public string? GpuAdapterName { get; init; }

    /// <summary>The processor name, for the Ryzen single-core stability note.</summary>
    public string? CpuName { get; init; }

    public bool LoadsCpu => Target is StressTarget.Cpu or StressTarget.Both;
    public bool WatchesGpu => Target is StressTarget.Gpu or StressTarget.Both;
    public bool BuiltInGpu => WatchesGpu && GpuMode == GpuStressMode.BuiltIn;
    public long TotalErrors => CpuErrors + GpuErrors;
}

/// <summary>Pass/fail with a short title, a one-paragraph summary and plain-English reasons for the page.</summary>
public sealed record StressVerdict(bool Passed, string Title, string Summary, IReadOnlyList<string> Reasons);

/// <summary>
/// Pure mapping from stress-test facts to a verdict. Failure wins over warnings: a driver reset, the temperature
/// watchdog, calculation errors or a GPU load that could not start all mean "not passed"; throttling or missing
/// temperatures complete with a warning; otherwise the test passed.
/// </summary>
public static class StressVerdictBuilder
{
    public static StressVerdict Build(StressFacts f)
    {
        ArgumentNullException.ThrowIfNull(f);
        var reasons = BuildReasons(f);
        string what = TestName(f);
        string reverted = f.OverclockReverted ? " The overclock was reset to defaults for safety." : string.Empty;

        if (f.DriverReset)
        {
            return new StressVerdict(false, "Failed: the graphics driver reset",
                $"{GpuDeviceLoss.DriverResetMessage}. The screen may have flickered or gone black for a moment while Windows recovered the driver. Lower the GPU clock offsets (or return to stock) and test again.{reverted}",
                reasons);
        }

        if (f.WatchdogAborted)
        {
            return new StressVerdict(false, "Stopped by the watchdog",
                $"Stopped by the temperature watchdog: a limit of {f.AbortCpuTempC:0} °C (CPU) / {f.AbortGpuTempC:0} °C (GPU) was reached.{(f.OverclockReverted ? reverted : " Improve cooling before testing longer.")}",
                reasons);
        }

        if (f.GpuErrors > 0)
        {
            return new StressVerdict(false, "Failed: GPU errors",
                $"Unstable: the graphics card returned {Plural(f.GpuErrors, "wrong value")} during the {what}. These are the same kind of errors that show up as artifacts in games. Lower the GPU core or memory offset and test again.",
                reasons);
        }

        if (f.CpuErrors > 0)
        {
            return new StressVerdict(false, "Failed: calculation errors",
                $"Unstable: {Plural(f.CpuErrors, "calculation error")} on the processor. Lower the CPU ratio or raise the voltage a notch and test again.",
                reasons);
        }

        if (f.BuiltInGpu && f.GpuNotStarted)
        {
            return new StressVerdict(false, "GPU load could not start",
                $"The built-in GPU load could not start: {f.GpuFailure?.TrimEnd('.') ?? "unknown error"}. Try \"Use my own game or benchmark instead\" to test the graphics card.",
                reasons);
        }

        if (f.BuiltInGpu && f.GpuFailure is { } failure)
        {
            return new StressVerdict(false, "Failed: the GPU test stopped",
                $"{failure.TrimEnd('.')}. Test again at stock settings; if it happens again, update the graphics driver.",
                reasons);
        }

        if (f.ThrottleSuspected)
        {
            return new StressVerdict(false, "Completed with warnings",
                "Completed without errors, but clocks dropped under load, which usually means thermal or power throttling. Improve cooling or lower the power limit before pushing further.",
                reasons);
        }

        if (f.NoTemperatureData)
        {
            return new StressVerdict(false, "Completed with warnings",
                "Completed with no calculation errors, but no temperature data was available, so the watchdog could not protect the hardware. Check the hardware monitor on the Temperatures page and treat this result with caution.",
                reasons);
        }

        string temps = string.Join(", ", new[]
        {
            f.LoadsCpu && f.MaxCpuTempC is { } c ? $"CPU peaked at {c:0} °C" : null,
            f.WatchesGpu && f.MaxGpuTempC is { } g ? $"GPU peaked at {g:0} °C" : null,
        }.Where(s => s is not null));
        string guided = f.WatchesGpu && f.GpuMode == GpuStressMode.Guided
            ? " (Guided GPU test: PC Checker watched while you ran your own game or benchmark.)"
            : string.Empty;
        return new StressVerdict(true, "Passed",
            $"Stable: the {what} finished with no errors{(temps.Length > 0 ? " (" + temps + ")" : string.Empty)}.{guided}",
            reasons);
    }

    /// <summary>Short name of the test for sentences ("CPU burn", "built-in GPU load", …).</summary>
    public static string TestName(StressFacts f) => f.Target switch
    {
        StressTarget.Cpu => "CPU burn",
        StressTarget.Gpu => f.GpuMode == GpuStressMode.BuiltIn ? "built-in GPU load" : "guided GPU test",
        _ => f.GpuMode == GpuStressMode.BuiltIn ? "combined CPU and GPU test" : "CPU burn with guided GPU test",
    };

    private static List<string> BuildReasons(StressFacts f)
    {
        var reasons = new List<string>();
        if (f.LoadsCpu)
        {
            reasons.Add(f.CpuChecks > 0
                ? $"CPU: {Plural(f.CpuErrors, "error")} in {f.CpuChecks.ToString("N0", CultureInfo.InvariantCulture)} checked calculations."
                : $"CPU: {Plural(f.CpuErrors, "error")}.");
        }

        if (f.BuiltInGpu)
        {
            if (f.GpuNotStarted)
            {
                reasons.Add("GPU: the built-in load did not start" + (f.GpuFailure is { } why ? $" ({why.TrimEnd('.')})." : "."));
            }
            else
            {
                string on = string.IsNullOrWhiteSpace(f.GpuAdapterName) ? string.Empty : $" on {f.GpuAdapterName}";
                reasons.Add($"GPU{on}: {Plural(f.GpuErrors, "wrong value")} in {f.GpuDispatches.ToString("N0", CultureInfo.InvariantCulture)} compute passes.");
            }
        }
        else if (f.WatchesGpu)
        {
            reasons.Add("GPU: guided test, so PC Checker watched temperatures and clocks while your own game or benchmark ran (it cannot see rendering errors).");
        }

        if (f.DriverReset)
        {
            reasons.Add("The graphics driver reset during the test (Windows recovered it automatically).");
        }

        if (f.LoadsCpu || f.MaxCpuTempC is not null)
        {
            reasons.Add(f.MaxCpuTempC is { } c ? $"CPU peaked at {c:0} °C (stop limit {f.AbortCpuTempC:0} °C)." : "No CPU temperature was reported.");
        }

        if (f.WatchesGpu)
        {
            reasons.Add(f.MaxGpuTempC is { } g ? $"GPU peaked at {g:0} °C (stop limit {f.AbortGpuTempC:0} °C)." : "No GPU temperature was reported.");
        }

        if (f.LoadsCpu && StressAdvice.AmdSingleCoreNote(f.CpuName) is { } amd)
        {
            reasons.Add(amd);
        }

        if (f.AverageCpuClockMhz is { } cc && f.LoadsCpu)
        {
            reasons.Add($"Average CPU clock {cc.ToString("N0", CultureInfo.InvariantCulture)} MHz.");
        }

        if (f.AverageGpuClockMhz is { } gc && f.WatchesGpu)
        {
            reasons.Add($"Average GPU clock {gc.ToString("N0", CultureInfo.InvariantCulture)} MHz.");
        }

        reasons.Add(f.ThrottleSuspected ? "Clocks dropped under full load: throttling suspected." : "No throttling detected.");
        if (f.OverclockReverted)
        {
            reasons.Add("The applied overclock was reset to defaults.");
        }

        return reasons;
    }

    private static string Plural(long count, string noun) =>
        $"{count.ToString("N0", CultureInfo.InvariantCulture)} {noun}{(count == 1 ? string.Empty : "s")}";
}
