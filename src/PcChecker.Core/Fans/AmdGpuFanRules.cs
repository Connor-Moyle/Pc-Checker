using System.Text.RegularExpressions;

namespace PcChecker.Core.Fans;

/// <summary>
/// Pure rule for Radeon fan control. LibreHardwareMonitor drives the AMD "GPU Fan" control through Overdrive5 only;
/// Radeon RX 5000 and newer (Overdrive8: RDNA1–4, Radeon VII, Radeon Pro W5000+) ignore those calls, so the control
/// exists but the fan never changes. Those cards are listed read-only on the Fans page and their fan is set through
/// the Overdrive8 fan curve on the Overclock page (or AMD Software).
/// </summary>
public static partial class AmdGpuFanRules
{
    public const string ReadOnlyNote =
        "Radeon RX 5000 and newer ignore the fan commands the hardware monitor sends, so this fan is shown read-only. Set it on the Overclock page (Overdrive8 fan curve) or in AMD Software → Performance → Tuning.";

    [GeneratedRegex(@"\bRX\s*[5-9]\d{3}(?!\d)|Radeon\s+VII\b|\bPro\s+W[5-9]\d{3}(?!\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Overdrive8Name();

    /// <summary>True for Radeon cards that use Overdrive8 (fan control through the host does not work).</summary>
    public static bool IsOverdrive8Card(string? gpuName) =>
        !string.IsNullOrWhiteSpace(gpuName) && Overdrive8Name().IsMatch(gpuName);

    /// <summary>True when a GPU fan control from the shared hardware monitor must not be offered as controllable.</summary>
    public static bool HostControlIgnored(string? hardwareName, bool isAmdGpu) => isAmdGpu && IsOverdrive8Card(hardwareName);
}
