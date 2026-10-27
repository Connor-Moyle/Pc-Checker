using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Input;
using PcChecker.Core.Settings;

namespace PcChecker.Core.Latency;

/// <summary>
/// Windows implementation of <see cref="ILatencyService"/>: samples the processor DPC/interrupt counters, reads the
/// kernel timer resolution, boot and power settings, Game Mode and mouse acceleration, measures the mouse polling rate
/// and pings the router and public DNS servers. Every probe is guarded; anything that fails becomes a warning.
/// </summary>
public sealed class WindowsLatencyService : ILatencyService
{
    private const string DnsProbeHost = "www.microsoft.com";
    private const int PollingSeconds = 3;

    private readonly IMousePollingMeter _pollingMeter;
    private readonly ISettingsService _settings;
    private readonly ILogger<WindowsLatencyService> _logger;

    public WindowsLatencyService(IMousePollingMeter pollingMeter, ISettingsService settings, ILogger<WindowsLatencyService> logger)
    {
        _pollingMeter = pollingMeter;
        _settings = settings;
        _logger = logger;
    }

    public LatencyReport? Last { get; private set; }

    public Task<LatencyReport> AnalyzeAsync(int sampleSeconds, bool includeNetwork, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            Last = LatencyReport.Empty("Latency checks need Windows.");
            return Task.FromResult(Last);
        }

