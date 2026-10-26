namespace PcChecker.Core.Latency;

/// <summary>Action ids the Latency module's <c>IOptimizationActionProvider</c> handles.</summary>
public static class LatencyActionIds
{
    public const string HagsOn = "latency.hags.on";
    public const string HagsOff = "latency.hags.off";
    public const string UsbSelectiveSuspendOff = "latency.usb.selectivesuspend.off";
    public const string NicPowerSavingOff = "latency.nic.powersaving.off";
    public const string PcieAspmOff = "latency.pcie.aspm.off";
    public const string CoreParkingOff = "latency.coreparking.off";
    public const string PointerPrecisionOff = "latency.pointerprecision.off";
    public const string GameModeOn = "latency.gamemode.on";
    public const string HpetOff = "latency.hpet.off";
    public const string DynamicTickOff = "latency.dynamictick.off";
    public const string NagleOff = "latency.nagle.off";
    public const string MsiGpuOn = "latency.msi.gpu.on";
    public const string TimerResolutionHint = "latency.timerresolution.hint";

    public static IReadOnlyList<string> All { get; } =
    [
        HagsOn, HagsOff, UsbSelectiveSuspendOff, NicPowerSavingOff, PcieAspmOff, CoreParkingOff, PointerPrecisionOff,
        GameModeOn, HpetOff, DynamicTickOff, NagleOff, MsiGpuOn, TimerResolutionHint,
    ];
}
