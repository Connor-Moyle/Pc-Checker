using PcChecker.Core.Common;
using PcChecker.Core.Hardware;

namespace PcChecker.Core.Drivers;

/// <summary>
/// Demo drivers for the sample gaming desktop: a realistic mix of inbox and third-party drivers, one device with a
/// problem, a couple of old drivers, two optional updates from Windows Update and a populated driver store. Pure .NET.
/// </summary>
public sealed class DemoDriverService : IDriverService, IDriverStoreProvider, IDriverUpdateStateProvider, IDriverUpdateHiding
{
    private readonly List<DriverUpdateInfo> _hidden = [];

    private static readonly DateTime Today = DateTime.Today;
    private readonly HashSet<string> _downloaded = new(StringComparer.OrdinalIgnoreCase);

    public async Task<DriverScanResult> ScanInstalledAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new ProgressUpdate("Reading device status", 15));
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        progress?.Report(new ProgressUpdate("Reading installed drivers", 55));
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        var drivers = DriverInfoBuilder.Sort(DriverInfoBuilder.Deduplicate(SampleDrivers()));
        progress?.Report(new ProgressUpdate("Done", 100));
        return new DriverScanResult(drivers, [], DateTimeOffset.Now);
    }

    public async Task<DriverUpdateCheckResult> CheckForUpdatesAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new ProgressUpdate("Connecting to Windows Update", 10));
        await DemoData.DelayAsync(cancellationToken, 200, 400).ConfigureAwait(false);
        progress?.Report(new ProgressUpdate("Searching the Windows Update driver catalog", 40, "This can take a minute."));
        await DemoData.DelayAsync(cancellationToken, 500, 900).ConfigureAwait(false);
        var updates = new List<DriverUpdateInfo>
        {
            new("wu-intel-i2c", "Intel Corporation - System - 30.100.2417.47",
                "Intel(R) Serial IO I2C Host Controller · Intel Corporation · System\nIntel Corporation System driver update released in October 2024.",
                "30.100.2417.47", new DateTime(2024, 10, 3), 2_900_000, DriverUpdateSource.WindowsUpdate, @"PCI\VEN_8086&DEV_7A4C", null, null, false, false, false)
            {
                Provider = "Intel Corporation",
            },
            new("wu-nvidia-display", "NVIDIA - Display - 32.0.15.6094",
                "NVIDIA GeForce RTX 4070 · NVIDIA · Display\nNVIDIA display driver offered by Windows Update.",
                "32.0.15.6094", new DateTime(2024, 8, 14), 712_000_000, DriverUpdateSource.WindowsUpdate, @"PCI\VEN_10DE&DEV_2786", null, null, false, false, false)
            {
                Provider = "NVIDIA",
                DriverClass = "Display",
            },
            new("wu-realtek-audio", "Realtek Semiconductor Corp. - MEDIA - 6.0.9728.1",
                "Realtek(R) Audio · Realtek Semiconductor Corp. · MEDIA\nRealtek audio driver update released in April 2025.",
                "6.0.9728.1", new DateTime(2025, 4, 22), 48_500_000, DriverUpdateSource.WindowsUpdate, @"HDAUDIO\FUNC_01&VEN_10EC&DEV_4080", null, null, false, false, true)
            {
                Provider = "Realtek Semiconductor Corp.",
            },
        };
        var links = DriverRules.VendorLinksFor(DemoHardwareInventoryService.Build());
        progress?.Report(new ProgressUpdate("Done", 100));

        // The sample PC has the NVIDIA app, so the Windows Update display driver is flagged and left unticked.
        var visible = updates.Where(u => !_hidden.Any(h => h.Id == u.Id));
        return new DriverUpdateCheckResult(DriverUpdateRules.Annotate(visible, amdSoftware: false, nvidiaSoftware: true), links, [], DateTimeOffset.Now);
    }

    public async Task<OperationResult> SetHiddenAsync(IReadOnlyList<DriverUpdateInfo> updates, bool hidden, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(updates);
        await DemoData.DelayAsync(cancellationToken, 150, 300).ConfigureAwait(false);
        foreach (var u in updates)
        {
            _hidden.RemoveAll(h => h.Id == u.Id);
            if (hidden)
            {
                _hidden.Add(u);
            }
        }

        return OperationResult.Ok($"{updates.Count} driver update(s) {(hidden ? "hidden" : "unhidden")} (demo).");
    }

    public Task<IReadOnlyList<DriverUpdateInfo>> GetHiddenAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DriverUpdateInfo>>(_hidden.ToList());

    public async Task<OperationResult> DownloadAsync(IReadOnlyList<DriverUpdateInfo> updates, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(updates);
        if (updates.Count == 0)
        {
            return OperationResult.Fail("No updates were selected.");
        }

        for (int i = 0; i < updates.Count; i++)
        {
            for (int step = 0; step <= 4; step++)
            {
                progress?.Report(new ProgressUpdate("Downloading", 100.0 * (i * 5 + step) / (updates.Count * 5), updates[i].Title));
                await Task.Delay(90, cancellationToken).ConfigureAwait(false);
            }
        }

        lock (_downloaded)
        {
            foreach (var u in updates)
            {
                _downloaded.Add(u.Id);
            }
        }

        progress?.Report(new ProgressUpdate("Download finished", 100));
        return OperationResult.Ok($"{updates.Count} update(s) downloaded (demo).");
    }

    public Task<IReadOnlySet<string>> GetDownloadedUpdateIdsAsync(IEnumerable<string> updateIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(updateIds);
        lock (_downloaded)
        {
            IReadOnlySet<string> result = updateIds.Where(_downloaded.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return Task.FromResult(result);
        }
    }

    public async Task<DriverInstallResult> InstallAsync(IReadOnlyList<DriverUpdateInfo> updates, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(updates);
        progress?.Report(new ProgressUpdate("Installing driver updates", 20, $"{updates.Count} update(s)"));
        await DemoData.DelayAsync(cancellationToken, 600, 1000).ConfigureAwait(false);
        progress?.Report(new ProgressUpdate("Install finished", 100));
        bool reboot = updates.Any(u => u.RebootRequired);
        return new DriverInstallResult(true, reboot, updates.Select(u => (u, OperationResult.Ok("Succeeded (demo).", u.RebootRequired))).ToList());
    }

    public async Task<OperationResult> BackupDriversAsync(string folder, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        const int total = 112;
        for (int i = 0; i <= total; i += 8)
        {
            progress?.Report(new ProgressUpdate("Exporting driver packages", 100.0 * i / total, $"{i} of {total}"));
            await Task.Delay(60, cancellationToken).ConfigureAwait(false);
        }

        return OperationResult.Ok($"Exported {total} driver package(s) to {folder} (demo).");
    }

    public async Task<OperationResult> InstallInfAsync(string infPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(infPath);
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok($"Installed {Path.GetFileName(infPath)} (demo).");
    }

    public async Task<OperationResult> RollbackAsync(string deviceId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        var driver = SampleDrivers().FirstOrDefault(d => d.DeviceId.Equals(deviceId, StringComparison.OrdinalIgnoreCase));
        if (driver is null)
        {
            return OperationResult.Fail("This device was not in the last scan. Scan drivers again.");
        }

        if (!DriverRules.CanRollBack(driver))
        {
            return OperationResult.Fail($"{driver.DeviceName} uses the driver Windows ships, so there is no third-party package to remove. Use Device Manager → Roll Back Driver.");
        }

        if (DriverRules.RollbackBlockReason(driver, SampleDrivers()) is { } blocked)
        {
            return OperationResult.Fail(blocked);
        }

        return OperationResult.Ok($"Removed {driver.InfName} for {driver.DeviceName} (demo). Restart Windows to finish.", rebootRequired: true);
    }

    public async Task<DriverStoreResult> GetDriverStoreAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 100, 300).ConfigureAwait(false);
        return new DriverStoreResult(SamplePackages(), [], DateTimeOffset.Now);
    }

    /// <summary>The sample installed drivers (unsorted).</summary>
    public static IReadOnlyList<DriverInfo> SampleDrivers() =>
    [
        Third(DemoData.GpuName, @"PCI\VEN_10DE&DEV_2786&SUBSYS_88AB1043", "Display", "NVIDIA", "32.0.15.6094", new DateTime(2025, 2, 10), "oem12.inf", "NVIDIA Corporation", "PCI bus 1, device 0, function 0"),
        Third("NVIDIA High Definition Audio", @"HDAUDIO\FUNC_01&VEN_10DE&DEV_00A3", "MEDIA", "NVIDIA", "1.4.1.0", new DateTime(2023, 3, 22), "oem10.inf", "NVIDIA Corporation", null),
        Third("Intel Ethernet Controller I226-V", @"PCI\VEN_8086&DEV_125C", "Net", "Intel", "2.1.4.3", new DateTime(2024, 11, 5), "oem7.inf", "Intel Corporation", "PCI bus 6, device 0, function 0"),
        Third("Realtek Gaming 2.5GbE Family Controller", @"PCI\VEN_10EC&DEV_8125&SUBSYS_7D781462", "Net", "Realtek", "10.68.815.2023", new DateTime(2023, 8, 15), "oem35.inf", "Realtek Semiconductor Corp.", "PCI bus 7, device 0, function 0"),
        Third("Intel Wi-Fi 6E AX211 160MHz", @"PCI\VEN_8086&DEV_51F0", "Net", "Intel", "23.70.1.3", new DateTime(2024, 9, 18), "oem9.inf", "Intel Corporation", "PCI bus 0, device 20, function 3"),
        Third("Intel(R) Wireless Bluetooth(R)", @"USB\VID_8087&PID_0033", "Bluetooth", "Intel", "23.70.0.3", new DateTime(2024, 9, 18), "oem11.inf", "Intel Corporation", "Port_#0014.Hub_#0001"),
        Third("Realtek(R) Audio", @"HDAUDIO\FUNC_01&VEN_10EC&DEV_4080", "MEDIA", "Realtek", "6.0.9680.1", new DateTime(2023, 8, 2), "oem15.inf", "Realtek Semiconductor Corp.", null),
        Third("Intel(R) Serial IO I2C Host Controller", @"PCI\VEN_8086&DEV_7A4C", "System", "Intel", "30.100.2129.21", Today.AddYears(-4).AddDays(-20), "oem3.inf", "Intel Corporation", "PCI bus 0, device 21, function 0"),
        Third("Intel(R) Management Engine Interface", @"PCI\VEN_8086&DEV_7A68", "System", "Intel", "2406.5.5.0", new DateTime(2024, 5, 2), "oem4.inf", "Intel Corporation", "PCI bus 0, device 22, function 0"),
        Third("Intel(R) Chipset SATA/PCIe RST Premium Controller", @"PCI\VEN_8086&DEV_7A62", "SCSIAdapter", "Intel", "19.5.4.1050", new DateTime(2024, 1, 12), "oem5.inf", "Intel Corporation", "PCI bus 0, device 23, function 0"),
        Third("ASUS Aura Lighting Controller", @"USB\VID_0B05&PID_19AF", "HIDClass", "ASUS", "1.0.0.4", Today.AddYears(-3).AddDays(-100), "oem18.inf", "ASUSTeK Computer Inc.", "Port_#0008.Hub_#0001"),
        Third("Logitech G PRO X SUPERLIGHT", @"HID\VID_046D&PID_C547", "HIDClass", "Logitech", "2024.3.1.0", new DateTime(2024, 3, 1), "oem21.inf", "Logitech", null),
        Third("Corsair K70 RGB PRO", @"HID\VID_1B1C&PID_1B49", "HIDClass", "Corsair", "5.1.0.10", new DateTime(2024, 7, 9), "oem22.inf", "Corsair Memory, Inc.", null),
        Third("SteelSeries Arctis Nova Pro", @"USB\VID_1038&PID_12E0", "MEDIA", "SteelSeries", "1.5.0.2", new DateTime(2023, 11, 14), "oem24.inf", "SteelSeries ApS", "Port_#0003.Hub_#0002"),
        new("ASUS USB Audio (legacy)", @"USB\VID_0B05&PID_1822", "MEDIA", "ASUSTeK", "6.12.8.1023", Today.AddYears(-5), "oem30.inf", false, null, false, [@"USB\VID_0B05&PID_1822"], "OK", 0, "Port_#0005.Hub_#0001"),
        Inbox("Samsung NVMe Controller", @"PCI\VEN_144D&DEV_A80C", "SCSIAdapter", "10.0.26100.1", new DateTime(2024, 6, 1), "stornvme.inf", "PCI bus 2, device 0, function 0"),
        Inbox("Standard SATA AHCI Controller", @"PCI\VEN_8086&DEV_7AE2", "HDC", "10.0.26100.1", new DateTime(2024, 6, 1), "mshdc.inf", "PCI bus 0, device 23, function 0"),
        Inbox("USB xHCI Compliant Host Controller", @"PCI\VEN_8086&DEV_7A60", "USB", "10.0.26100.1150", new DateTime(2024, 6, 1), "usbxhci.inf", "PCI bus 0, device 20, function 0"),
        Inbox("High Definition Audio Controller", @"PCI\VEN_8086&DEV_7A50", "System", "10.0.26100.1", new DateTime(2024, 6, 1), "hdaudbus.inf", "PCI bus 0, device 31, function 3"),
        Inbox("Intel(R) 13th Gen Core(TM) i7-13700K", @"ACPI\GENUINEINTEL_-_INTEL64_FAMILY_6_MODEL_183", "Processor", "10.0.26100.1", new DateTime(2024, 6, 1), "cpu.inf", null),
        Inbox("Generic PnP Monitor", @"DISPLAY\AUS27B1\5&1A2B3C4D&0&UID4352", "Monitor", "10.0.26100.1", new DateTime(2024, 6, 1), "monitor.inf", null),
        Inbox("HID-compliant mouse", @"HID\VID_046D&PID_C547&MI_00", "Mouse", "10.0.26100.1", new DateTime(2024, 6, 1), "msmouse.inf", null),
        Inbox("HID Keyboard Device", @"HID\VID_1B1C&PID_1B49&MI_00", "Keyboard", "10.0.26100.1", new DateTime(2024, 6, 1), "keyboard.inf", null),
        Inbox("Microsoft Basic Render Driver", @"ROOT\BASICRENDER\0000", "System", "10.0.26100.1", new DateTime(2024, 6, 1), "basicrender.inf", null),
        Inbox("Trusted Platform Module 2.0", @"ACPI\MSFT0101\1", "SecurityDevices", "10.0.26100.1", new DateTime(2024, 6, 1), "tpm.inf", null),
        Inbox("UEFI Firmware", @"UEFI\RES_{ASUS-2801}\0", "Firmware", "2801", new DateTime(2025, 1, 20), "uefi.inf", null),
        new("ASUS ACPI device", @"ACPI\ASUS2021\2&DABA3FF&1", "Unknown", null, null, null, null, false, null, false, [@"ACPI\ASUS2021"], "Error", 28, null),
        new("SanDisk Ultra USB 3.0", @"USBSTOR\DISK&VEN_SANDISK&PROD_ULTRA", "DiskDrive", "Microsoft", "10.0.26100.1", new DateTime(2024, 6, 1), "disk.inf", true, "Microsoft Windows", true, [@"USBSTOR\DISK&VEN_SANDISK&PROD_ULTRA"], "Unknown", 0, "Port_#0002.Hub_#0002") { IsPresent = false },
        new("Apple iPhone", @"USB\VID_05AC&PID_12A8", "WPD", "Microsoft", "10.0.26100.1", new DateTime(2024, 6, 1), "wpdmtp.inf", true, "Microsoft Windows", true, [@"USB\VID_05AC&PID_12A8"], "Unknown", 0, "Port_#0001.Hub_#0002") { IsPresent = false },
    ];

    /// <summary>The sample driver store contents.</summary>
    public static IReadOnlyList<DriverPackage> SamplePackages() =>
    [
        new("oem3.inf", "iaLPSS2_I2C_ADL.inf", "Intel Corporation", "System devices", "{4d36e97d-e325-11ce-bfc1-08002be10318}", "30.100.2129.21", Today.AddYears(-4).AddDays(-20), "Microsoft Windows Hardware Compatibility Publisher"),
        new("oem4.inf", "heci.inf", "Intel Corporation", "System devices", "{4d36e97d-e325-11ce-bfc1-08002be10318}", "2406.5.5.0", new DateTime(2024, 5, 2), "Microsoft Windows Hardware Compatibility Publisher"),
        new("oem5.inf", "iaStorVD.inf", "Intel Corporation", "Storage controllers", "{4d36e97b-e325-11ce-bfc1-08002be10318}", "19.5.4.1050", new DateTime(2024, 1, 12), "Microsoft Windows Hardware Compatibility Publisher"),
        new("oem7.inf", "e2f.inf", "Intel Corporation", "Network adapters", "{4d36e972-e325-11ce-bfc1-08002be10318}", "2.1.4.3", new DateTime(2024, 11, 5), "Microsoft Windows Hardware Compatibility Publisher"),
        new("oem35.inf", "rt25cx21x64.inf", "Realtek", "Network adapters", "{4d36e972-e325-11ce-bfc1-08002be10318}", "10.68.815.2023", new DateTime(2023, 8, 15), "Microsoft Windows Hardware Compatibility Publisher"),
        new("oem9.inf", "netwtw6e.inf", "Intel Corporation", "Network adapters", "{4d36e972-e325-11ce-bfc1-08002be10318}", "23.70.1.3", new DateTime(2024, 9, 18), "Microsoft Windows Hardware Compatibility Publisher"),
        new("oem10.inf", "nvhda.inf", "NVIDIA Corporation", "Sound, video and game controllers", "{4d36e96c-e325-11ce-bfc1-08002be10318}", "1.4.1.0", new DateTime(2023, 3, 22), "Microsoft Windows Hardware Compatibility Publisher"),
        new("oem11.inf", "ibtusb.inf", "Intel Corporation", "Bluetooth", "{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}", "23.70.0.3", new DateTime(2024, 9, 18), "Microsoft Windows Hardware Compatibility Publisher"),
        new("oem12.inf", "nv_dispi.inf", "NVIDIA Corporation", "Display adapters", "{4d36e968-e325-11ce-bfc1-08002be10318}", "32.0.15.6094", new DateTime(2025, 2, 10), "Microsoft Windows Hardware Compatibility Publisher"),
        new("oem15.inf", "hdxrt.inf", "Realtek Semiconductor Corp.", "Sound, video and game controllers", "{4d36e96c-e325-11ce-bfc1-08002be10318}", "6.0.9680.1", new DateTime(2023, 8, 2), "Microsoft Windows Hardware Compatibility Publisher"),
        new("oem18.inf", "AsusAura.inf", "ASUSTeK Computer Inc.", "Human Interface Devices", "{745a17a0-74d3-11d0-b6fe-00a0c90f57da}", "1.0.0.4", Today.AddYears(-3).AddDays(-100), "Microsoft Windows Hardware Compatibility Publisher"),
        new("oem21.inf", "lghub.inf", "Logitech", "Human Interface Devices", "{745a17a0-74d3-11d0-b6fe-00a0c90f57da}", "2024.3.1.0", new DateTime(2024, 3, 1), "Microsoft Windows Hardware Compatibility Publisher"),
        new("oem22.inf", "CorsairLLA.inf", "Corsair Memory, Inc.", "Human Interface Devices", "{745a17a0-74d3-11d0-b6fe-00a0c90f57da}", "5.1.0.10", new DateTime(2024, 7, 9), "Microsoft Windows Hardware Compatibility Publisher"),
        new("oem24.inf", "SteelSeriesSonar.inf", "SteelSeries ApS", "Sound, video and game controllers", "{4d36e96c-e325-11ce-bfc1-08002be10318}", "1.5.0.2", new DateTime(2023, 11, 14), "Microsoft Windows Hardware Compatibility Publisher"),
        new("oem30.inf", "AsusUsbAudio.inf", "ASUSTeK", "Sound, video and game controllers", "{4d36e96c-e325-11ce-bfc1-08002be10318}", "6.12.8.1023", Today.AddYears(-5), null),
    ];

    private static DriverInfo Third(string name, string id, string cls, string provider, string version, DateTime date, string inf, string signer, string? location) =>
        new(name, id, cls, provider, version, date, inf, true, signer, false, [id], "OK", 0, location);

    private static DriverInfo Inbox(string name, string id, string cls, string version, DateTime date, string inf, string? location) =>
        new(name, id, cls, "Microsoft", version, date, inf, true, "Microsoft Windows", true, [id], "OK", 0, location);
}
