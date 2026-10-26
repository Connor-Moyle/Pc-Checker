using PcChecker.Core.Common;
using PcChecker.Core.Optimization;

namespace PcChecker.Core.Latency;

/// <summary>
/// Demo latency check for the sample gaming desktop: healthy DPC/ISR load, a fast wired connection and a few settings
/// worth fixing. When a <see cref="DemoLatencyActionsProvider"/> is registered, fixes applied through it change the
/// next report so the page feels real.
/// </summary>
public sealed class DemoLatencyService : ILatencyService
{
    private readonly DemoLatencyActionsProvider? _provider;

    public DemoLatencyService()
    {
    }

    public DemoLatencyService(IEnumerable<IOptimizationActionProvider> providers)
    {
        _provider = providers?.OfType<DemoLatencyActionsProvider>().FirstOrDefault();
    }

    public LatencyReport? Last { get; private set; }

    public async Task<LatencyReport> AnalyzeAsync(int sampleSeconds, bool includeNetwork, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        int seconds = Math.Clamp(sampleSeconds, 1, 10);
        var random = DemoData.CreateRandom(seconds * 31 + 7);
        for (int i = 0; i < seconds; i++)
        {
            progress?.Report(new ProgressUpdate("Sampling driver and interrupt load", 50.0 * i / seconds, $"{i + 1}/{seconds} s"));
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        progress?.Report(new ProgressUpdate("Reading timer, boot and power settings", 60));
        await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        progress?.Report(new ProgressUpdate("Measuring mouse polling rate", 70, "Move the mouse in circles for a few seconds"));
        await Task.Delay(200, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<NetworkLatencyResult> network = [];
        double? dns = null;
        if (includeNetwork)
        {
            progress?.Report(new ProgressUpdate("Pinging your router and public DNS servers", 80));
            network = await PingTestAsync([("gateway", "Your router"), ("1.1.1.1", "Cloudflare DNS"), ("8.8.8.8", "Google DNS")], 10, cancellationToken).ConfigureAwait(false);
            progress?.Report(new ProgressUpdate("Timing a DNS lookup", 92));
            dns = await MeasureDnsAsync("www.microsoft.com", cancellationToken).ConfigureAwait(false);
        }

        progress?.Report(new ProgressUpdate("Evaluating", 97));
        bool hagsOn = Applied(LatencyActionIds.HagsOn) || !Applied(LatencyActionIds.HagsOff);
        var facts = new LatencyReport(
            DpcTimePercent: Math.Round(DemoData.Jitter(random, 0.6, 0.15), 2),
            InterruptTimePercent: Math.Round(DemoData.Jitter(random, 0.3, 0.08), 2),
            DpcsQueuedPerSec: Math.Round(DemoData.Jitter(random, 4200, 300)),
            InterruptsPerSec: Math.Round(DemoData.Jitter(random, 9800, 600)),
            TimerResolutionMs: 1.0,
            TimerResolutionMinMs: 0.5,
            HpetForced: false,
            DynamicTickDisabled: Applied(LatencyActionIds.DynamicTickOff),
            HagsEnabled: hagsOn,
            PowerPlan: "Balanced",
            CoreParkingAllowed: !Applied(LatencyActionIds.CoreParkingOff),
            UsbSelectiveSuspend: !Applied(LatencyActionIds.UsbSelectiveSuspendOff),
            PcieAspmEnabled: !Applied(LatencyActionIds.PcieAspmOff),
            GameMode: true,
            PointerPrecision: !Applied(LatencyActionIds.PointerPrecisionOff),
            MousePollingHz: 1000,
            DnsMs: dns,
            Network: network,
            Findings: [],
            Score: 0,
            Captured: DateTimeOffset.Now,
            Warnings: ["Sample data: this is the demo build of PC Checker, nothing on this PC was measured."]);
        var findings = LatencyRules.BuildFindings(facts, hasNvidiaGpu: true, nicPowerSaving: !Applied(LatencyActionIds.NicPowerSavingOff));
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
            results.Add(NetworkLatencyMath.Summarize(target, label, samples, 0));
        }

        return results;
    }

    public async Task<double?> MeasureDnsAsync(string host, CancellationToken cancellationToken)
    {
        await Task.Delay(80, cancellationToken).ConfigureAwait(false);
        return 14;
    }

    private bool Applied(string actionId) => _provider?.IsApplied(actionId) == true;
}
