namespace PcChecker.Core.History;

/// <summary>
/// Combines samples into coarser buckets, both for storage (one-minute samples become 15-minute points after
/// <see cref="FineAfterDays"/> days and hourly points after <see cref="CoarseAfterDays"/> days) and for drawing
/// charts with a sensible number of points. Each metric is combined by its own rule (peak temperature, lowest
/// health score, time-weighted average load, last free space), so rolling up twice gives the same result.
/// </summary>
public static class HistoryRollup
{
    public const int FineAfterDays = 7;
    public const int CoarseAfterDays = 30;
    public static readonly TimeSpan FineBucket = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan CoarseBucket = TimeSpan.FromHours(1);

    /// <summary>Most points a chart gets; the display bucket is the smallest "nice" size that stays under it.</summary>
    public const int MaxChartPoints = 360;

    private static readonly int[] NiceBucketMinutes = [1, 2, 5, 10, 15, 30, 60, 120, 180, 360, 720, 1440];

    /// <summary>
    /// The resolution a day file should be stored at, by its age: null (as recorded) for the last week, 15 minutes
    /// up to 30 days, one hour after that.
    /// </summary>
    public static TimeSpan? StorageBucketFor(DateOnly day, DateOnly today)
    {
        int age = today.DayNumber - day.DayNumber;
        return age > CoarseAfterDays ? CoarseBucket : age > FineAfterDays ? FineBucket : null;
    }

    /// <summary>The bucket a chart over <paramref name="range"/> uses so it has at most <see cref="MaxChartPoints"/> points.</summary>
    public static TimeSpan DisplayBucketFor(TimeSpan range)
    {
        foreach (int minutes in NiceBucketMinutes)
        {
            if (range.TotalMinutes / minutes <= MaxChartPoints)
            {
                return TimeSpan.FromMinutes(minutes);
            }
        }

        return TimeSpan.FromMinutes(NiceBucketMinutes[^1]);
    }

    /// <summary>True when some sample is finer than <paramref name="bucket"/> or two samples share a bucket.</summary>
    public static bool NeedsCompaction(IReadOnlyList<HistorySample> samples, TimeSpan bucket)
    {
        ArgumentNullException.ThrowIfNull(samples);
        long size = BucketSeconds(bucket);
        var seen = new HashSet<long>();
        foreach (var s in samples)
        {
            long time = s.Time.ToUnixTimeSeconds();
            if (s.SpanSeconds < size || time % size != 0 || !seen.Add(time))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Groups the samples into buckets of <paramref name="bucket"/> (aligned to Unix time, so to UTC hours) and
    /// combines each metric by its <see cref="HistoryAggregation"/>. Each result is stamped with its bucket start and
    /// covers the bucket's length (or more, when an input sample was already coarser). Averages are weighted by the
    /// seconds each sample covers.
    /// </summary>
    public static IReadOnlyList<HistorySample> Compact(IEnumerable<HistorySample> samples, TimeSpan bucket)
    {
        ArgumentNullException.ThrowIfNull(samples);
        long size = BucketSeconds(bucket);
        var buckets = new SortedDictionary<long, Bucket>();
        foreach (var sample in samples.OrderBy(s => s.Time))
        {
            long start = BucketStart(sample.Time.ToUnixTimeSeconds(), size);
            if (!buckets.TryGetValue(start, out var bucketState))
            {
                bucketState = new Bucket();
                buckets[start] = bucketState;
            }

            var metrics = bucketState.Metrics;
            bucketState.Span = Math.Max(bucketState.Span, sample.SpanSeconds);

            double weight = Math.Max(0, sample.SpanSeconds);
            foreach (var (metric, value) in sample.Values)
            {
                if (!double.IsFinite(value))
                {
                    continue;
                }

                if (!metrics.TryGetValue(metric, out var acc))
                {
                    acc = new Accumulator(HistoryMetrics.AggregationOf(metric));
                    metrics[metric] = acc;
                }

                acc.Add(value, weight);
            }
        }

        var result = new List<HistorySample>(buckets.Count);
        foreach (var (start, state) in buckets)
        {
            var values = state.Metrics.Where(m => m.Value.HasValue).ToDictionary(m => m.Key, m => m.Value.Result, StringComparer.Ordinal);
            if (values.Count > 0)
            {
                result.Add(new HistorySample(DateTimeOffset.FromUnixTimeSeconds(start), (int)Math.Max(size, state.Span), values));
            }
        }

        return result;
    }

    private static long BucketSeconds(TimeSpan bucket)
    {
        long size = (long)bucket.TotalSeconds;
        if (size <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bucket), "The bucket must be at least one second.");
        }

        return size;
    }

    private static long BucketStart(long unixSeconds, long size) => unixSeconds - (((unixSeconds % size) + size) % size);

    private sealed class Bucket
    {
        public Dictionary<string, Accumulator> Metrics { get; } = new(StringComparer.Ordinal);

        public int Span { get; set; }
    }

    /// <summary>Running combination of one metric inside one bucket.</summary>
    private sealed class Accumulator(HistoryAggregation aggregation)
    {
        private double _sum;
        private double _weight;
        private double _instantSum;
        private int _instantCount;
        private double _value;
        private bool _has;

        public bool HasValue => _has;

        /// <summary>Instants (span 0) only count when nothing in the bucket covers any time.</summary>
        public double Result => aggregation != HistoryAggregation.Average ? _value
            : _weight > 0 ? _sum / _weight
            : _instantSum / _instantCount;

        public void Add(double value, double weight)
        {
            switch (aggregation)
            {
                case HistoryAggregation.Average when weight > 0:
                    _sum += value * weight;
                    _weight += weight;
                    break;
                case HistoryAggregation.Average:
                    _instantSum += value;
                    _instantCount++;
                    break;
                case HistoryAggregation.Max:
                    _value = _has ? Math.Max(_value, value) : value;
                    break;
                case HistoryAggregation.Min:
                    _value = _has ? Math.Min(_value, value) : value;
                    break;
                default:
                    _value = value;
                    break;
            }

            _has = true;
        }
    }
}