        // Every probe below (PerformanceCounter construction loads perflib/PDH, registry and WMI reads) can block for
        // seconds, so none of it may run on the caller's (UI) thread before the first real await.
        return Task.Run(() => AnalyzeCoreAsync(sampleSeconds, includeNetwork, progress, cancellationToken), cancellationToken);
    }

    private async Task<LatencyReport> AnalyzeCoreAsync(int sampleSeconds, bool includeNetwork, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        sampleSeconds = Math.Clamp(sampleSeconds, 1, 120);
        var warnings = new List<string>();
        var stopwatch = Stopwatch.StartNew();

        // 1. Processor counters (the longest step).
        progress?.Report(new ProgressUpdate("Sampling driver and interrupt load", 0, $"{sampleSeconds} s"));
        var counters = await SampleCountersAsync(sampleSeconds, progress, warnings, cancellationToken).ConfigureAwait(false);

        // 2. Timer, boot and GPU scheduling.
        progress?.Report(new ProgressUpdate("Reading timer and boot settings", 55));
        var timer = LatencyProbes.QueryTimerResolution();
        if (timer is null)
        {
            warnings.Add("The kernel timer resolution could not be read.");
        }

        var boot = await LatencyProbes.ReadBootConfigAsync(cancellationToken).ConfigureAwait(false);
        if (boot is null)
        {
            warnings.Add("bcdedit could not be read (it needs administrator rights), so HPET and dynamic tick are unknown.");
        }

        bool? hpetForced = boot is null ? null : BcdEditParser.GetFlag(boot, "useplatformclock");
        bool? dynamicTickDisabled = boot is null ? null : BcdEditParser.GetFlag(boot, "disabledynamictick");
        if (boot is not null && (hpetForced is null || dynamicTickDisabled is null))
        {
            warnings.Add("bcdedit printed a Yes/No value in a language PC Checker does not recognise, so HPET or dynamic tick is unknown.");
        }
        bool? hags = LatencyProbes.ReadHags();
        bool hasNvidia = LatencyProbes.HasNvidiaGpu();

        // 3. Power plan settings.
        progress?.Report(new ProgressUpdate("Reading power plan settings", 65));
        var (planId, planName) = await LatencyProbes.ReadActiveSchemeAsync(cancellationToken).ConfigureAwait(false);
        if (planName is null)
        {
            warnings.Add("powercfg did not report the active power plan.");
        }

        var usb = await LatencyProbes.ReadPowerSettingAsync(LatencyProbes.UsbSubgroup, LatencyProbes.UsbSelectiveSuspendSetting, cancellationToken).ConfigureAwait(false);
        var aspm = await LatencyProbes.ReadPowerSettingAsync(LatencyProbes.PciSubgroup, LatencyProbes.PciAspmSetting, cancellationToken).ConfigureAwait(false);
        var parking = await LatencyProbes.ReadPowerSettingAsync(LatencyProbes.ProcessorSubgroup, LatencyProbes.CoreParkingMinCoresSetting, cancellationToken).ConfigureAwait(false);
        bool? usbSuspend = usb.Ac is { } u ? u != 0 : null;
        bool? aspmEnabled = aspm.Ac is { } a ? a != 0 : null;
        bool? coreParking = parking.Ac is { } p ? p < 100 : null;

        // 4. Game Mode, mouse acceleration, network adapter power saving.
        bool? gameMode = LatencyProbes.ReadGameMode();
        bool? pointerPrecision = LatencyProbes.ReadPointerPrecision();
        var adapterStates = LatencyProbes.ReadNetworkAdapters().Select(a => a.State).ToList();
        bool? nicPowerSaving = adapterStates.Count == 0 ? null : NicPowerRules.AnyPowerSaving(adapterStates);
        foreach (var adapter in adapterStates)
        {
            _logger.LogDebug("Adapter {Name}: power management allowed {Pm}, EEE {Eee}, Green Ethernet {Green}, interrupt moderation {Im}, flow control {Fc}.",
                adapter.Name, adapter.PowerManagementAllowed, adapter.EnergyEfficientEthernet, adapter.GreenEthernet, adapter.InterruptModeration, adapter.FlowControl);
        }

        // 5. Mouse polling rate.
        progress?.Report(new ProgressUpdate("Measuring mouse polling rate", 72, "Move the mouse in circles for a few seconds"));
        int? pollingHz = await MeasurePollingAsync(warnings, cancellationToken).ConfigureAwait(false);

        // 6. Network.
        IReadOnlyList<NetworkLatencyResult> network = [];
        double? dnsMs = null;
        if (includeNetwork)
        {
            progress?.Report(new ProgressUpdate("Pinging your router and public DNS servers", 80));
            var targets = _settings.Current.Latency.PingTargets
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => (t.Trim(), NetworkLatencyProbe.LabelFor(t.Trim())))
                .ToList();
            if (targets.Count == 0)
            {
                targets = [(NetworkLatencyProbe.GatewayToken, "Your router"), ("1.1.1.1", "Cloudflare DNS"), ("8.8.8.8", "Google DNS")];
            }

            network = await PingTestAsync(targets, _settings.Current.Latency.PingCount, cancellationToken).ConfigureAwait(false);
            progress?.Report(new ProgressUpdate("Timing a DNS lookup", 92));
            dnsMs = await MeasureDnsAsync(DnsProbeHost, cancellationToken).ConfigureAwait(false);
            if (dnsMs is null)
            {
                warnings.Add($"The DNS lookup of {DnsProbeHost} failed, so DNS speed is unknown. Are you online?");
            }
        }

        progress?.Report(new ProgressUpdate("Evaluating", 97));
        var facts = new LatencyReport(
            counters.Dpc, counters.Isr, counters.DpcsPerSec, counters.InterruptsPerSec,
            timer?.CurrentMs, timer?.MinMs,
            hpetForced, dynamicTickDisabled, hags, planName, coreParking, usbSuspend, aspmEnabled,
            gameMode, pointerPrecision, pollingHz, dnsMs, network, [], 0, DateTimeOffset.Now, warnings)
        {
            PowerPlanId = planId,
        };
        var findings = LatencyRules.BuildFindings(facts, hasNvidia, nicPowerSaving, adapterStates);
        Last = facts with { Findings = findings, Score = LatencyRules.Score(findings) };
        _logger.LogInformation("Latency check finished in {Elapsed:0.0} s: score {Score}, {Findings} findings, {Warnings} warnings.",
            stopwatch.Elapsed.TotalSeconds, Last.Score, findings.Count, warnings.Count);
        return Last;
    }

    public async Task<IReadOnlyList<NetworkLatencyResult>> PingTestAsync(IEnumerable<(string Target, string Label)> targets, int count, CancellationToken cancellationToken)
    {
        var results = new List<NetworkLatencyResult>();
        foreach (var (target, label) in targets ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await NetworkLatencyProbe.PingAsync(target, label, count, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Ping {Target}: avg {Avg} ms, jitter {Jitter} ms, loss {Loss}%", target, result.AvgMs, result.JitterMs, result.LossPercent);
            results.Add(result);
        }

        return results;
    }

    public Task<double?> MeasureDnsAsync(string host, CancellationToken cancellationToken) =>
        NetworkLatencyProbe.MeasureDnsAsync(host, cancellationToken);

    private async Task<int?> MeasurePollingAsync(List<string> warnings, CancellationToken cancellationToken)
    {
        try
        {
            int hz = await _pollingMeter.MeasureAsync(PollingSeconds, null, cancellationToken).ConfigureAwait(false);
            if (hz <= 0)
            {
                warnings.Add("The mouse polling rate could not be measured. Move the mouse during the check to measure it.");
                return null;
            }

            return hz;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Mouse polling measurement failed.");
            warnings.Add("The mouse polling rate could not be measured: " + ex.Message);
            return null;
        }
    }

    /// <summary>Averages the four processor counters over <paramref name="seconds"/> samples (first value discarded).</summary>
    private async Task<(double? Dpc, double? Isr, double? DpcsPerSec, double? InterruptsPerSec)> SampleCountersAsync(
        int seconds, IProgress<ProgressUpdate>? progress, List<string> warnings, CancellationToken cancellationToken)
    {
        PerformanceCounter? dpc = null, isr = null, dpcs = null, interrupts = null;
        try
        {
            // Counter construction and priming hit perflib/PDH and the registry; keep them off the caller's thread.
            await Task.Run(() =>
            {
                dpc = new PerformanceCounter("Processor", "% DPC Time", "_Total", true);
                isr = new PerformanceCounter("Processor", "% Interrupt Time", "_Total", true);
                dpcs = new PerformanceCounter("Processor", "DPCs Queued/sec", "_Total", true);
                interrupts = new PerformanceCounter("Processor", "Interrupts/sec", "_Total", true);
                foreach (var c in new[] { dpc, isr, dpcs, interrupts })
                {
                    c.NextValue();
                }
            }, cancellationToken).ConfigureAwait(false);

            var dpcSamples = new List<float>();
            var isrSamples = new List<float>();
            var dpcsSamples = new List<float>();
            var intSamples = new List<float>();
            for (int i = 0; i < seconds; i++)
            {
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                dpcSamples.Add(dpc!.NextValue());
                isrSamples.Add(isr!.NextValue());
                dpcsSamples.Add(dpcs!.NextValue());
                intSamples.Add(interrupts!.NextValue());
                progress?.Report(new ProgressUpdate("Sampling driver and interrupt load", 50.0 * (i + 1) / seconds, $"{i + 1}/{seconds} s"));
            }

            return (LatencyRules.AverageSamples(dpcSamples, false), LatencyRules.AverageSamples(isrSamples, false),
                LatencyRules.AverageSamples(dpcsSamples, false), LatencyRules.AverageSamples(intSamples, false));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Processor performance counters are unavailable.");
            warnings.Add("The processor performance counters could not be read, so DPC and interrupt load are unknown. " +
                         "If this keeps happening, run 'lodctr /R' from an administrator command prompt to rebuild the counters. (" + ex.Message + ")");
            return (null, null, null, null);
        }
        finally
        {
            dpc?.Dispose();
            isr?.Dispose();
            dpcs?.Dispose();
            interrupts?.Dispose();
        }
    }

}
