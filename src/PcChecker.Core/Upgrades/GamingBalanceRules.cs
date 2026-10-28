using PcChecker.Core.History;

namespace PcChecker.Core.Upgrades;

/// <summary>What one History sample says about the CPU/GPU balance.</summary>
public enum SampleBound
{
    NotGaming,
    GpuLimited,
    CpuLimited,
    Neither,
}

/// <summary>
/// Works out from History samples whether games were held back by the graphics card or by the processor. A sample
/// counts as gaming when the GPU is busy (at least <see cref="GamingGpuMin"/> %). GPU-limited: GPU at
/// <see cref="GpuLimitedMin"/> % or more while the CPU total stays under <see cref="GpuLimitedCpuMax"/> %. CPU-limited:
/// GPU under <see cref="CpuLimitedGpuMax"/> % while the busiest core is near 100 % (per-core data) or, when only the
/// total is recorded, the total is at least <see cref="CpuLimitedTotalMin"/> %.
/// </summary>
public static class GamingBalanceRules
{
    /// <summary>History metric for the busiest core's load, used when the recorder provides it.</summary>
    public const string BusiestCoreMetric = "cpu.core.max";

    public const double GamingGpuMin = 50;
    public const double GpuLimitedMin = 90;
    public const double GpuLimitedCpuMax = 70;
    public const double CpuLimitedGpuMax = 80;
    public const double CpuLimitedCoreMin = 90;
    public const double CpuLimitedTotalMin = 55;

    /// <summary>Less gaming than this (minutes) is not enough to say anything.</summary>
    public const double MinGamingMinutes = 15;

    /// <summary>A share of the gaming time at least this large decides the overall verdict.</summary>
    public const double VerdictShare = 0.35;

    /// <summary>Gaps longer than this split two sessions.</summary>
    public static readonly TimeSpan SessionGap = TimeSpan.FromMinutes(10);

    /// <summary>Classifies one sample. <paramref name="busiestCore"/> is null when only the CPU total is known.</summary>
    public static SampleBound Classify(double? gpuLoad, double? cpuTotal, double? busiestCore)
    {
        if (gpuLoad is not { } gpu || !double.IsFinite(gpu) || gpu < GamingGpuMin)
        {
            return SampleBound.NotGaming;
        }

        double cpu = cpuTotal is { } c && double.IsFinite(c) ? c : double.NaN;
        if (gpu >= GpuLimitedMin && (double.IsNaN(cpu) || cpu < GpuLimitedCpuMax))
        {
            return SampleBound.GpuLimited;
        }

        if (gpu < CpuLimitedGpuMax)
        {
            bool cpuBusy = busiestCore is { } core && double.IsFinite(core)
                ? core >= CpuLimitedCoreMin
                : !double.IsNaN(cpu) && cpu >= CpuLimitedTotalMin;
            if (cpuBusy)
            {
                return SampleBound.CpuLimited;
            }
        }

        return SampleBound.Neither;
    }

    /// <summary>Analyses every gaming sample in <paramref name="samples"/> (any order) into sessions and an overall verdict.</summary>
    public static BoundAnalysis Analyze(IReadOnlyList<HistorySample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var gaming = new List<(HistorySample Sample, SampleBound Bound)>();
        bool perCore = false;
        foreach (var s in samples.Where(s => s.SpanSeconds > 0).OrderBy(s => s.Time))
        {
            double? core = s.Get(BusiestCoreMetric);
            var bound = Classify(s.Get(HistoryMetrics.GpuLoad), s.Get(HistoryMetrics.CpuLoad), core);
            if (bound != SampleBound.NotGaming)
            {
                perCore |= core is not null;
                gaming.Add((s, bound));
            }
        }

        double totalSeconds = gaming.Sum(g => (double)g.Sample.SpanSeconds);
        double minutes = totalSeconds / 60;
        if (minutes < MinGamingMinutes)
        {
            return BoundAnalysis.None with { GamingMinutes = minutes };
        }

        double gpuShare = gaming.Where(g => g.Bound == SampleBound.GpuLimited).Sum(g => (double)g.Sample.SpanSeconds) / totalSeconds;
        double cpuShare = gaming.Where(g => g.Bound == SampleBound.CpuLimited).Sum(g => (double)g.Sample.SpanSeconds) / totalSeconds;
        var verdict = VerdictFor(gpuShare, cpuShare);
        var confidence = ConfidenceFor(verdict, minutes, Math.Max(gpuShare, cpuShare), perCore);
        return new BoundAnalysis(verdict, confidence, minutes, gpuShare, cpuShare, perCore, Sessions(gaming));
    }

