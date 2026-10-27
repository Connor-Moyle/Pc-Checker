using PcChecker.Core.Common;

namespace PcChecker.Core.Lighting;

/// <summary>
/// Finds RGB hardware without OpenRGB (USB/HID IDs, graphics card IDs, RGB memory part numbers, motherboard brand and
/// Windows Dynamic Lighting devices), spots vendor RGB apps that hold devices, and offers the two optional fixes:
/// closing such an app for this session and installing OpenRGB.
/// </summary>
public interface IRgbHardwareService
{
    /// <summary>Runs the detection pass. Read-only and safe to repeat; never throws for a failed step (it is listed in Problems).</summary>
    Task<RgbScanResult> ScanAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Closes the app's processes and stops its running services until the next restart (start-up settings are not
    /// changed). Goes through the change guard; the caller confirms first.
    /// </summary>
    Task<OperationResult> CloseForSessionAsync(RgbConflict conflict, CancellationToken cancellationToken);

    /// <summary>True when OpenRGB can be installed here with the Windows Package Manager.</summary>
    Task<bool> CanInstallOpenRgbAsync(CancellationToken cancellationToken);

    /// <summary>Installs OpenRGB for all users with winget (package <see cref="OpenRgbInstallRules.WingetPackageId"/>). The caller confirms first.</summary>
    Task<OperationResult> InstallOpenRgbAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);
}
