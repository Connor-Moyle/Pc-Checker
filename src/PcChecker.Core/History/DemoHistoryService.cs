using PcChecker.Core.Common;

namespace PcChecker.Core.History;

/// <summary>
/// Demo history: 30 days of generated samples for the sample desktop (see <see cref="DemoHistoryGenerator"/>). Nothing
/// is read from or written to disk; health scores recorded during the session are kept in memory.
/// </summary>
public sealed class DemoHistoryService : IHistoryService
{
    private readonly object _gate = new();
    private readonly List<HistorySample> _recorded = [];
    private IReadOnlyList<HistorySample>? _generated;
    private DateTimeOffset _generatedAt;

    public bool IsRecording { get; private set; }

    public string DataDirectory => Path.Combine(Settings.JsonSettingsService.DefaultDirectory(), "demo", "history");

    public DateTimeOffset? LastRecorded => Samples().LastOrDefault()?.Time;

    public event EventHandler? Recorded;

    public void Start() => IsRecording = true;

    public void Stop() => IsRecording = false;

    public void RecordHealthScore(DateTimeOffset captured, int score)
    {
        lock (_gate)
        {
            _recorded.Add(new HistorySample(captured, 0, new Dictionary<string, double> { [HistoryMetrics.Health] = Math.Clamp(score, 0, 100) }));
        }

        Recorded?.Invoke(this, EventArgs.Empty);
    }

    public async Task<HistoryData> LoadAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 150, 400).ConfigureAwait(false);
        var samples = Samples().Where(s => s.Time >= from && s.Time <= to).OrderBy(s => s.Time).ToList();
        return new HistoryData(from, to, samples);
    }

    public Task<HistoryStorageInfo> GetStorageInfoAsync(CancellationToken cancellationToken)
    {
        var samples = Samples();
        int days = samples.Select(s => HistoryRetention.DayOf(s.Time)).Distinct().Count();
        long bytes = samples.Count * 110L;
        return Task.FromResult(new HistoryStorageInfo(days, bytes, samples.Count == 0 ? null : HistoryRetention.DayOf(samples[0].Time)));
    }

    /// <summary>The generated month (refreshed every few minutes so "now" keeps moving) plus anything recorded this session.</summary>
    private IReadOnlyList<HistorySample> Samples()
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (_generated is null || now - _generatedAt > TimeSpan.FromMinutes(5))
            {
                _generated = DemoHistoryGenerator.Generate(now);
                _generatedAt = now;
            }

            return _recorded.Count == 0 ? _generated : _generated.Concat(_recorded).OrderBy(s => s.Time).ToList();
        }
    }
}
