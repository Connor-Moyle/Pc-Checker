namespace PcChecker.Core.Overclocking;

/// <summary>
/// Pure heuristic for thermal throttling: under sustained full load a clock that drops well below its peak
/// means the chip is protecting itself. Used by the stress test summary.
/// </summary>
public static class ThrottleHeuristic
{
    public const double DefaultDropFraction = 0.15;
    public const double DefaultLoadThreshold = 90;

    /// <summary>
    /// Returns true when the clock fell more than <paramref name="dropFraction"/> below its peak while load stayed at or
    /// above <paramref name="loadThreshold"/> percent. Samples without a load value are treated as loaded (CPU burn).
    /// </summary>
    public static bool Suspect(IReadOnlyList<double> clocksMhz, IReadOnlyList<double?>? loadsPercent = null,
        double dropFraction = DefaultDropFraction, double loadThreshold = DefaultLoadThreshold)
    {
        ArgumentNullException.ThrowIfNull(clocksMhz);
        if (clocksMhz.Count < 3)
        {
            return false;
        }

        double peak = 0;
        for (int i = 0; i < clocksMhz.Count; i++)
        {
            if (clocksMhz[i] <= 0)
            {
                continue;
            }

            bool loaded = loadsPercent is null || i >= loadsPercent.Count || loadsPercent[i] is null || loadsPercent[i] >= loadThreshold;
            if (!loaded)
            {
                continue;
            }

            peak = Math.Max(peak, clocksMhz[i]);
            if (peak > 0 && clocksMhz[i] < peak * (1 - dropFraction))
            {
                // Require the drop to persist for at least two consecutive samples to ignore single-sample noise.
                if (i + 1 < clocksMhz.Count && clocksMhz[i + 1] > 0 && clocksMhz[i + 1] < peak * (1 - dropFraction))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Plain-English verdict for the stress result.</summary>
    public static string Summarize(StressTarget target, int errors, bool aborted, bool throttle, double? maxCpu, double? maxGpu, double abortCpu, double abortGpu)
    {
        if (aborted)
        {
            return $"Stopped by the temperature watchdog: a limit of {abortCpu:0} °C (CPU) / {abortGpu:0} °C (GPU) was reached. The overclock was reverted for safety.";
        }

        if (errors > 0)
        {
            return $"Unstable: {errors} calculation error{(errors == 1 ? string.Empty : "s")} detected. Lower the offsets or raise the voltage limit a notch and test again.";
        }

        if (throttle)
        {
            return "Completed, but clocks dropped under load, which usually means thermal throttling. Improve cooling or lower the power limit before pushing further.";
        }

        var temps = string.Join(", ", new[]
        {
            maxCpu is { } c ? $"CPU peaked at {c:0} °C" : null,
            maxGpu is { } g ? $"GPU peaked at {g:0} °C" : null,
        }.Where(s => s is not null));
        string what = target switch
        {
            StressTarget.Cpu => "CPU burn",
            StressTarget.Gpu => "guided GPU test",
            _ => "combined test",
        };
        return $"Stable: the {what} finished with no errors{(temps.Length > 0 ? " (" + temps + ")" : string.Empty)}.";
    }
}
