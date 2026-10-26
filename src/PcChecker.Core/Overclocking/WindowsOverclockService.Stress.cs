using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;

namespace PcChecker.Core.Overclocking;

/// <summary>Stress test and crash-watchdog half of <see cref="WindowsOverclockService"/>.</summary>
public sealed partial class WindowsOverclockService
{
    public async Task<StressTestResult> RunStressTestAsync(StressTarget target, int seconds, double abortCpuTempC, double abortGpuTempC,
        IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        seconds = Math.Clamp(seconds, 5, 3 * 60 * 60);
        bool burnCpu = target is StressTarget.Cpu or StressTarget.Both;

        using var burnCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var runner = burnCpu ? new CpuStressRunner() : null;
        var burnTask = runner?.RunAsync(burnCts.Token);

        double? maxCpu = null, maxGpu = null;
        var cpuClocks = new List<double>();
        var cpuLoads = new List<double?>();
        var gpuClocks = new List<double>();
        var gpuLoads = new List<double?>();
        bool aborted = false;
        int elapsed = 0;
        var started = DateTime.UtcNow;

        try
        {
            while (elapsed < seconds)
            {
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                elapsed = (int)Math.Round((DateTime.UtcNow - started).TotalSeconds);

                var snapshot = _sensors.Latest;
                var live = SensorPicker.Pick(snapshot);
                double? cpuTemp = SensorPicker.MaxCpuTemp(snapshot);
                double? gpuTemp = SensorPicker.MaxGpuTemp(snapshot);
                maxCpu = Max(maxCpu, cpuTemp);
                maxGpu = Max(maxGpu, gpuTemp);
                if (live.CpuClockMhz is { } cc)
                {
                    cpuClocks.Add(cc);
                    cpuLoads.Add(burnCpu ? null : live.CpuLoadPercent);
                }

                if (live.GpuCoreClockMhz is { } gc)
                {
                    gpuClocks.Add(gc);
                    gpuLoads.Add(live.GpuLoadPercent);
                }

                string detail = BuildDetail(target, cpuTemp, gpuTemp, live, runner);
                progress?.Report(new ProgressUpdate("Stress test running", 100.0 * elapsed / seconds, detail));

                if ((cpuTemp is { } ct && ct >= abortCpuTempC) || (gpuTemp is { } gt && gt >= abortGpuTempC))
                {
                    aborted = true;
                    _logger.LogWarning("Stress test aborted by the watchdog: CPU {Cpu} °C, GPU {Gpu} °C.", cpuTemp, gpuTemp);
                    break;
                }
            }
        }
        finally
        {
            burnCts.Cancel();
        }

        StressRunStats stats = burnTask is null ? new StressRunStats(0, 0, 0) : await burnTask.ConfigureAwait(false);

        if (aborted && !_applied.IsStock)
        {
            progress?.Report(new ProgressUpdate("Reverting the overclock", null, "Temperature limit reached; returning to stock."));
            var revert = await ResetAsync(CancellationToken.None).ConfigureAwait(false);
            _logger.LogInformation("Watchdog revert: {Message}", revert.Message);
        }

        bool throttle = (burnCpu && ThrottleHeuristic.Suspect(cpuClocks, cpuLoads)) || (target != StressTarget.Cpu && ThrottleHeuristic.Suspect(gpuClocks, gpuLoads));
        bool passed = stats.Errors == 0 && !aborted && !throttle;
        string summary = ThrottleHeuristic.Summarize(target, stats.Errors, aborted, throttle, maxCpu, maxGpu, abortCpuTempC, abortGpuTempC);
        if (target == StressTarget.Gpu && !aborted)
        {
            summary += " (Guided GPU test: PC Checker monitored while you ran your own game or benchmark; it cannot generate GPU load itself.)";
        }

        return new StressTestResult(target, elapsed, maxCpu, maxGpu, throttle, stats.Errors, passed, summary, aborted);
    }

    public async Task<OperationResult> CheckUncleanShutdownAndRevertAsync(CancellationToken cancellationToken)
    {
        var session = OverclockSessionStore.Load(SessionPath);
        if (session is null)
        {
            if (!_settings.Current.Overclock.LastSessionClean)
            {
                _settings.Current.Overclock.LastSessionClean = true;
                _settings.Current.Overclock.ActiveProfileName = null;
                await _settings.SaveAsync().ConfigureAwait(false);
            }

            return OperationResult.Ok("No overclock session to recover.");
        }

        if (session.Clean)
        {
            // Profiles never survive a reboot (driver deltas are volatile); start fresh.
            OverclockSessionStore.Delete(SessionPath);
            _settings.Current.Overclock.ActiveProfileName = null;
            _settings.Current.Overclock.LastSessionClean = true;
            await _settings.SaveAsync().ConfigureAwait(false);
            return OperationResult.Ok("The last session ended cleanly.");
        }

        _logger.LogWarning("Unclean shutdown detected with overclock profile \"{Profile}\" applied at {When}; reverting.", session.Profile.Name, session.AppliedUtc);
        try
        {
            if (Capabilities is null)
            {
                await DetectAsync(cancellationToken).ConfigureAwait(false);
            }

            _applied = session.Profile;
            var reset = await ResetCoreAsync(cancellationToken).ConfigureAwait(false);
            OverclockSessionStore.Delete(SessionPath);
            return reset.Success
                ? OperationResult.Ok($"The previous overclock (\"{session.Profile.Name}\") was reverted after an unclean shutdown. {reset.Message}")
                : OperationResult.Ok($"The previous session ended uncleanly while \"{session.Profile.Name}\" was applied. Driver clock offsets do not survive a reboot, so the card is at stock; the explicit reset reported: {reset.Message}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reverting after an unclean shutdown failed.");
            OverclockSessionStore.Delete(SessionPath);
            return OperationResult.Fail("The previous overclock session ended uncleanly and the automatic reset failed: " + ex.Message, ex);
        }
    }

    public async Task MarkSessionCleanAsync()
    {
        try
        {
            var session = OverclockSessionStore.Load(SessionPath);
            if (session is not null && !session.Clean)
            {
                session.Clean = true;
                OverclockSessionStore.Save(SessionPath, session);
            }

            if (!_settings.Current.Overclock.LastSessionClean)
            {
                _settings.Current.Overclock.LastSessionClean = true;
                await _settings.SaveAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not mark the overclock session clean.");
        }
    }

    private static double? Max(double? a, double? b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);

    private static string BuildDetail(StressTarget target, double? cpuTemp, double? gpuTemp, LiveReadout live, CpuStressRunner? runner)
    {
        var parts = new List<string>();
        if (cpuTemp is { } c)
        {
            parts.Add($"CPU {c:0} °C" + (live.CpuClockMhz is { } cc ? $" @ {cc} MHz" : string.Empty));
        }

        if (gpuTemp is { } g)
        {
            parts.Add($"GPU {g:0} °C" + (live.GpuCoreClockMhz is { } gc ? $" @ {gc} MHz" : string.Empty));
        }

        if (runner is not null)
        {
            parts.Add($"{runner.Iterations} checks, {runner.Errors} errors");
        }

        if (target == StressTarget.Gpu)
        {
            parts.Add("run your game or benchmark now");
        }

        return parts.Count == 0 ? "No sensor data yet." : string.Join(" · ", parts);
    }
}
