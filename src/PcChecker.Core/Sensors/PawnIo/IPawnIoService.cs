using PcChecker.Core.Common;

namespace PcChecker.Core.Sensors.PawnIo;

/// <summary>
/// The PawnIO driver LibreHardwareMonitor needs for the CPU and the motherboard sensor chip (CPU and case fan headers):
/// whether it is installed and running, and a confirmed one-click install of the official signed release.
/// </summary>
public interface IPawnIoService
{
    /// <summary>Checks the uninstall entry, the driver service and the device. Cheap; never throws.</summary>
    PawnIoStatus GetStatus();

    /// <summary>True when the Windows Package Manager can be used for the install (otherwise the official setup is downloaded).</summary>
    bool CanUseWinget { get; }

    /// <summary>
    /// Installs PawnIO through winget when it is available, otherwise downloads PawnIO_setup.exe from the official GitHub
    /// releases, checks its signature and runs it silently. Journaled through the change guard without a restore point
    /// (the driver can be removed from Settings → Apps). The caller confirms first.
    /// </summary>
    Task<PawnIoInstallResult> InstallAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);
}
