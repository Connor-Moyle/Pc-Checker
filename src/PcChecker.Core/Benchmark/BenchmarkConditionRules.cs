namespace PcChecker.Core.Benchmark;

/// <summary>
/// Pure rules for "is now a good moment to benchmark?". Nothing here blocks a run: the warnings are shown before it
/// starts and saved with it, so a low score taken on battery or with a busy processor explains itself later.
/// </summary>
public static class BenchmarkConditionRules
{
    /// <summary>The processor counts as busy above this share of its time (measured over a second before the run).</summary>
    public const double BusyCpuPercent = 15;

    /// <summary>A processor this warm before the run may already be boosting less.</summary>
    public const double HotCpuC = 80;

    /// <summary>A graphics card this warm before the run may already be clocking down.</summary>
    public const double HotGpuC = 80;

    public static BenchmarkConditions Evaluate(BenchmarkConditionFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var warnings = new List<string>();
        if (facts.OnBattery == true)
        {
            string level = facts.BatteryPercent is { } p ? $" ({p} %)" : string.Empty;
            warnings.Add($"The laptop is on battery{level}. Windows slows the processor and graphics down to save power, so scores will be lower than on the charger.");
        }

        if (facts.CpuBusyPercent is { } busy && busy > BusyCpuPercent)
        {
            warnings.Add($"The processor is already {Math.Round(busy):0} % busy. Close other programs (games, browsers, downloads, updates) for a fair result.");
        }

        if (facts.CpuTempC is { } cpu && cpu >= HotCpuC)
        {
            warnings.Add($"The processor is hot ({Math.Round(cpu):0} °C) before the run. Let it cool down first, or check the cooling on the Fans page.");
        }

        if (facts.GpuTempC is { } gpu && gpu >= HotGpuC)
        {
            warnings.Add($"The graphics card is hot ({Math.Round(gpu):0} °C) before the run. Let it cool down first.");
        }

        return new BenchmarkConditions(facts, warnings);
    }

    /// <summary>
    /// Share of time the processor was busy between two <c>GetSystemTimes</c> readings, in percent. Kernel time includes
    /// idle time, so busy = (kernel + user − idle) ÷ (kernel + user). Null when no time passed or the counters went backwards.
    /// </summary>
    public static double? CpuBusyPercent(ulong idleBefore, ulong kernelBefore, ulong userBefore, ulong idleAfter, ulong kernelAfter, ulong userAfter)
    {
        if (idleAfter < idleBefore || kernelAfter < kernelBefore || userAfter < userBefore)
        {
            return null;
        }

        double idle = idleAfter - idleBefore;
        double total = (double)(kernelAfter - kernelBefore) + (userAfter - userBefore);
        if (total <= 0)
        {
            return null;
        }

        return Math.Clamp((total - idle) / total * 100, 0, 100);
    }
}

/// <summary>Pure rules for the saved history.</summary>
public static class BenchmarkHistoryRules
{
    /// <summary>Runs kept; the oldest are dropped beyond this.</summary>
    public const int MaxRuns = 100;

    /// <summary>Longest note kept with a run.</summary>
    public const int MaxNoteLength = 80;

    /// <summary>Newest first, without duplicate ids, at most <paramref name="max"/> runs.</summary>
    public static IReadOnlyList<BenchmarkRun> Normalize(IEnumerable<BenchmarkRun> runs, int max = MaxRuns)
    {
        ArgumentNullException.ThrowIfNull(runs);
        return runs
            .Where(r => r is not null)
            .GroupBy(r => r.Id)
            .Select(g => g.OrderByDescending(r => r.Started).First())
            .OrderByDescending(r => r.Started)
            .Take(Math.Max(1, max))
            .ToList();
    }

    /// <summary>The note trimmed to one line of at most <see cref="MaxNoteLength"/> characters; null when empty.</summary>
    public static string? CleanNote(string? note)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            return null;
        }

        var line = string.Join(' ', note.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return line.Length <= MaxNoteLength ? line : line[..MaxNoteLength].TrimEnd();
    }

    /// <summary>
    /// The default pair to compare: the newest run and the newest older run that completed the same tests (or, when
    /// none did, simply the one before it). Null when there are fewer than two runs.
    /// </summary>
    public static (BenchmarkRun Before, BenchmarkRun After)? DefaultComparison(IReadOnlyList<BenchmarkRun> newestFirst)
    {
        ArgumentNullException.ThrowIfNull(newestFirst);
        if (newestFirst.Count < 2)
        {
            return null;
        }

        var after = newestFirst[0];
        var before = newestFirst.Skip(1).FirstOrDefault(r => BenchmarkComparer.SameTests(r, after)) ?? newestFirst[1];
        return (before, after);
    }
}
