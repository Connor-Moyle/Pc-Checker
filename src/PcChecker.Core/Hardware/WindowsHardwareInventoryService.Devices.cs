using PcChecker.Core.Common;

namespace PcChecker.Core.Hardware;

/// <summary>Storage, network, monitor, audio, USB, battery, input and Plug and Play collectors.</summary>
public sealed partial class WindowsHardwareInventoryService
{
    // ---- Storage ------------------------------------------------------------------------------------------------

    private static void CollectStorage(WmiQueryRunner wmi, HardwareInventory inventory, CancellationToken ct)
    {
        var drives = wmi.Query("SELECT * FROM Win32_DiskDrive", friendlyName: "Disk drives", cancellationToken: ct);
        var physical = wmi.Query("SELECT * FROM MSFT_PhysicalDisk", WmiQueryRunner.StorageNamespace, "Physical disks (storage namespace)", ct);
        var partitions = wmi.Query("SELECT * FROM Win32_DiskPartition", friendlyName: "Partitions", cancellationToken: ct);
        var links = wmi.Query("SELECT * FROM Win32_LogicalDiskToPartition", friendlyName: "Partition to drive letter links", cancellationToken: ct);
        var logical = wmi.Query("SELECT * FROM Win32_LogicalDisk", friendlyName: "Logical drives", cancellationToken: ct);

        var lettersByPartition = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var link in links)
        {
            var partitionId = HardwareMappings.ExtractWmiKey(link.Str("Antecedent"));
            var letter = HardwareMappings.ExtractWmiKey(link.Str("Dependent"));
            if (partitionId is not null && letter is not null)
            {
                lettersByPartition[partitionId] = letter;
            }
        }

        var matched = new HashSet<WmiRow>();
        foreach (var drive in drives)
        {
            var index = drive.Int("Index");
            var model = drive.Str("Model");
            var rawSerial = drive.Str("SerialNumber");
            var match = physical.FirstOrDefault(p => !matched.Contains(p) && p.Int("DeviceId") == index)
                ?? physical.FirstOrDefault(p => !matched.Contains(p) && Storage.DriveSerial.SameDrive(p.Str("SerialNumber"), rawSerial))
                ?? physical.FirstOrDefault(p => !matched.Contains(p) && model is not null && string.Equals(p.Str("FriendlyName"), model, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                matched.Add(match);
            }

            // Win32_DiskDrive can return the ATA serial byte-swapped or hex-encoded; MSFT_PhysicalDisk has it right.
            var serial = Storage.DriveSerial.Clean(rawSerial, match?.Str("SerialNumber"));

            var parts = partitions
                .Where(p => p.Int("DiskIndex") == index)
                .OrderBy(p => p.Int("Index"))
                .Select(p =>
                {
                    var id = p.Str("DeviceID") ?? p.Str("Name") ?? "Partition";
                    var props = new Dictionary<string, string>(p.ToProperties(), StringComparer.OrdinalIgnoreCase);
                    if (lettersByPartition.TryGetValue(id, out var letter))
                    {
                        props["DriveLetter"] = letter;
                    }

                    return new PartitionInfo(letter is null ? id : $"{id} ({letter})", id, p.Long("Size"), p.Long("StartingOffset"), p.Int("Index"), p.Str("Type"),
                        p.Bool("Bootable"), p.Bool("BootPartition"), p.Bool("PrimaryPartition"), props);
                })
                .ToArray();

            var mediaType = MediaTypeFor(match, model) ?? HardwareMappings.GuessMediaType(model, drive.Str("MediaType"), drive.Str("PNPDeviceID"));
            var busType = HardwareMappings.StorageBusTypeName(match?.Int("BusType"));
            var name = match?.Str("FriendlyName") ?? model ?? drive.Str("Caption") ?? "Disk";
            inventory.PhysicalDisks.Add(new PhysicalDiskInfo(
                name,
                drive.Str("Manufacturer") is { } m && !m.Contains("Standard disk", StringComparison.OrdinalIgnoreCase) ? m : HardwareMappings.VendorFromText(ManufacturerFromModel(model)),
                drive.Str("DeviceID"),
                drive.Str("Status"),
                model,
                serial ?? match?.Str("SerialNumber"),
                drive.Str("FirmwareRevision") ?? match?.Str("FirmwareVersion"),
                drive.Str("InterfaceType"),
                mediaType,
                busType,
                drive.Long("Size") ?? match?.Long("Size"),
                HardwareMappings.StorageHealthName(match?.Int("HealthStatus")),
                match?.Int("SpindleSpeed"),
                drive.Str("PNPDeviceID"),
                parts,
                drive.ToProperties(("PhysicalDisk", match))));
        }

        // Drives inside a Storage Spaces pool are hidden from Win32_DiskDrive; list them from the storage namespace.
        foreach (var member in physical.Where(p => !matched.Contains(p) && IsPoolMember(p)))
        {
            var model = member.Str("FriendlyName") ?? member.Str("Model");
            inventory.PhysicalDisks.Add(new PhysicalDiskInfo(
                model ?? "Disk",
                HardwareMappings.VendorFromText(ManufacturerFromModel(model)),
                null,
                null,
                model,
                Storage.DriveSerial.Clean(member.Str("SerialNumber")),
                member.Str("FirmwareVersion"),
                null,
                MediaTypeFor(member, model),
                HardwareMappings.StorageBusTypeName(member.Int("BusType")),
                member.Long("Size"),
                HardwareMappings.StorageHealthName(member.Int("HealthStatus")),
                member.Int("SpindleSpeed"),
                null,
                [],
                member.ToProperties()));
        }

        foreach (var disk in logical)
        {
            var id = disk.Str("DeviceID") ?? "?:";
            inventory.LogicalDrives.Add(new LogicalDriveInfo(
                id,
                id,
                disk.Str("VolumeName"),
                disk.Str("FileSystem"),
                HardwareMappings.DriveTypeName(disk.Int("DriveType")),
                disk.Long("Size"),
                disk.Long("FreeSpace"),
                disk.Str("VolumeSerialNumber"),
                disk.Bool("Compressed"),
                disk.ToProperties()));
        }
    }

