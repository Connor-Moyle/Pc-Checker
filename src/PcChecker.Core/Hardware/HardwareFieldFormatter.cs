using System.Globalization;
using PcChecker.Core.Common;

namespace PcChecker.Core.Hardware;

/// <summary>
/// Turns the typed fields of every <see cref="HardwareItem"/> into human-labelled rows ("Cores: 16 (24 threads)")
/// for the Hardware page and reports. Pure formatting, no Windows dependency.
/// </summary>
public static class HardwareFieldFormatter
{
    private const string Unknown = "Unknown";

    /// <summary>Human-labelled rows for an item; empty values are skipped so the list only shows what is known.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Fields(HardwareItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var rows = new List<KeyValuePair<string, string>>();
        void Add(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                rows.Add(new KeyValuePair<string, string>(label, value));
            }
        }

        switch (item)
        {
            case ComputerSystemInfo s:
                Add("Computer name", s.Name);
                Add("Manufacturer", s.Manufacturer);
                Add("Model", s.Model);
                Add("Chassis", s.ChassisType);
                Add("PC type", s.PcSystemType);
                Add("System type", s.SystemType);
                Add("Family", s.SystemFamily);
                Add("SKU", s.SystemSkuNumber);
                Add("Installed memory", Bytes(s.TotalPhysicalMemoryBytes));
                Add("Processors", s.NumberOfProcessors is null ? null : $"{s.NumberOfProcessors} physical, {s.NumberOfLogicalProcessors?.ToString(CultureInfo.InvariantCulture) ?? "?"} logical");
                Add("Domain / workgroup", s.Domain);
                Add("Signed-in user", s.UserName);
                break;
            case OperatingSystemInfo os:
                Add("Edition", os.Caption ?? os.Name);
                Add("Version", VersionText(os));
                Add("Architecture", os.Architecture);
                Add("Installed on", Date(os.InstallDate));
                Add("Last boot", os.LastBootUpTime is { } boot ? $"{Date(boot, true)} (up {Uptime(boot)})" : null);
                Add("Windows folder", os.WindowsDirectory);
                Add("Locale", os.Locale);
                Add("Memory visible to Windows", Bytes(os.TotalVisibleMemoryBytes));
                Add("Memory free", Bytes(os.FreePhysicalMemoryBytes));
                Add("Registered to", os.RegisteredUser);
                Add("Product ID", os.SerialNumber);
                break;
            case BiosInfo b:
                Add("Manufacturer", b.Manufacturer);
                Add("Version", b.SmBiosVersion ?? b.Version);
                Add("Release date", b.ReleaseDate is { } rd ? $"{Date(rd)} ({AgeText(rd)})" : null);
                Add("Firmware mode", b.IsUefi switch { true => "UEFI", false => "Legacy BIOS", null => null });
                Add("Secure Boot", b.SecureBootEnabled switch { true => "On", false => "Off", null => null });
                Add("Serial number", b.SerialNumber);
                break;
            case MotherboardInfo mb:
                Add("Manufacturer", mb.Manufacturer);
                Add("Product", mb.Product);
                Add("Chipset", mb.Chipset);
                Add("Revision", mb.Version);
                Add("Serial number", mb.SerialNumber);
                break;
            case CpuInfo c:
                Add("Manufacturer", c.Manufacturer);
                Add("Cores", c.Cores is null ? null : $"{c.Cores} ({c.LogicalProcessors?.ToString(CultureInfo.InvariantCulture) ?? "?"} threads)");
                Add("Max clock", Mhz(c.MaxClockMhz));
                Add("Current clock", Mhz(c.CurrentClockMhz));
                Add("Platform", CpuTraits.Describe(c.Traits));
                Add("Socket", c.Socket);
                Add("Architecture", c.Architecture);
                Add("L2 cache", Kb(c.L2CacheKb));
                Add("L3 cache", Kb(c.L3CacheKb));
                Add("Family / stepping", c.Family is null && c.Stepping is null ? null : $"Family {c.Family ?? "?"}, stepping {c.Stepping ?? "?"}");
                Add("Virtualization", c.VirtualizationEnabled switch { true => "Enabled in firmware", false => "Disabled in firmware", null => null });
                Add("Current load", c.LoadPercent is null ? null : $"{c.LoadPercent} %");
                Add("Address width", c.AddressWidth is null ? null : $"{c.AddressWidth}-bit");
                Add("Processor ID", c.ProcessorId);
                Add("Description", c.Description);
                break;
            case GpuInfo g:
                Add("Vendor", g.Vendor);
                Add("Type", g.IsIntegrated ? "Integrated (shares system memory)" : "Dedicated graphics card");
                Add("Video memory", Bytes(g.AdapterRamBytes));
                Add("Driver version", g.DriverVersion);
                Add("Driver date", g.DriverDate is { } dd ? $"{Date(dd)} ({AgeText(dd)})" : null);
                Add("Current mode", Mode(g.CurrentHorizontalResolution, g.CurrentVerticalResolution, g.CurrentRefreshRate));
                Add("Video processor", g.VideoProcessor);
                Add("Mode description", g.VideoModeDescription);
                Add("PnP id", g.PnpDeviceId);
                break;
            case MemoryModuleInfo m:
                Add("Capacity", Bytes(m.CapacityBytes));
                Add("Type", m.MemoryType is null ? null : m.FormFactor is null ? m.MemoryType : $"{m.MemoryType} {m.FormFactor}");
                Add("Rated speed", m.SpeedMhz is null ? null : $"{m.SpeedMhz} MT/s");
                Add("Configured speed", m.ConfiguredClockSpeedMhz is null ? null : $"{m.ConfiguredClockSpeedMhz} MT/s");
                Add("Slot", m.DeviceLocator is null ? m.BankLabel : m.BankLabel is null ? m.DeviceLocator : $"{m.DeviceLocator} ({m.BankLabel})");
                Add("Manufacturer", m.Manufacturer);
                Add("Part number", m.PartNumber);
                Add("Voltage", m.ConfiguredVoltageMv is null ? null : $"{m.ConfiguredVoltageMv / 1000.0:0.00} V");
                Add("Bus width", m.DataWidth is null ? null : m.TotalWidth > m.DataWidth ? $"{m.DataWidth}-bit (ECC: {m.TotalWidth - m.DataWidth} extra bits)" : $"{m.DataWidth}-bit");
                Add("Serial number", m.SerialNumber);
                break;
            case PhysicalDiskInfo d:
                Add("Model", d.Model);
                Add("Type", d.MediaType);
                Add("Connection", d.BusType ?? d.InterfaceType);
                Add("Capacity", Bytes(d.SizeBytes));
                Add("Health", d.HealthStatusText);
                Add("Spindle speed", d.SpindleSpeed is > 0 ? $"{d.SpindleSpeed} RPM" : null);
                Add("Firmware", d.FirmwareRevision);
                Add("Serial number", d.SerialNumber);
                Add("Partitions", d.PartitionCount == 0 ? null : string.Join("; ", d.Partitions.Select(PartitionText)));
                Add("PnP id", d.PnpDeviceId);
                break;
            case LogicalDriveInfo l:
                Add("Label", l.VolumeName);
                Add("File system", l.FileSystem);
                Add("Drive type", l.DriveTypeText);
                Add("Capacity", Bytes(l.SizeBytes));
                Add("Free space", l.FreeSpaceBytes is null ? null : l.UsedPercent is { } up ? $"{Bytes(l.FreeSpaceBytes)} free ({100 - up:0} % free)" : Bytes(l.FreeSpaceBytes));
                Add("Used", Bytes(l.UsedBytes));
                Add("Compressed", l.Compressed switch { true => "Yes", false => "No", null => null });
                Add("Volume serial", l.VolumeSerialNumber);
                break;
            case NetworkAdapterInfo n:
                Add("Kind", n.AdapterType);
                Add("Connection", n.NetConnectionId);
                Add("Status", n.NetConnectionStatus);
                Add("Link speed", BitsPerSecond(n.SpeedBitsPerSecond));
                Add("MAC address", n.MacAddress);
                Add("IP addresses", Join(n.IpAddresses));
                Add("Gateway", Join(n.Gateways));
                Add("DNS servers", Join(n.DnsServers));
                Add("DHCP", n.DhcpEnabled switch { true => "Enabled", false => "Disabled (static address)", null => null });
                Add("Physical adapter", n.PhysicalAdapter switch { true => "Yes", false => "No", null => null });
                Add("Driver", n.Get("Driver.DriverVersion") is { } dv ? dv + (n.Get("Driver.DriverProviderName") is { } pv ? $" ({pv})" : string.Empty) : null);
                Add("PnP id", n.PnpDeviceId);
                break;
            case MonitorInfo mo:
                Add("Manufacturer", mo.Manufacturer);
                Add("Product code", mo.ProductCode);
                Add("Manufactured", mo.YearOfManufacture is null ? null : mo.WeekOfManufacture is > 0 ? $"{mo.YearOfManufacture} (week {mo.WeekOfManufacture})" : mo.YearOfManufacture.ToString());
                Add("Native resolution", mo.ScreenWidth is > 0 && mo.ScreenHeight is > 0 ? $"{mo.ScreenWidth} x {mo.ScreenHeight}" + (mo.Get("MaxRefreshHz") is { } hz ? $" @ {hz} Hz" : string.Empty) : null);
                Add("Size", mo.Get("DiagonalInches") is { } di ? $"{di}\"" : null);
                Add("Serial number", mo.SerialNumber);
                Add("Instance", mo.InstanceName);
                break;
            case AudioDeviceInfo a:
                Add("Manufacturer", a.Manufacturer);
                Add("Product", a.ProductName);
                Add("PnP id", a.PnpDeviceId);
                break;
            case UsbDeviceInfo u:
                Add("Description", u.Description);
                Add("Manufacturer", u.Manufacturer);
                Add("Device class", u.PnpClass);
                Add("Driver service", u.Service);
                Add("Device id", u.DeviceId);
                break;
            case BatteryInfo bt:
                Add("Charge", bt.EstimatedChargePercent is null ? null : $"{bt.EstimatedChargePercent} %");
                Add("State", bt.BatteryStatusText);
                Add("Chemistry", bt.Chemistry);
                Add("Design capacity", Mwh(bt.DesignCapacityMwh));
                Add("Full charge capacity", Mwh(bt.FullChargeCapacityMwh));
                Add("Wear", HardwareMappings.BatteryWearPercent(bt.DesignCapacityMwh, bt.FullChargeCapacityMwh) is { } w ? $"{w:0.#} % of original capacity lost" : null);
                Add("Cycle count", bt.Get("CycleCount"));
                Add("Estimated runtime", bt.EstimatedRunTimeMinutes is { } rt && rt < 60_000 ? $"{rt / 60} h {rt % 60} min" : null);
                Add("Design voltage", bt.DesignVoltageMv is null ? null : $"{bt.DesignVoltageMv / 1000.0:0.0} V");
                break;
            case InputDeviceInfo i:
                Add("Kind", i.Kind);
                Add("Description", i.Description);
                Add("Manufacturer", i.Manufacturer);
                Add("Buttons", i.NumberOfButtons?.ToString(CultureInfo.InvariantCulture));
                Add("Function keys", i.NumberOfFunctionKeys?.ToString(CultureInfo.InvariantCulture));
                Add("Layout", i.Layout);
                Add("PnP id", i.PnpDeviceId);
                break;
            case PnpDeviceInfo p:
                Add("Device class", p.PnpClass);
                Add("Description", p.Description);
                Add("Manufacturer", p.Manufacturer);
                Add("Driver service", p.Service);
                Add("Problem", p.ConfigManagerErrorCode is > 0 ? $"Code {p.ConfigManagerErrorCode}: {ProblemText(p.ConfigManagerErrorCode.Value)}" : p.ConfigManagerErrorCode == 0 ? "None (working properly)" : null);
                Add("Present", p.Present switch { true => "Yes", false => "No (remembered device)", null => null });
                Add("Hardware ids", Join(p.HardwareIds));
                Add("Device id", p.DeviceId);
                break;
            case PartitionInfo pt:
                Add("Size", Bytes(pt.SizeBytes));
                Add("Type", pt.Type);
                Add("Bootable", pt.Bootable switch { true => "Yes", false => "No", null => null });
                Add("Starting offset", pt.StartingOffset?.ToString("N0", CultureInfo.InvariantCulture));
                break;
            default:
                Add("Manufacturer", item.Manufacturer);
                Add("Device id", item.DeviceId);
                break;
        }

        if (item.Status is { } st && item is not NetworkAdapterInfo)
        {
            rows.Add(new KeyValuePair<string, string>("Status", st));
        }

        return rows;
    }

    /// <summary>A one-line subtitle for list headers ("16 cores · 5.4 GHz", "12 GB · driver 32.0.15").</summary>
    public static string? Subtitle(HardwareItem item) => item switch
    {
        ComputerSystemInfo s => JoinParts(s.Model, s.ChassisType),
        OperatingSystemInfo os => VersionText(os),
        BiosInfo b => JoinParts(b.SmBiosVersion ?? b.Version, b.IsUefi == true ? "UEFI" : null, b.SecureBootEnabled == true ? "Secure Boot on" : null),
        MotherboardInfo mb => JoinParts(mb.Product, mb.Chipset),
        CpuInfo c => JoinParts(c.Cores is null ? null : $"{c.Cores} cores / {c.LogicalProcessors} threads", Mhz(c.MaxClockMhz), c.Socket),
        GpuInfo g => JoinParts(Bytes(g.AdapterRamBytes), g.DriverVersion is null ? null : "driver " + g.DriverVersion, g.IsIntegrated ? "integrated" : null),
        MemoryModuleInfo m => JoinParts(Bytes(m.CapacityBytes), m.MemoryType, m.ConfiguredClockSpeedMhz is null ? null : $"{m.ConfiguredClockSpeedMhz} MT/s", m.DeviceLocator),
        PhysicalDiskInfo d => JoinParts(Bytes(d.SizeBytes), d.MediaType, d.BusType ?? d.InterfaceType, d.HealthStatusText),
        LogicalDriveInfo l => JoinParts(l.VolumeName, l.FileSystem, l.FreeSpaceBytes is null ? null : Bytes(l.FreeSpaceBytes) + " free", Bytes(l.SizeBytes)),
        NetworkAdapterInfo n => JoinParts(n.AdapterType, n.NetConnectionStatus, BitsPerSecond(n.SpeedBitsPerSecond), n.IpAddresses.FirstOrDefault()),
        MonitorInfo mo => JoinParts(mo.ScreenWidth is > 0 ? $"{mo.ScreenWidth} x {mo.ScreenHeight}" : null, mo.Get("DiagonalInches") is { } di ? di + "\"" : null, mo.YearOfManufacture?.ToString()),
        AudioDeviceInfo a => a.ProductName,
        UsbDeviceInfo u => JoinParts(u.PnpClass, u.Description),
        BatteryInfo bt => JoinParts(bt.EstimatedChargePercent is null ? null : bt.EstimatedChargePercent + " %", bt.BatteryStatusText, bt.Chemistry),
        InputDeviceInfo i => JoinParts(i.Kind, i.Description),
        PnpDeviceInfo p => JoinParts(p.PnpClass, p.ConfigManagerErrorCode is > 0 ? $"problem code {p.ConfigManagerErrorCode}" : null),
        _ => item.Manufacturer,
    };

    /// <summary>"Good" for OK/Healthy, "Critical" for errors, "Warning" for degraded, otherwise Unknown.</summary>
    public static HealthStatus StatusOf(HardwareItem item)
    {
        if (item is PnpDeviceInfo { ConfigManagerErrorCode: > 0 })
        {
            return HealthStatus.Critical;
        }

        if (item is PhysicalDiskInfo { HealthStatusText: { } h })
        {
            if (h.Contains("Unhealthy", StringComparison.OrdinalIgnoreCase)) return HealthStatus.Critical;
            if (h.Contains("Warning", StringComparison.OrdinalIgnoreCase)) return HealthStatus.Warning;
        }

        var status = item.Status;
        if (string.IsNullOrWhiteSpace(status))
        {
            return HealthStatus.Unknown;
        }

        if (status.Equals("OK", StringComparison.OrdinalIgnoreCase) || status.Equals("Healthy", StringComparison.OrdinalIgnoreCase))
        {
            return HealthStatus.Good;
        }

        if (status.Contains("Error", StringComparison.OrdinalIgnoreCase) || status.Contains("Fail", StringComparison.OrdinalIgnoreCase)
            || status.Contains("Lost", StringComparison.OrdinalIgnoreCase) || status.Contains("No Contact", StringComparison.OrdinalIgnoreCase))
        {
            return HealthStatus.Critical;
        }

        if (status.Contains("Degraded", StringComparison.OrdinalIgnoreCase) || status.Contains("Warn", StringComparison.OrdinalIgnoreCase)
            || status.Contains("Pred", StringComparison.OrdinalIgnoreCase) || status.Contains("Stress", StringComparison.OrdinalIgnoreCase))
        {
            return HealthStatus.Warning;
        }

        return HealthStatus.Unknown;
    }

    public static string? Bytes(long? bytes) => bytes is null ? null : ByteSize.Format(bytes.Value);

    public static string? Mhz(int? mhz) => mhz switch
    {
        null or <= 0 => null,
        >= 1000 => (mhz.Value / 1000.0).ToString("0.0#", CultureInfo.InvariantCulture) + " GHz",
        _ => mhz.Value.ToString(CultureInfo.InvariantCulture) + " MHz",
    };

    public static string? Kb(int? kb) => kb switch
    {
        null or <= 0 => null,
        >= 1024 => (kb.Value / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " MB",
        _ => kb.Value.ToString(CultureInfo.InvariantCulture) + " KB",
    };

    public static string? Mwh(int? mwh) => mwh switch
    {
        null or <= 0 => null,
        >= 1000 => (mwh.Value / 1000.0).ToString("0.##", CultureInfo.InvariantCulture) + " Wh",
        _ => mwh.Value.ToString(CultureInfo.InvariantCulture) + " mWh",
    };

    public static string? BitsPerSecond(long? bps) => bps switch
    {
        null or <= 0 => null,
        >= 1_000_000_000 => (bps.Value / 1_000_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + " Gbit/s",
        >= 1_000_000 => (bps.Value / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + " Mbit/s",
        >= 1_000 => (bps.Value / 1_000.0).ToString("0.#", CultureInfo.InvariantCulture) + " kbit/s",
        _ => bps.Value.ToString(CultureInfo.InvariantCulture) + " bit/s",
    };

    public static string? Mode(int? width, int? height, int? refresh)
    {
        if (width is not > 0 || height is not > 0)
        {
            return null;
        }

        return refresh is > 1 ? $"{width} x {height} @ {refresh} Hz" : $"{width} x {height}";
    }

    /// <summary>"24H2 (build 26100.3037)" or whatever parts are known.</summary>
    public static string? VersionText(OperatingSystemInfo os)
    {
        var build = os.BuildNumber;
        if (build is not null && os.Get("Registry.UBR") is { } ubr)
        {
            build = $"{build}.{ubr}";
        }

        return JoinParts(os.DisplayVersion, build is null ? os.Version : $"build {build}");
    }

    public static string Uptime(DateTime lastBoot)
    {
        var span = DateTime.Now - lastBoot;
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        return span.TotalDays >= 1 ? $"{(int)span.TotalDays} d {span.Hours} h" : span.TotalHours >= 1 ? $"{(int)span.TotalHours} h {span.Minutes} min" : $"{(int)span.TotalMinutes} min";
    }

    public static string AgeText(DateTime date)
    {
        var days = (DateTime.Now - date).TotalDays;
        if (days < 0) return "in the future";
        if (days < 31) return $"{(int)days} days ago";
        if (days < 365) return $"{(int)(days / 30.4)} months ago";
        return $"{days / 365.25:0.#} years ago";
    }

    public static string Date(DateTime? date, bool withTime = false) =>
        date is null ? string.Empty : date.Value.ToString(withTime ? "yyyy-MM-dd HH:mm" : "yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Short explanation of common Device Manager problem codes.</summary>
    public static string ProblemText(int code) => code switch
    {
        1 => "not configured correctly", 3 => "driver may be corrupted or the system is low on resources", 10 => "device cannot start",
        12 => "not enough free resources", 14 => "needs a restart", 18 => "reinstall the drivers", 19 => "registry information is damaged",
        21 => "Windows is removing this device", 22 => "disabled", 24 => "not present, not working or drivers missing",
        28 => "drivers are not installed", 29 => "disabled by firmware", 31 => "Windows cannot load the driver", 32 => "driver service is disabled",
        33 => "cannot determine required resources", 34 => "needs manual configuration", 35 => "firmware does not describe this device correctly",
        36 => "IRQ configuration problem", 37 => "driver failed to initialise", 38 => "previous driver instance still in memory",
        39 => "driver is missing or corrupted", 40 => "registry service entry is invalid", 41 => "driver loaded but device not found",
        42 => "duplicate device", 43 => "device reported a problem and was stopped", 44 => "stopped by an application or service",
        45 => "not connected (remembered device)", 46 => "Windows is shutting down", 47 => "safe removal pending", 48 => "driver blocked for stability",
        49 => "system hive is too large", 50 => "driver properties could not be applied", 51 => "waiting on another device", 52 => "driver signature problem",
        53 => "reserved by the Windows kernel debugger", 54 => "device failed and is being reset", _ => "unknown problem",
    };

    private static string PartitionText(PartitionInfo p)
    {
        var letter = p.Get("DriveLetter");
        var size = Bytes(p.SizeBytes);
        var name = p.Index is { } idx ? $"#{idx}" : p.Name;
        return JoinParts(name, letter, size, p.Type) ?? p.Name;
    }

    private static string? Join(IReadOnlyList<string>? values) => values is null || values.Count == 0 ? null : string.Join(", ", values);

    private static string? JoinParts(params string?[] parts)
    {
        var filtered = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
        return filtered.Length == 0 ? null : string.Join(" · ", filtered);
    }
}
