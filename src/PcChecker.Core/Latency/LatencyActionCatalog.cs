using PcChecker.Core.Optimization;

namespace PcChecker.Core.Latency;

/// <summary>The optimization actions the Latency module contributes, with beginner-friendly text. Pure.</summary>
public static class LatencyActionCatalog
{
    public const string Category = "Latency";

    public static IReadOnlyList<OptimizationAction> All { get; } =
    [
        new(LatencyActionIds.HagsOn, "Turn on hardware-accelerated GPU scheduling",
            "Lets the graphics card manage its own work queue, which can shave a little input lag on recent NVIDIA and AMD cards. Needs a restart.",
            Category, RiskLevel.Safe, true, true, false),
        new(LatencyActionIds.HagsOff, "Turn off hardware-accelerated GPU scheduling",
            "Hands scheduling back to Windows. Try this if games stutter after turning it on. Needs a restart.",
            Category, RiskLevel.Safe, true, true, false),
        new(LatencyActionIds.UsbSelectiveSuspendOff, "Stop USB devices from sleeping",
            "Turns off USB selective suspend in the active power plan so mice, keyboards and headsets never need to wake up first.",
            Category, RiskLevel.Safe, true, false, false),
        new(LatencyActionIds.NicPowerSavingOff, "Turn off network adapter power saving",
            "Disables Energy-Efficient Ethernet, Green Ethernet and \"turn off this device to save power\" on your network adapters. Takes effect after the adapter reconnects.",
            Category, RiskLevel.Safe, true, true, false),
        new(LatencyActionIds.PcieAspmOff, "Turn off PCI Express power saving",
            "Sets link state power management to Off in the active power plan so the graphics card and NVMe drives stay fully awake.",
            Category, RiskLevel.Safe, true, false, false),
        new(LatencyActionIds.CoreParkingOff, "Stop CPU cores from parking",
            "Keeps every CPU core available (minimum unparked cores = 100%) so a sudden burst of work does not wait for a core to wake.",
            Category, RiskLevel.Safe, true, false, false),
        new(LatencyActionIds.PointerPrecisionOff, "Turn off Enhance pointer precision",
            "Disables mouse acceleration so the cursor always moves the same distance for the same hand movement. Most gamers prefer this.",
            Category, RiskLevel.Safe, true, false, false),
        new(LatencyActionIds.GameModeOn, "Turn on Game Mode",
            "Tells Windows to hold back driver installs and notifications while a game runs and to give the game more CPU time.",
            Category, RiskLevel.Safe, true, false, false),
        new(LatencyActionIds.HpetOff, "Stop forcing the HPET timer",
            "Removes the useplatformclock boot option so Windows uses the modern TSC timer. Only matters if someone set it earlier. Needs a restart.",
            Category, RiskLevel.Advanced, true, true, true),
        new(LatencyActionIds.DynamicTickOff, "Turn off dynamic tick",
            "Keeps the system timer ticking at a fixed rate instead of coalescing idle ticks. Can slightly reduce jitter; slightly raises idle power. Needs a restart.",
            Category, RiskLevel.Advanced, true, true, true),
        new(LatencyActionIds.NagleOff, "Turn off Nagle's algorithm",
            "Sends small network packets immediately instead of batching them (TcpAckFrequency=1, TCPNoDelay=1). Helps a few older games; makes no difference for most.",
            Category, RiskLevel.Advanced, true, true, true),
        new(LatencyActionIds.MsiGpuOn, "Use message-signaled interrupts for the GPU",
            "Switches the graphics card to MSI interrupt mode, which avoids shared interrupt lines. Most modern drivers already do this. Needs a restart.",
            Category, RiskLevel.Advanced, true, true, true),
        new(LatencyActionIds.TimerResolutionHint, "About timer resolution",
            "Information only. Windows 11 raises the timer rate per application when a game asks for it, so a coarse idle value is normal and nothing needs changing.",
            Category, RiskLevel.Safe, false, false, false),
    ];

    /// <summary>Looks an action up by id (case-insensitive) or returns null.</summary>
    public static OptimizationAction? Find(string? id) =>
        id is null ? null : All.FirstOrDefault(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when the Latency module owns this action id.</summary>
    public static bool Handles(string? id) => Find(id) is not null;
}
