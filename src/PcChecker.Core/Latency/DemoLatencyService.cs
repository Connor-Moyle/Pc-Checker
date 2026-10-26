using PcChecker.Core.Common;

namespace PcChecker.Core.Latency;

/// <summary>Demo latency check with a believable mix of good values and a few things to fix.</summary>
public sealed class DemoLatencyService : ILatencyService
{
    public LatencyReport? Last { get; private set; }

    public async Task<LatencyReport> AnalyzeAsync(int sampleSeconds, bool includeNetwork, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        int seconds = Math.Clamp(sampleSeconds, 1, 10);
        for (int i = 0; i < seconds; i++)
        {
            progress?.Report(new ProgressUpdate("Sampling driver and interrupt load", 80.0 * i / seconds, $"{i + 1}/{seconds} s"));
            await Task.Delay(300, cancellationToken).ConfigureAwait(false);
        }

        var network = includeNetwork
            ? await PingTestAsync([("gateway", "Your router"), ("1.1.1.1", "Cloudflare DNS"), ("8.8.8.8", "Google DNS")], 10, cancellationToken).ConfigureAwait(false)
            : [];
        progress?.Report(new ProgressUpdate("Evaluating", 95));

        var facts = new LatencyReport(0.6, 0.3, 4200, 9800, 1.0, 0.5, false, false, false, "Balanced", true, true, true, true, true, 1000, 14,
            network, [], 0, DateTimeOffset.Now, []);
        var findings = LatencyRules.BuildFindings(facts, hasNvidiaGpu: true);
        Last = facts with { Findings = findings, Score = LatencyRules.Score(findings) };
        return Last;
    }

    public async Task<IReadOnlyList<NetworkLatencyResult>> PingTestAsync(IEnumerable<(string Target, string Label)> targets, int count, CancellationToken cancellationToken)
    {
        var random = DemoData.CreateRandom(7);
        var results = new List<NetworkLatencyResult>();
        foreach (var (target, label) in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(120, cancellationToken).ConfigureAwait(false);
            double baseMs = target == "gateway" ? 0.8 : target.StartsWith('1') ? 9.5 : 12.3;
            var samples = Enumerable.Range(0, Math.Max(1, count)).Select(_ => Math.Round(DemoData.Jitter(random, baseMs, baseMs * 0.15), 2)).ToList();
            results.Add(new NetworkLatencyResult(target, label, Math.Round(samples.Average(), 2), samples.Min(), samples.Max(),
                Math.Round(LatencyRules.Jitter(samples) ?? 0, 2), 0, samples.Count));
        }

        return results;
    }

    public async Task<double?> MeasureDnsAsync(string host, CancellationToken cancellationToken)
    {
        await Task.Delay(80, cancellationToken).ConfigureAwait(false);
        return 14;
    }
}
