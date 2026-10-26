using PcChecker.Core.Common;

namespace PcChecker.Core.Drivers;

/// <summary>Demo drivers: a realistic list with one old driver and one optional update available.</summary>
public sealed class DemoDriverService : IDriverService
{
    public async Task<DriverScanResult> ScanInstalledAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new ProgressUpdate("Reading installed drivers", 20));
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        var drivers = new List<DriverInfo>
        {
            new(DemoData.GpuName, @"PCI\VEN_10DE&DEV_2786", "Display", "NVIDIA", "32.0.15.6094", new DateTime(2025, 2, 10), "oem12.inf", true, "NVIDIA Corporation", false, [@"PCI\VEN_10DE&DEV_2786"], "OK", 0, "PCI bus 1"),
            new("Intel Ethernet Controller I226-V", @"PCI\VEN_8086&DEV_125C", "Net", "Intel", "2.1.4.3", new DateTime(2024, 11, 5), "oem7.inf", true, "Intel Corporation", false, [@"PCI\VEN_8086&DEV_125C"], "OK", 0, "PCI bus 6"),
            new("Intel Wi-Fi 6E AX211 160MHz", @"PCI\VEN_8086&DEV_51F0", "Net", "Intel", "23.70.1.3", new DateTime(2024, 9, 18), "oem9.inf", true, "Intel Corporation", false, [@"PCI\VEN_8086&DEV_51F0"], "OK", 0, "PCI bus 0"),
            new("Realtek(R) Audio", @"HDAUDIO\FUNC_01&VEN_10EC&DEV_4080", "MEDIA", "Realtek", "6.0.9680.1", new DateTime(2023, 8, 2), "oem15.inf", true, "Realtek Semiconductor Corp.", false, [@"HDAUDIO\FUNC_01&VEN_10EC&DEV_4080"], "OK", 0, null),
            new("Intel(R) Serial IO I2C Host Controller", @"PCI\VEN_8086&DEV_7A4C", "System", "Intel", "30.100.2129.21", new DateTime(2021, 7, 14), "oem3.inf", true, "Intel Corporation", false, [@"PCI\VEN_8086&DEV_7A4C"], "OK", 0, null),
            new("Samsung NVMe Controller", @"PCI\VEN_144D&DEV_A80C", "SCSIAdapter", "Microsoft", "10.0.26100.1", new DateTime(2024, 6, 1), "stornvme.inf", true, "Microsoft Windows", true, [@"PCI\VEN_144D&DEV_A80C"], "OK", 0, null),
            new("ASUS ACPI device", @"ACPI\ASUS2021", "Unknown", null, null, null, null, false, null, false, [@"ACPI\ASUS2021"], "Error", 28, null),
        };
        progress?.Report(new ProgressUpdate("Done", 100));
        return new DriverScanResult(drivers, [], DateTimeOffset.Now);
    }

    public async Task<DriverUpdateCheckResult> CheckForUpdatesAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new ProgressUpdate("Asking Windows Update for driver updates", 30));
        await DemoData.DelayAsync(cancellationToken, 500, 900).ConfigureAwait(false);
        var updates = new List<DriverUpdateInfo>
        {
            new("wu-intel-i2c", "Intel Corporation - System - 30.100.2417.47", "Intel(R) Serial IO I2C Host Controller", "30.100.2417.47", new DateTime(2024, 10, 3), 2_900_000, DriverUpdateSource.WindowsUpdate, @"PCI\VEN_8086&DEV_7A4C", null, null, false, false, false),
        };
        var links = new List<VendorLink>
        {
            new("NVIDIA", "Graphics", "https://www.nvidia.com/drivers", "GeForce Game Ready drivers for the RTX 4070."),
            new("Intel", "Chipset", "https://www.intel.com/content/www/us/en/download-center/home.html", "Chipset, Wi-Fi and Ethernet drivers."),
            new("ASUS", "Motherboard", "https://rog.asus.com/motherboards/rog-strix/rog-strix-z790-e-gaming-wifi-model/helpdesk_download/", "BIOS and board-specific drivers for the ROG STRIX Z790-E."),
        };
        progress?.Report(new ProgressUpdate("Done", 100));
        return new DriverUpdateCheckResult(updates, links, [], DateTimeOffset.Now);
    }

    public async Task<OperationResult> DownloadAsync(IReadOnlyList<DriverUpdateInfo> updates, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        for (int i = 0; i <= 10; i++)
        {
            progress?.Report(new ProgressUpdate("Downloading", i * 10));
            await Task.Delay(80, cancellationToken).ConfigureAwait(false);
        }

        return OperationResult.Ok($"{updates.Count} update(s) downloaded (demo).");
    }

    public async Task<DriverInstallResult> InstallAsync(IReadOnlyList<DriverUpdateInfo> updates, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new ProgressUpdate("Installing", 50));
        await DemoData.DelayAsync(cancellationToken, 500, 900).ConfigureAwait(false);
        return new DriverInstallResult(true, false, updates.Select(u => (u, OperationResult.Ok("Installed (demo)."))).ToList());
    }

    public async Task<OperationResult> BackupDriversAsync(string folder, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new ProgressUpdate("Exporting drivers", 40, folder));
        await DemoData.DelayAsync(cancellationToken, 500, 900).ConfigureAwait(false);
        return OperationResult.Ok($"Exported 112 driver packages to {folder} (demo).");
    }

    public async Task<OperationResult> InstallInfAsync(string infPath, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok($"Installed {Path.GetFileName(infPath)} (demo).");
    }

    public async Task<OperationResult> RollbackAsync(string deviceId, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok($"Rolled back the driver for {deviceId} (demo).", rebootRequired: true);
    }
}
