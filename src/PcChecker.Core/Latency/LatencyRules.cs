using PcChecker.Core.Common;
using PcChecker.Core.Optimization;

namespace PcChecker.Core.Latency;

/// <summary>Turns measured facts into findings and a 0–100 score. Pure.</summary>
public static class LatencyRules
{
    /// <summary>Findings for a report whose measurements are filled in (its own Findings/Score are ignored).</summary>
    public static IReadOnlyList<LatencyFinding> BuildFindings(LatencyReport facts, bool hasNvidiaGpu = false)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var list = new List<LatencyFinding>();

        if (facts.DpcTimePercent is > 3)
        {
            list.Add(new("latency.dpc.high", Severity.High, "High driver (DPC) load",
                $"Drivers spend {facts.DpcTimePercent:0.0}% of CPU time in deferred procedure calls. Over 3% usually means a misbehaving network, audio or GPU driver.", null, RiskLevel.Safe, false));
        }

        if (facts.InterruptTimePercent is > 1)
        {
            list.Add(new("latency.isr.high", Severity.Medium, "High interrupt load",
                $"{facts.InterruptTimePercent:0.0}% of CPU time is spent servicing interrupts. Check for outdated drivers.", null, RiskLevel.Safe, false));
        }

        if (facts.TimerResolutionMs is > 1)
        {
            list.Add(new("latency.timer.coarse", Severity.Info, "Timer resolution is coarse",
                $"The system timer is at {facts.TimerResolutionMs:0.00} ms. Windows 11 raises it per application when needed, so this is normal when idle.", LatencyActionIds.TimerResolutionHint, RiskLevel.Safe, false));
        }

        if (facts.HpetForced == true)
        {
            list.Add(new("latency.hpet.forced", Severity.Medium, "HPET is forced on",
                "useplatformclock is set, which can add latency on modern systems. Removing it restores the default timer.", LatencyActionIds.HpetOff, RiskLevel.Advanced, true));
        }

        if (hasNvidiaGpu && facts.HagsEnabled == false)
        {
            list.Add(new("latency.hags.off", Severity.Info, "Hardware-accelerated GPU scheduling is off",
                "Turning it on can lower input latency slightly on recent NVIDIA GPUs.", LatencyActionIds.HagsOn, RiskLevel.Safe, false));
        }

        if (facts.PowerPlan is { } plan && plan.Contains("Balanced", StringComparison.OrdinalIgnoreCase))
        {
            list.Add(new("latency.powerplan.balanced", Severity.Low, "Balanced power plan",
                "The Balanced plan lets the CPU clock down between frames. High performance keeps it ready.", "power.highperformance", RiskLevel.Safe, false));
        }

        if (facts.UsbSelectiveSuspend == true)
        {
            list.Add(new("latency.usb.suspend", Severity.Info, "USB selective suspend is on",
                "Windows may power down USB devices between uses, which can add wake-up delay for mice and keyboards.", LatencyActionIds.UsbSelectiveSuspendOff, RiskLevel.Safe, false));
        }

        if (facts.PcieAspmEnabled == true)
        {
            list.Add(new("latency.pcie.aspm", Severity.Low, "PCI Express power saving is on",
                "Link state power management can add small delays for the GPU and NVMe drives.", LatencyActionIds.PcieAspmOff, RiskLevel.Safe, false));
        }

        if (facts.PointerPrecision == true)
        {
            list.Add(new("latency.pointer.precision", Severity.Info, "Enhance pointer precision is on",
                "This is mouse acceleration: the cursor moves further when you move faster. Most gamers turn it off for consistent aim.", LatencyActionIds.PointerPrecisionOff, RiskLevel.Safe, false));
        }

        if (facts.MousePollingHz is > 0 and < 500)
        {
            list.Add(new("latency.polling.low", Severity.Info, "Low mouse polling rate",
                $"The mouse reports about {facts.MousePollingHz} times per second. Gaming mice support 1000 Hz or more; set it in the vendor software.", null, RiskLevel.Safe, false));
        }

        foreach (var n in facts.Network ?? [])
        {
            if (n.LossPercent > 0 || n.JitterMs is > 10)
            {
                list.Add(new($"latency.network.{n.Target}", Severity.High, $"Unstable connection to {n.Label}",
                    $"{n.LossPercent:0.#}% packet loss and {n.JitterMs ?? 0:0.#} ms jitter. Check cables, Wi-Fi signal or the router.", null, RiskLevel.Safe, false));
            }
        }

        return list;
    }

    /// <summary>100 minus a weight per finding (Critical 30, High 20, Medium 12, Low 6, Info 2), never below 0.</summary>
    public static int Score(IEnumerable<LatencyFinding> findings)
    {
        int score = 100;
        foreach (var f in findings ?? [])
        {
            score -= f.Severity switch
            {
                Severity.Critical => 30,
                Severity.High => 20,
                Severity.Medium => 12,
                Severity.Low => 6,
                _ => 2,
            };
        }

        return Math.Clamp(score, 0, 100);
    }

    /// <summary>Mean absolute difference between consecutive samples.</summary>
    public static double? Jitter(IReadOnlyList<double> roundTripsMs)
    {
        if (roundTripsMs is null || roundTripsMs.Count < 2)
        {
            return null;
        }

        double sum = 0;
        for (int i = 1; i < roundTripsMs.Count; i++)
        {
            sum += Math.Abs(roundTripsMs[i] - roundTripsMs[i - 1]);
        }

        return sum / (roundTripsMs.Count - 1);
    }

    /// <summary>Converts a timer value in 100-nanosecond units to milliseconds.</summary>
    public static double HundredNanosecondsToMs(long value) => value / 10_000.0;
}
