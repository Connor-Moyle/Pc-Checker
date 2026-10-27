using PcChecker.Core.Sensors.PawnIo;

namespace PcChecker.Core.Sensors;

/// <summary>The "Some sensors are missing" notes on the Temperatures page, and the PawnIO problem texts. Pure.</summary>
public static class SensorWarnings
{
    /// <summary>
    /// Shown when PawnIO is missing. LibreHardwareMonitor reads the CPU and the motherboard sensor chip only through the
    /// signed PawnIO driver (it no longer bundles WinRing0, which Microsoft Defender quarantines), and it does not install it.
    /// </summary>
    public const string PawnIoMissing = "The PawnIO driver is not installed, so CPU temperatures and the motherboard sensor chip (CPU and case fan headers, VRM and chipset readings) cannot be read. "
        + "Click \"Install PawnIO\" on the Fans or Temperatures page (or install it from https://pawnio.eu, or run \"winget install namazso.PawnIO\"), then click \"Reload sensors\".";

    /// <summary>Shown when PawnIO is installed but its device cannot be opened (not started yet, or blocked).</summary>
    public const string PawnIoNotRunning = "The PawnIO driver is installed but not running, so CPU temperatures and the motherboard sensor chip (CPU and case fan headers) cannot be read. "
        + "Restart the PC, then click \"Reload sensors\". If it still does not start, reinstall PawnIO and check that antivirus software is not blocking it.";

    /// <summary>The driver problem for a PawnIO state, or null when nothing is known to be wrong.</summary>
    public static string? DriverProblem(PawnIoState state) => state switch
    {
        PawnIoState.NotInstalled => PawnIoMissing,
        PawnIoState.InstalledNotRunning => PawnIoNotRunning,
        _ => null,
    };

    /// <summary>
    /// Notes for partly available hardware. <paramref name="hasMotherboard"/> is true whenever the board was identified
    /// (from SMBIOS, which needs no driver); <paramref name="hasMotherboardChip"/> only when its Super I/O chip or embedded
    /// controller could actually be read, which is what the fan headers need.
    /// </summary>
    public static IReadOnlyList<string> Build(bool hasCpuTemp, bool hasMotherboard, bool hasMotherboardChip, bool hasGpu, string? driverProblem, string adminHint)
    {
        var notes = new List<string>();
        if (driverProblem is not null)
        {
            notes.Add(driverProblem);
        }

        if (!hasCpuTemp)
        {
            notes.Add("No CPU temperature sensor was found. " + (driverProblem is null ? adminHint : "Install PawnIO (see above)."));
        }

        if (!hasMotherboardChip)
        {
            notes.Add(driverProblem is not null
                ? "The motherboard sensor chip cannot be read until PawnIO runs, so CPU and case fan headers, VRM and chipset readings are missing. Only graphics card fans can be shown."
                : hasMotherboard
                    ? "No motherboard sensor chip was found, so case fan headers, VRM and chipset readings are not available. The chip may not be supported yet, or another monitoring program may be holding it (see \"Why is a fan missing?\" on the Fans page)."
                    : "No motherboard sensor chip was detected, so VRM, chipset and case fan readings are not available on this board.");
        }

        if (!hasGpu)
        {
            notes.Add("No graphics card sensors were found. Install the latest graphics driver if a dedicated GPU is present.");
        }

        return notes;
    }
}