    /// <summary>GPU- or CPU-limited when that share is at least <see cref="VerdictShare"/> and the larger one; otherwise balanced.</summary>
    public static BoundVerdict VerdictFor(double gpuShare, double cpuShare)
    {
        if (gpuShare >= VerdictShare && gpuShare >= cpuShare)
        {
            return BoundVerdict.GpuLimited;
        }

        return cpuShare >= VerdictShare ? BoundVerdict.CpuLimited : BoundVerdict.Balanced;
    }

    /// <summary>
    /// High with two hours or more and a clear majority, medium with 45 minutes or more, low otherwise. A CPU verdict
    /// from the CPU total alone (no per-core data) is never more than medium.
    /// </summary>
    public static AdviceConfidence ConfidenceFor(BoundVerdict verdict, double minutes, double dominantShare, bool perCore)
    {
        var confidence = minutes >= 120 && (verdict == BoundVerdict.Balanced || dominantShare >= 0.6) ? AdviceConfidence.High
            : minutes >= 45 ? AdviceConfidence.Medium
            : AdviceConfidence.Low;
        if (verdict == BoundVerdict.CpuLimited && !perCore && confidence == AdviceConfidence.High)
        {
            confidence = AdviceConfidence.Medium;
        }

        return confidence;
    }

    private static IReadOnlyList<GamingSession> Sessions(List<(HistorySample Sample, SampleBound Bound)> gaming)
    {
        var sessions = new List<GamingSession>();
        int start = 0;
        for (int i = 1; i <= gaming.Count; i++)
        {
            bool split = i == gaming.Count
                || gaming[i].Sample.Time - gaming[i - 1].Sample.Time.AddSeconds(gaming[i - 1].Sample.SpanSeconds) > SessionGap;
            if (!split)
            {
                continue;
            }

            var run = gaming.GetRange(start, i - start);
            double seconds = run.Sum(r => (double)r.Sample.SpanSeconds);
            if (seconds >= 5 * 60)
            {
                double Avg(string metric) => run.Sum(r => (r.Sample.Get(metric) ?? 0) * r.Sample.SpanSeconds) / seconds;
                double gpuShare = run.Where(r => r.Bound == SampleBound.GpuLimited).Sum(r => (double)r.Sample.SpanSeconds) / seconds;
                double cpuShare = run.Where(r => r.Bound == SampleBound.CpuLimited).Sum(r => (double)r.Sample.SpanSeconds) / seconds;
                bool hasCore = run.Any(r => r.Sample.Get(BusiestCoreMetric) is not null);
                var verdict = gpuShare >= 0.5 ? BoundVerdict.GpuLimited : cpuShare >= 0.5 ? BoundVerdict.CpuLimited : VerdictFor(gpuShare, cpuShare);
                sessions.Add(new GamingSession(run[0].Sample.Time, run[^1].Sample.Time.AddSeconds(run[^1].Sample.SpanSeconds), seconds / 60,
                    verdict, Avg(HistoryMetrics.GpuLoad), Avg(HistoryMetrics.CpuLoad), hasCore ? Avg(BusiestCoreMetric) : null));
            }

            start = i;
        }

        return sessions.OrderByDescending(s => s.Start).ToList();
    }
}