    /// <summary>MSFT_PhysicalDisk.CannotPoolReason contains 2 ("In a pool").</summary>
    private static bool IsPoolMember(WmiRow physical) =>
        physical.Raw("CannotPoolReason") is Array reasons && reasons.Cast<object?>().Any(r => r is not null && Convert.ToInt32(r, System.Globalization.CultureInfo.InvariantCulture) == Storage.DriveInventoryJoin.CannotPoolInPool);

    /// <summary>"HDD", "SSD", "SCM" or "Memory card" from MSFT_PhysicalDisk (media type, bus, spindle speed), or null.</summary>
    private static string? MediaTypeFor(WmiRow? physical, string? model)
    {
        if (physical is null)
        {
            return null;
        }

        long? spindle = physical.Long("SpindleSpeed");
        var decision = Storage.DriveMediaRules.Decide(physical.Int("MediaType"), physical.Int("BusType"), null, spindle, model);
        return decision.Media == Storage.DriveMedia.Unknown ? null : Storage.DriveMediaRules.Label(decision.Media);
    }

    private static string? ManufacturerFromModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        var first = model.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        return first.ToUpperInvariant() switch
        {
            "WDC" or "WD" => "Western Digital",
            "ST" => "Seagate",
            "HGST" => "HGST",
            "TOSHIBA" => "Toshiba",
            "KINGSTON" => "Kingston",
            "SAMSUNG" => "Samsung",
            "CT" or "CRUCIAL" => "Crucial",
            "KXG" or "KBG" => "Kioxia",
            "SK" => "SK Hynix",
            "INTEL" => "Intel",
            "SANDISK" => "SanDisk",
            _ => first.StartsWith("ST", StringComparison.OrdinalIgnoreCase) && first.Length > 4 && char.IsDigit(first[2]) ? "Seagate" : first,
        };
    }

    // ---- Network ------------------------------------------------------------------------------------------------

    private static void CollectNetwork(WmiQueryRunner wmi, HardwareInventory inventory, CancellationToken ct)
    {
        var adapters = wmi.Query("SELECT * FROM Win32_NetworkAdapter", friendlyName: "Network adapters", cancellationToken: ct);
        var configs = wmi.Query("SELECT * FROM Win32_NetworkAdapterConfiguration", friendlyName: "Network configuration", cancellationToken: ct)
            .ToDictionary(c => c.Int("Index") ?? -1, c => c);
        var drivers = wmi.Query("SELECT DeviceID, DriverVersion, DriverDate, DriverProviderName, InfName FROM Win32_PnPSignedDriver WHERE DeviceClass = 'NET'", friendlyName: "Network drivers", cancellationToken: ct)
            .Where(d => d.Str("DeviceID") is not null)
            .GroupBy(d => d.Str("DeviceID")!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var adapter in adapters)
        {
            var mac = adapter.Str("MACAddress");
            var physicalFlag = adapter.Bool("PhysicalAdapter");
            if (physicalFlag != true && mac is null)
            {
                continue;
            }

            var pnp = adapter.Str("PNPDeviceID");
            var name = adapter.Str("Name") ?? adapter.Str("Description") ?? "Network adapter";
            var kind = HardwareMappings.NetworkAdapterKind(name, pnp, adapter.Str("AdapterType"), adapter.Str("NetConnectionID"));
            if (kind == "Virtual" && physicalFlag != true)
            {
                // Keep virtual adapters only when Windows calls them physical; otherwise they are tunnels, filters or VM switches.
                continue;
            }

            configs.TryGetValue(adapter.Int("Index") ?? adapter.Int("DeviceID") ?? -2, out var config);
            WmiRow? driver = null;
            if (pnp is not null)
            {
                drivers.TryGetValue(pnp, out driver);
            }

            inventory.NetworkAdapters.Add(new NetworkAdapterInfo(
                name,
                adapter.Str("Manufacturer"),
                adapter.Str("DeviceID"),
                adapter.Str("Status") ?? HardwareMappings.NetConnectionStatusName(adapter.Int("NetConnectionStatus")),
                mac,
                kind,
                adapter.Long("Speed"),
                physicalFlag,
                adapter.Str("NetConnectionID"),
                HardwareMappings.NetConnectionStatusName(adapter.Int("NetConnectionStatus")),
                config?.List("IPAddress") ?? [],
                config?.List("DefaultIPGateway") ?? [],
                config?.List("DNSServerSearchOrder") ?? [],
                config?.Bool("DHCPEnabled"),
                pnp,
                adapter.ToProperties(("Configuration", config), ("Driver", driver))));
        }
    }

    // ---- Monitors -----------------------------------------------------------------------------------------------

    private static void CollectMonitors(WmiQueryRunner wmi, HardwareInventory inventory, CancellationToken ct)
    {
        var ids = wmi.Query("SELECT * FROM WmiMonitorID", WmiQueryRunner.WmiRoot, "Monitor identification", ct);
        var basics = wmi.Query("SELECT * FROM WmiMonitorBasicDisplayParams", WmiQueryRunner.WmiRoot, "Monitor display parameters", ct)
            .ToDictionary(b => b.Str("InstanceName") ?? string.Empty, b => b, StringComparer.OrdinalIgnoreCase);
        var modes = wmi.Query("SELECT * FROM WmiMonitorListedSupportedSourceModes", WmiQueryRunner.WmiRoot, "Monitor supported modes", ct)
            .ToDictionary(b => b.Str("InstanceName") ?? string.Empty, b => b, StringComparer.OrdinalIgnoreCase);
        var desktop = wmi.Query("SELECT * FROM Win32_DesktopMonitor", friendlyName: "Desktop monitors", cancellationToken: ct);

        foreach (var id in ids)
        {
            var instance = id.Str("InstanceName") ?? string.Empty;
            basics.TryGetValue(instance, out var basic);
            modes.TryGetValue(instance, out var mode);
            var manufacturerCode = HardwareMappings.DecodeMonitorString(id.Raw("ManufacturerName") as System.Collections.IEnumerable is { } me ? me.Cast<object?>() : null);
            var friendly = HardwareMappings.DecodeMonitorString(id.Raw("UserFriendlyName") as System.Collections.IEnumerable is { } fe ? fe.Cast<object?>() : null);
            var product = HardwareMappings.DecodeMonitorString(id.Raw("ProductCodeID") as System.Collections.IEnumerable is { } pe ? pe.Cast<object?>() : null);
            var serial = HardwareMappings.DecodeMonitorString(id.Raw("SerialNumberID") as System.Collections.IEnumerable is { } se ? se.Cast<object?>() : null);
            var manufacturer = HardwareMappings.MonitorManufacturerName(manufacturerCode);
            var (width, height, hz) = BestSourceMode(mode);
            var diagonal = HardwareMappings.DiagonalInches(basic?.Int("MaxHorizontalImageSize"), basic?.Int("MaxVerticalImageSize"));

            var properties = new Dictionary<string, string>(id.ToProperties(("DisplayParams", basic)), StringComparer.OrdinalIgnoreCase)
            {
                ["UserFriendlyName.Decoded"] = friendly ?? string.Empty,
                ["ManufacturerName.Decoded"] = manufacturerCode ?? string.Empty,
            };
            if (diagonal is not null) properties["DiagonalInches"] = diagonal.Value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
            if (hz is not null) properties["MaxRefreshHz"] = hz.Value.ToString();
            properties = properties.Where(kv => kv.Value.Length > 0).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

            var name = friendly ?? (manufacturer is null ? "Monitor" : $"{manufacturer} {product}".Trim());
            inventory.Monitors.Add(new MonitorInfo(
                name,
                manufacturer,
                instance.Split('\\').Length > 1 ? string.Join('\\', instance.Split('\\').Take(2)) : instance,
                basic?.Bool("Active") == true ? "OK" : null,
                product,
                serial,
                id.Int("YearOfManufacture"),
                id.Int("WeekOfManufacture"),
                instance,
                width,
                height,
                properties));
        }

        if (inventory.Monitors.Count == 0)
        {
            foreach (var row in desktop)
            {
                inventory.Monitors.Add(new MonitorInfo(
                    row.Str("Name") ?? "Monitor",
                    row.Str("MonitorManufacturer"),
                    row.Str("DeviceID"),
                    row.Str("Status"),
                    null,
                    null,
                    null,
                    null,
                    row.Str("PNPDeviceID"),
                    row.Int("ScreenWidth"),
                    row.Int("ScreenHeight"),
                    row.ToProperties()));
            }
        }
    }

    /// <summary>The largest listed source mode (width × height) and its highest refresh rate.</summary>
    private static (int? Width, int? Height, int? Hz) BestSourceMode(WmiRow? modes)
    {
        if (modes?.Raw("MonitorSourceModes") is not WmiRow[] list || list.Length == 0)
        {
            return (null, null, null);
        }

        int? bestW = null, bestH = null, bestHz = null;
        foreach (var m in list)
        {
            var w = m.Int("HorizontalActivePixels");
            var h = m.Int("VerticalActivePixels");
            if (w is null || h is null)
            {
                continue;
            }

            var num = m.Long("VerticalRefreshRateNumerator");
            var den = m.Long("VerticalRefreshRateDenominator");
            int? hz = num is > 0 && den is > 0 ? (int)Math.Round((double)num.Value / den.Value) : null;
            long pixels = (long)w.Value * h.Value;
            long bestPixels = (long)(bestW ?? 0) * (bestH ?? 0);
            if (pixels > bestPixels)
            {
                (bestW, bestH, bestHz) = (w, h, hz);
            }
            else if (pixels == bestPixels && hz > (bestHz ?? 0))
            {
                bestHz = hz;
            }
        }

        return (bestW, bestH, bestHz);
    }

    // ---- Audio / PnP / USB / battery / input --------------------------------------------------------------------

    private static void CollectAudio(WmiQueryRunner wmi, HardwareInventory inventory, CancellationToken ct)
    {
        foreach (var row in wmi.Query("SELECT * FROM Win32_SoundDevice", friendlyName: "Audio devices", cancellationToken: ct))
        {
            inventory.AudioDevices.Add(new AudioDeviceInfo(
                row.Str("Name") ?? row.Str("ProductName") ?? "Audio device",
                row.Str("Manufacturer"),
                row.Str("DeviceID"),
                row.Str("Status"),
                row.Str("ProductName"),
                row.Str("PNPDeviceID"),
                row.ToProperties()));
        }
    }

    private static void CollectPnpDevices(WmiQueryRunner wmi, HardwareInventory inventory, CancellationToken ct)
    {
        foreach (var row in wmi.Query("SELECT * FROM Win32_PnPEntity", friendlyName: "Plug and Play devices", cancellationToken: ct))
        {
            var name = row.Str("Name") ?? row.Str("Caption") ?? row.Str("Description") ?? "Unknown device";
            inventory.PnpDevices.Add(new PnpDeviceInfo(
                name,
                row.Str("Manufacturer"),
                row.Str("DeviceID") ?? row.Str("PNPDeviceID"),
                row.Str("Status"),
                row.Str("PNPClass"),
                row.Str("ClassGuid"),
                row.Str("Service"),
                row.Str("Description"),
                row.Int("ConfigManagerErrorCode"),
                row.Bool("Present"),
                row.List("HardwareID"),
                row.ToProperties()));
        }

        inventory.PnpDevices.Sort((a, b) => string.Compare(a.PnpClass, b.PnpClass, StringComparison.OrdinalIgnoreCase) is var c && c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
    }

    private static void CollectUsb(WmiQueryRunner wmi, HardwareInventory inventory, CancellationToken ct)
    {
        var byId = inventory.PnpDevices.Where(p => p.DeviceId is not null).GroupBy(p => p.DeviceId!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var link in wmi.Query("SELECT * FROM Win32_USBControllerDevice", friendlyName: "USB devices", cancellationToken: ct))
        {
            var id = HardwareMappings.ExtractWmiKey(link.Str("Dependent"));
            if (id is null || !seen.Add(id) || !byId.TryGetValue(id, out var device))
            {
                continue;
            }

            inventory.UsbDevices.Add(new UsbDeviceInfo(device.Name, device.Manufacturer, device.DeviceId, device.Status, device.Description, device.Service, device.ClassGuid, device.PnpClass, device.Properties));
        }

        if (inventory.UsbDevices.Count == 0)
        {
            // Older systems may lack the association class; fall back to anything on the USB bus.
            foreach (var device in inventory.PnpDevices.Where(p => p.DeviceId?.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase) == true))
            {
                inventory.UsbDevices.Add(new UsbDeviceInfo(device.Name, device.Manufacturer, device.DeviceId, device.Status, device.Description, device.Service, device.ClassGuid, device.PnpClass, device.Properties));
            }
        }
    }

    private static void CollectBatteries(WmiQueryRunner wmi, HardwareInventory inventory, CancellationToken ct)
    {
        var batteries = wmi.Query("SELECT * FROM Win32_Battery", friendlyName: "Battery", cancellationToken: ct);
        if (batteries.Count == 0)
        {
            return;
        }

        var statics = wmi.Query("SELECT * FROM BatteryStaticData", WmiQueryRunner.WmiRoot, "Battery design data", ct);
        var fulls = wmi.Query("SELECT * FROM BatteryFullChargedCapacity", WmiQueryRunner.WmiRoot, "Battery full-charge capacity", ct);
        var cycles = wmi.Query("SELECT * FROM BatteryCycleCount", WmiQueryRunner.WmiRoot, "Battery cycle count", ct);
        for (int i = 0; i < batteries.Count; i++)
        {
            var row = batteries[i];
            var stat = statics.ElementAtOrDefault(i);
            var full = fulls.ElementAtOrDefault(i);
            var cycle = cycles.ElementAtOrDefault(i);
            var properties = new Dictionary<string, string>(row.ToProperties(("Static", stat), ("FullCharge", full), ("Cycles", cycle)), StringComparer.OrdinalIgnoreCase);
            if (cycle?.Int("CycleCount") is { } count)
            {
                properties["CycleCount"] = count.ToString();
            }

            var runtime = row.Int("EstimatedRunTime");
            inventory.Batteries.Add(new BatteryInfo(
                row.Str("Name") ?? stat?.Str("DeviceName") ?? "Battery",
                stat?.Str("ManufactureName") ?? row.Str("Manufacturer"),
                row.Str("DeviceID"),
                row.Str("Status"),
                HardwareMappings.BatteryChemistryName(row.Int("Chemistry")),
                stat?.Int("DesignedCapacity") ?? row.Int("DesignCapacity"),
                full?.Int("FullChargedCapacity") ?? row.Int("FullChargeCapacity"),
                row.Int("EstimatedChargeRemaining"),
                runtime is >= 0 and < 60_000 ? runtime : null,
                HardwareMappings.BatteryStatusName(row.Int("BatteryStatus")),
                row.Int("DesignVoltage"),
                properties));
        }
    }

    private static void CollectInput(WmiQueryRunner wmi, HardwareInventory inventory, CancellationToken ct)
    {
        foreach (var row in wmi.Query("SELECT * FROM Win32_Keyboard", friendlyName: "Keyboards", cancellationToken: ct))
        {
            inventory.InputDevices.Add(new InputDeviceInfo(
                row.Str("Name") ?? row.Str("Description") ?? "Keyboard",
                row.Str("Manufacturer") ?? VendorFromPnpVendor(row.Str("PNPDeviceID")),
                row.Str("DeviceID"), row.Str("Status"), "Keyboard", row.Str("Description"), row.Str("Layout"),
                null, row.Int("NumberOfFunctionKeys"), row.Str("PNPDeviceID"), row.ToProperties()));
        }

        foreach (var row in wmi.Query("SELECT * FROM Win32_PointingDevice", friendlyName: "Pointing devices", cancellationToken: ct))
        {
            inventory.InputDevices.Add(new InputDeviceInfo(
                row.Str("Name") ?? row.Str("Description") ?? "Pointing device",
                row.Str("Manufacturer") ?? VendorFromPnpVendor(row.Str("PNPDeviceID")),
                row.Str("DeviceID"), row.Str("Status"), PointingKind(row), row.Str("Description"), null,
                row.Int("NumberOfButtons"), null, row.Str("PNPDeviceID"), row.ToProperties()));
        }
    }

    private static string PointingKind(WmiRow row) => row.Int("PointingType") switch
    {
        3 => "Mouse", 4 => "Trackball", 5 => "Trackpoint", 6 => "Glide point", 7 => "Touch pad", 8 => "Touch screen", 9 => "Mouse (optical)",
        _ => (row.Str("Name") ?? string.Empty).Contains("Touch", StringComparison.OrdinalIgnoreCase) ? "Touch pad" : "Mouse",
    };

    /// <summary>Common USB vendor ids for keyboards and mice, so the card shows a brand instead of "(Standard)".</summary>
    private static string? VendorFromPnpVendor(string? pnp)
    {
        var id = pnp is null ? null : System.Text.RegularExpressions.Regex.Match(pnp, @"VID_([0-9A-Fa-f]{4})").Groups[1].Value.ToUpperInvariant();
        return id switch
        {
            "046D" => "Logitech", "1B1C" => "Corsair", "1532" => "Razer", "1038" => "SteelSeries", "045E" => "Microsoft", "3434" => "Keychron",
            "04D9" => "Holtek", "0C45" => "Sonix", "258A" => "SINO WEALTH", "1E7D" => "ROCCAT", "0B05" => "ASUS", "0DB0" => "MSI",
            "3297" => "ZSA", "320F" => "Glorious", "361D" => "Wooting", "2516" => "Cooler Master", "1EA7" => "SHARKOON", "05AC" => "Apple",
            "" or null => null, _ => null,
        };
    }
}
