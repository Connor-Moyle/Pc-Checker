using PcChecker.Core.Common;
using PcChecker.Core.Optimization;

namespace PcChecker.Core.Latency;

/// <summary>Turns measured facts into findings and a 0–100 score. Pure.</summary>
public static class LatencyRules
{
    /// <summary>DPC time above this share of CPU time points at a misbehaving driver.</summary>
    public const double DpcHighPercent = 3;

    /// <summary>Interrupt time above this share of CPU time is unusual on a healthy desktop.</summary>
    public const double IsrHighPercent = 1;

    /// <summary>A DNS lookup slower than this makes every new connection feel sluggish.</summary>
    public const double DnsSlowMs = 100;

    /// <summary>Findings for a report whose measurements are filled in (its own Findings/Score are ignored).</summary>
    public static IReadOnlyList<LatencyFinding> BuildFindings(LatencyReport facts, bool hasNvidiaGpu = false, bool? nicPowerSaving = null)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var list = new List<LatencyFinding>();

        if (facts.DpcTimePercent is > DpcHighPercent)
        {
            list.Add(new("latency.dpc.high", Severity.High, "High driver (DPC) load",
                $"Drivers spend {facts.DpcTimePercent:0.0}% of CPU time in deferred procedure calls. Over {DpcHighPercent:0}% usually means a misbehaving network, audio or GPU driver. Update those drivers first, then use LatencyMon to find the exact one.", null, RiskLevel.Safe, false));
        }

        if (facts.InterruptTimePercent is > IsrHighPercent)
        {
            list.Add(new("latency.isr.high", Severity.Medium, "High interrupt load",
                $"{facts.InterruptTimePercent:0.0}% of CPU time is spent servicing interrupts. Check for outdated chipset, storage and network drivers.", null, RiskLevel.Safe, false));
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

        if (facts.PowerPlan is { } plan && (plan.Contains("Balanced", StringComparison.OrdinalIgnoreCase) || plan.Contains("Power saver", StringComparison.OrdinalIgnoreCase)))
        {
            list.Add(new("latency.powerplan.balanced", Severity.Low, $"{plan} power plan",
                "This plan lets the CPU clock down between frames. High performance keeps it ready.", "power.highperformance", RiskLevel.Safe, false));
        }

        if (facts.CoreParkingAllowed == true)
        {
            list.Add(new("latency.coreparking.on", Severity.Low, "CPU cores can be parked",
                "Windows may put idle cores to sleep and wake them when work arrives, which adds a short delay to sudden bursts of work.", LatencyActionIds.CoreParkingOff, RiskLevel.Safe, false));
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

        if (nicPowerSaving == true)
        {
            list.Add(new("latency.nic.powersaving", Severity.Low, "Network adapter power saving is on",
                "Energy-Efficient Ethernet, Green Ethernet or \"turn off this device to save power\" is enabled on a network adapter. These can add wake-up delay and jitter.", LatencyActionIds.NicPowerSavingOff, RiskLevel.Safe, false));
        }

        if (facts.GameMode == false)
        {
            list.Add(new("latency.gamemode.off", Severity.Info, "Game Mode is off",
                "Game Mode stops Windows Update driver installs and notifications while you play and prioritises the game.", LatencyActionIds.GameModeOn, RiskLevel.Safe, false));
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

        if (facts.DnsMs is > DnsSlowMs)
        {
            list.Add(new("latency.dns.slow", Severity.Low, "Slow DNS lookups",
                $"Looking up a web address took {facts.DnsMs:0} ms. A faster DNS server (for example Cloudflare 1.1.1.1) makes every new connection start sooner.", "dns.cloudflare", RiskLevel.Safe, false));
        }

        foreach (var n in facts.Network ?? [])
        {
            if (n.AvgMs is null && n.Sent > 0)
            {
                list.Add(new($"latency.network.{n.Target}", Severity.Medium, $"No reply from {n.Label}",
                    $"{n.Target} did not answer any of {n.Sent} pings. Some routers and networks block ping, so this is only a problem if other targets also fail.", null, RiskLevel.Safe, false));
            }
            else if (n.LossPercent > 0 || n.JitterMs is > NetworkLatencyMath.UnstableJitterMs)
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

    /// <summary>One-word grade for the score hero.</summary>
    public static string Grade(int score) => score switch
    {
        >= 90 => "Excellent",
        >= 75 => "Good",
        >= 55 => "Fair",
        _ => "Needs attention",
    };

    /// <summary>Good / Warning / Critical for the score colour.</summary>
    public static HealthStatus Status(int score) => score switch
    {
        >= 75 => HealthStatus.Good,
        >= 55 => HealthStatus.Warning,
        _ => HealthStatus.Critical,
    };

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

    /// <summary>Average of counter samples, ignoring the first (which is always 0 for rate counters) and NaNs.</summary>
    public static double? AverageSamples(IReadOnlyList<float> samples, bool skipFirst = true)
    {
        if (samples is null || samples.Count == 0)
        {
            return null;
        }

        var usable = samples.Skip(skipFirst && samples.Count > 1 ? 1 : 0).Where(s => !float.IsNaN(s) && !float.IsInfinity(s)).ToList();
        return usable.Count == 0 ? null : Math.Round(usable.Average(), 2);
    }

    /// <summary>Interprets the HwSchMode registry value: 2 = on, 1 = off, anything else = unknown.</summary>
    public static bool? HagsFromRegistry(int? hwSchMode) => hwSchMode switch
    {
        2 => true,
        1 => false,
        _ => null,
    };
}
