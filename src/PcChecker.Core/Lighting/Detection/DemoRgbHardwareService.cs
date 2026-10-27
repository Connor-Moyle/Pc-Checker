using PcChecker.Core.Common;

namespace PcChecker.Core.Lighting;

/// <summary>
/// Demo detection for the sample gaming desktop: the ASUS board, graphics card and keyboard that the demo OpenRGB
/// server controls, plus a Corsair Commander Core hub and Corsair Vengeance RGB PRO memory that Corsair iCUE holds.
/// Runs the real detection rules on sample Windows device IDs, so the page shows exactly what a real scan would.
/// Pure .NET; nothing on the machine is touched.
/// </summary>
public sealed class DemoRgbHardwareService : IRgbHardwareService
{
    private readonly object _gate = new();
    private bool _icueClosed;
    private bool _openRgbInstalled = true;

    /// <summary>The sample Windows device list (the same ID formats Windows reports).</summary>
    public static RgbScanInput SampleInput { get; } = new(
        [
            new(@"USB\VID_0B05&PID_19AF\9876543210", [@"USB\VID_0B05&PID_19AF&REV_0100", @"USB\VID_0B05&PID_19AF"], "AURA LED Controller"),
            new(@"HID\VID_0B05&PID_19AF&MI_02\8&2D4BD25E&0&0000", [@"HID\VID_0B05&PID_19AF&REV_0100&MI_02"], "HID-compliant vendor-defined device"),
            new(@"USB\VID_1B1C&PID_0C1C\7&1E2F3A4B&0&3", [@"USB\VID_1B1C&PID_0C1C&REV_0100"], "iCUE COMMANDER CORE"),
            new(@"USB\VID_1B1C&PID_1BC4\0A1B2C3D", [@"USB\VID_1B1C&PID_1BC4&REV_0300"], "Corsair K70 RGB PRO Mechanical Gaming Keyboard"),
            new(@"PCI\VEN_10DE&DEV_2786&SUBSYS_88F31043&REV_A1\4&1F2E3D4C&0&0008", [@"PCI\VEN_10DE&DEV_2786&SUBSYS_88F31043&REV_A1"], DemoData.GpuName),
            new(@"PCI\VEN_8086&DEV_7A23&SUBSYS_86941043&REV_11\3&11583659&0&FC", [@"PCI\VEN_8086&DEV_7A23&SUBSYS_86941043&REV_11", @"PCI\VEN_8086&DEV_7A23&CC_0C0500"], "Intel(R) SMBus - 7A23"),
        ],
        [],
        [
            new("DIMM_A2", "Corsair", "CMW32GX4M2E3200C16"),
            new("DIMM_B2", "Corsair", "CMW32GX4M2E3200C16"),
        ],
        "ASUSTeK COMPUTER INC.",
        "ROG STRIX Z790-E GAMING WIFI");

    private static readonly RgbVendorApp Icue = RgbConflictRules.Catalog.Single(a => a.Id == "icue");

    public async Task<RgbScanResult> ScanAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 300, 700).ConfigureAwait(false);
        bool closed;
        lock (_gate)
        {
            closed = _icueClosed;
        }

        IReadOnlyList<RgbConflict> conflicts = closed
            ? []
            : [new RgbConflict(Icue, [new("iCUE", 6120, @"C:\Program Files\Corsair\Corsair iCUE5 Software\iCUE.exe"), new("Corsair.Service", 4412, null)], ["CorsairService"])];
        return new RgbScanResult(RgbDetectionRules.Build(SampleInput), conflicts, HasSmbusController: true, SmbusDriverReady: true,
            IsAdministrator: true, Problems: []);
    }

    public async Task<OperationResult> CloseForSessionAsync(RgbConflict conflict, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conflict);
        await DemoData.DelayAsync(cancellationToken, 300, 700).ConfigureAwait(false);
        lock (_gate)
        {
            if (conflict.App.Id == Icue.Id)
            {
                _icueClosed = true;
            }
        }

        return OperationResult.Ok($"{conflict.App.Product} is closed until the next restart (demo).");
    }

    public Task<bool> CanInstallOpenRgbAsync(CancellationToken cancellationToken) => Task.FromResult(true);

    public async Task<OperationResult> InstallOpenRgbAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new ProgressUpdate("Installing OpenRGB with winget (demo)", null, "winget install --id " + OpenRgbInstallRules.WingetPackageId));
        await DemoData.DelayAsync(cancellationToken, 600, 1200).ConfigureAwait(false);
        lock (_gate)
        {
            if (_openRgbInstalled)
            {
                return OperationResult.Ok("OpenRGB is already installed (demo).");
            }

            _openRgbInstalled = true;
        }

        return OperationResult.Ok("OpenRGB is installed (demo).");
    }
}
