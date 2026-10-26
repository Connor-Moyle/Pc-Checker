namespace PcChecker.Core.Input;

/// <summary>One row of the polling rate interpretation table shown next to a measurement.</summary>
public sealed record PollingRateInfo(int NominalHz, string Label, string Note, double IntervalMs)
{
    public string IntervalText => IntervalMs >= 1 ? $"{IntervalMs:0.##} ms" : $"{IntervalMs * 1000:0} µs";
}

/// <summary>Interpretation of one measured polling rate: the closest standard rate and plain-English advice.</summary>
public sealed record PollingRateReading(int MeasuredHz, int NominalHz, string Label, string Note, string Verdict);

/// <summary>Pure math for the mouse polling rate meter (Raw Input event timestamps → Hz) and its interpretation.</summary>
public static class PollingRateMath
{
    /// <summary>Gaps longer than this are treated as the mouse standing still, not as a slow report rate.</summary>
    public const double PauseThresholdMs = 50;

    /// <summary>Standard USB/wireless report rates with notes a beginner can act on.</summary>
    public static IReadOnlyList<PollingRateInfo> Table { get; } =
    [
        new(125, "125 Hz", "Basic office mice and many Bluetooth mice. The pointer updates every 8 ms, which feels slightly laggy in games.", 8),
        new(250, "250 Hz", "Older gaming mice. Fine for desktop work; gamers usually want 500 Hz or more.", 4),
        new(500, "500 Hz", "Common gaming default. Smooth and easy on the CPU; a good balance for most people.", 2),
        new(1000, "1000 Hz", "The standard for gaming mice: a report every millisecond. Recommended for competitive play.", 1),
        new(2000, "2000 Hz", "High-rate wireless mice (needs the vendor dongle). Noticeably smoother on 240 Hz+ monitors.", 0.5),
        new(4000, "4000 Hz", "Enthusiast rate. Adds CPU load; some older games stutter at this rate.", 0.25),
        new(8000, "8000 Hz", "Maximum current rate. Only worth it with a fast CPU and a high-refresh monitor; some games misbehave.", 0.125),
    ];

    /// <summary>Finds the closest standard rate for a measurement (0 → the lowest row).</summary>
    public static PollingRateInfo Closest(int measuredHz)
    {
        if (measuredHz <= 0)
        {
            return Table[0];
        }

        // Compare on a log scale so 700 Hz is "closer" to 500 than to 1000 in the way users think about it.
        double log = Math.Log(measuredHz);
        return Table.MinBy(r => Math.Abs(Math.Log(r.NominalHz) - log))!;
    }

    /// <summary>Describes a measured rate: its nominal class and whether it looks healthy.</summary>
    public static PollingRateReading Interpret(int measuredHz)
    {
        if (measuredHz <= 0)
        {
            return new PollingRateReading(0, 0, "No movement", "No mouse reports were received.", "Move the mouse continuously during the test and try again.");
        }

        var row = Closest(measuredHz);
        double ratio = measuredHz / (double)row.NominalHz;
        string verdict;
        if (ratio < 0.8)
        {
            verdict = $"Measured below the nearest standard rate ({row.NominalHz} Hz). Keep the mouse moving the whole time; a low result can also mean a busy CPU, a Bluetooth link or a power-saving USB port.";
        }
        else if (row.NominalHz < 500)
        {
            verdict = "This rate is fine for everyday use. For gaming, set 1000 Hz in the mouse's software if the mouse supports it.";
        }
        else if (row.NominalHz < 1000)
        {
            verdict = "A solid gaming rate. 1000 Hz would shave another millisecond off pointer latency.";
        }
        else
        {
            verdict = "Excellent. Pointer latency from the mouse is about as low as it gets.";
        }

        return new PollingRateReading(measuredHz, row.NominalHz, row.Label, row.Note, verdict);
    }

    /// <summary>Short interpretation text used by tests and the UI summary line.</summary>
    public static string PollingRateText(int measuredHz)
    {
        var reading = Interpret(measuredHz);
        return reading.NominalHz == 0 ? reading.Verdict : $"{measuredHz} Hz — about {reading.Label}. {reading.Verdict}";
    }

    /// <summary>
    /// Estimates the report rate from the gaps (in milliseconds) between consecutive mouse reports. Gaps longer than
    /// <see cref="PauseThresholdMs"/> are ignored (the mouse was still). Uses the mean of the middle half of the gaps
    /// (the interquartile mean) so a few jittery or coalesced reports do not skew the result.
    /// </summary>
    public static int EstimateHz(IReadOnlyList<double> intervalsMs)
    {
        if (intervalsMs is null || intervalsMs.Count == 0)
        {
            return 0;
        }

        var moving = intervalsMs.Where(i => i > 0 && i <= PauseThresholdMs).OrderBy(i => i).ToArray();
        if (moving.Length < 8)
        {
            return 0;
        }

        int from = moving.Length / 4;
        int to = moving.Length - moving.Length / 4;
        double mean = moving.Skip(from).Take(to - from).Average();
        if (mean <= 0)
        {
            return 0;
        }

        return (int)Math.Round(1000.0 / mean);
    }

    /// <summary>Counts how many reports fell inside the last <paramref name="windowMs"/> before <paramref name="nowMs"/> (live Hz).</summary>
    public static int CountInWindow(IReadOnlyList<double> timestampsMs, double nowMs, double windowMs = 1000)
    {
        if (timestampsMs is null || timestampsMs.Count == 0 || windowMs <= 0)
        {
            return 0;
        }

        double from = nowMs - windowMs;
        int count = 0;
        for (int i = timestampsMs.Count - 1; i >= 0; i--)
        {
            double t = timestampsMs[i];
            if (t > nowMs)
            {
                continue;
            }

            if (t <= from)
            {
                break;
            }

            count++;
        }

        return (int)Math.Round(count * (1000.0 / windowMs));
    }
}
