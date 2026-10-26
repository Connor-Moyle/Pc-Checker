using System.Globalization;
using PcChecker.Core.Common;
using PcChecker.Core.Drivers;
using PcChecker.Core.Hardware;
using PcChecker.Core.Health;
using PcChecker.Core.Performance;
using PcChecker.Core.Sensors;
using PcChecker.Core.Software;
using static PcChecker.Core.Reports.HtmlReportBuilder;

namespace PcChecker.Core.Reports;

/// <summary>The per-module sections of the HTML report.</summary>
internal static class HtmlReportSections
{
    private const int MaxErrorRows = 50;
    private const int MaxPnpRows = 400;

    public static void Health(HtmlWriter w, HealthReport health)
    {
        w.OpenSection("health", "Health", $"Score {health.Score}/100 — {ReportText.ScoreLabel(health.Score)}. Checked {ReportText.DateTime(health.Captured)}.");

        w.Heading(3, "Findings");
        w.Table(["Component", "Status", "Finding", "Details", "What to do"],
            health.Items.OrderBy(i => i.Status == HealthStatus.Critical ? 0 : i.Status == HealthStatus.Warning ? 1 : 2).Select(i => (IReadOnlyList<HtmlWriter.Cell>)
            [
                i.Component, new HtmlWriter.Cell(i.Status.ToString(), StatusClass(i.Status)), i.Title, i.Detail, ReportText.Or(i.Recommendation, string.Empty),
            ]), "No health findings.");

        if (health.Disks.Count > 0)
        {
            w.Heading(3, "Drives (SMART)");
            w.Table(["Drive", "Type", "Size", "Status", "Temp", "Power-on hours", "Reallocated", "Pending", "Uncorrectable", "Wear"],
                health.Disks.Select(d => (IReadOnlyList<HtmlWriter.Cell>)
                [
                    d.Model, d.MediaType, ReportText.Bytes(d.SizeBytes), new HtmlWriter.Cell(ReportText.Or(d.SmartStatusText, d.Status.ToString()), StatusClass(d.Status)),
                    ReportText.Number(d.TemperatureC, "0", "°C"), ReportText.Number(d.PowerOnHours), ReportText.Number(d.ReallocatedSectors),
                    ReportText.Number(d.PendingSectors), ReportText.Number(d.UncorrectableErrors), ReportText.Percent(d.WearPercent),
                ]));
            foreach (var disk in health.Disks.Where(d => d.Attributes.Count > 0))
            {
                w.OpenDetails($"SMART attributes — {disk.Model}");
                w.Table(["ID", "Attribute", "Current", "Worst", "Threshold", "Raw", "Failed"],
                    disk.Attributes.Select(a => (IReadOnlyList<HtmlWriter.Cell>)
                    [
                        a.Id.ToString(CultureInfo.InvariantCulture), a.Name, ReportText.Number(a.Current), ReportText.Number(a.Worst), ReportText.Number(a.Threshold),
                        ReportText.Number(a.RawValue), new HtmlWriter.Cell(a.Failed ? "Yes" : "No", a.Failed ? "bad" : null),
                    ]));
                w.CloseDetails();
            }
        }

        if (health.Batteries.Count > 0)
        {
            w.Heading(3, "Batteries");
            w.Table(["Battery", "Design capacity", "Full charge capacity", "Wear", "Cycles", "Chemistry", "Status", "Charge"],
                health.Batteries.Select(b => (IReadOnlyList<HtmlWriter.Cell>)
                [
                    b.Name, Mwh(b.DesignCapacityMwh), Mwh(b.FullChargeCapacityMwh), ReportText.Percent(b.WearPercent), ReportText.Number(b.CycleCount),
                    ReportText.Or(b.Chemistry), b.Status, ReportText.Percent(b.ChargePercent),
                ]));
        }

        if (health.DeviceProblems.Count > 0)
        {
            w.Heading(3, "Devices with problems");
            w.Table(["Device", "Code", "Problem", "Suggestion", "Device id"],
                health.DeviceProblems.Select(p => (IReadOnlyList<HtmlWriter.Cell>)
                [
                    p.Name, p.ErrorCode.ToString(CultureInfo.InvariantCulture), p.Description, p.Suggestion, p.DeviceId,
                ]));
        }

        if (health.SystemErrors.Count > 0)
        {
            w.Heading(3, $"Recent system errors ({health.SystemErrors.Count})");
            w.Table(["Time", "Log", "Source", "Level", "Event", "Message"],
                health.SystemErrors.Take(MaxErrorRows).Select(e => (IReadOnlyList<HtmlWriter.Cell>)
                [
                    ReportText.DateTime(e.Time), e.Log, e.Source, new HtmlWriter.Cell(e.Level.ToString(), SeverityClass(e.Level)), e.EventId.ToString(CultureInfo.InvariantCulture), e.Message,
                ]));
        }

        if (health.Reliability.Count > 0)
        {
            w.Heading(3, "Reliability history");
            w.Table(["Time", "Source", "Type", "Message"],
                health.Reliability.Take(MaxErrorRows).Select(r => (IReadOnlyList<HtmlWriter.Cell>) [ReportText.DateTime(r.Time), r.Source, r.Type, r.Message]));
        }

        if (health.CrashDumps.Count > 0)
        {
            w.Heading(3, "Crash dumps");
            w.Table(["Time", "File", "Size"],
                health.CrashDumps.Select(c => (IReadOnlyList<HtmlWriter.Cell>) [ReportText.DateTime(c.Time), c.Path, ReportText.Bytes(c.SizeBytes)]));
        }

        w.CloseSection();
    }

    public static void Hardware(HtmlWriter w, HardwareInventory hw)
    {
        w.OpenSection("hardware", "Hardware", $"Scanned {ReportText.DateTime(hw.Captured)}. Raw properties for every device are in the fold-outs.");

        if (hw.System is { } sys)
        {
            w.Heading(3, "System");
            w.KeyValues(
            [
                new("Name", sys.Name), new("Manufacturer", sys.Manufacturer), new("Model", sys.Model), new("Type", sys.SystemType),
                new("Chassis", sys.ChassisType), new("Installed memory", ReportText.Bytes(sys.TotalPhysicalMemoryBytes)),
                new("Logical processors", ReportText.Number(sys.NumberOfLogicalProcessors)), new("Domain", sys.Domain),
            ]);
            RawProperties(w, sys);
        }

        if (hw.OperatingSystem is { } os)
        {
            w.Heading(3, "Operating system");
            w.KeyValues(
            [
                new("Name", ReportText.Or(os.Caption, os.Name)), new("Version", ReportText.Join(os.DisplayVersion, os.Version)), new("Build", os.BuildNumber),
                new("Architecture", os.Architecture), new("Installed", ReportText.Date(os.InstallDate)), new("Last boot", ReportText.Date(os.LastBootUpTime)),
                new("Windows folder", os.WindowsDirectory), new("Locale", os.Locale),
            ]);
            RawProperties(w, os);
        }

        if (hw.Bios is { } bios)
        {
            w.Heading(3, "BIOS / UEFI");
            w.KeyValues(
            [
                new("Vendor", bios.Manufacturer), new("Version", bios.Version), new("SMBIOS version", bios.SmBiosVersion), new("Release date", ReportText.Date(bios.ReleaseDate)),
                new("UEFI", ReportText.YesNo(bios.IsUefi)), new("Secure Boot", ReportText.YesNo(bios.SecureBootEnabled)),
            ]);
            RawProperties(w, bios);
        }

        if (hw.Motherboard is { } board)
        {
            w.Heading(3, "Motherboard");
            w.KeyValues([new("Manufacturer", board.Manufacturer), new("Product", board.Product), new("Version", board.Version), new("Chipset", board.Chipset), new("Serial number", board.SerialNumber)]);
            RawProperties(w, board);
        }

        if (hw.Processors.Count > 0)
        {
            w.Heading(3, "Processors");
            w.Table(["Name", "Cores", "Threads", "Max clock", "Socket", "L3 cache", "Virtualization"],
                hw.Processors.Select(c => (IReadOnlyList<HtmlWriter.Cell>)
                [
                    c.Name, ReportText.Number(c.Cores), ReportText.Number(c.LogicalProcessors), ReportText.Megahertz(c.MaxClockMhz), ReportText.Or(c.Socket),
                    c.L3CacheKb is { } l3 ? $"{l3 / 1024} MB" : ReportText.Dash, ReportText.YesNo(c.VirtualizationEnabled),
                ]));
            RawProperties(w, hw.Processors);
        }

        if (hw.Graphics.Count > 0)
        {
            w.Heading(3, "Graphics");
            w.Table(["Name", "Vendor", "Video memory", "Driver", "Driver date", "Current mode", "Integrated"],
                hw.Graphics.Select(g => (IReadOnlyList<HtmlWriter.Cell>)
                [
                    g.Name, ReportText.Or(g.Vendor, g.Manufacturer), ReportText.Bytes(g.AdapterRamBytes), ReportText.Or(g.DriverVersion), ReportText.Date(g.DriverDate),
                    g.CurrentHorizontalResolution is { } hres ? $"{hres} x {g.CurrentVerticalResolution} @ {g.CurrentRefreshRate} Hz" : ReportText.Dash, g.IsIntegrated ? "Yes" : "No",
                ]));
            RawProperties(w, hw.Graphics);
        }

        if (hw.MemoryModules.Count > 0)
        {
            w.Heading(3, "Memory");
            w.Table(["Slot", "Capacity", "Rated speed", "Running at", "Type", "Form factor", "Manufacturer", "Part number"],
                hw.MemoryModules.Select(m => (IReadOnlyList<HtmlWriter.Cell>)
                [
                    ReportText.Or(m.DeviceLocator, m.BankLabel), ReportText.Bytes(m.CapacityBytes), m.SpeedMhz is { } s ? $"{s} MT/s" : ReportText.Dash,
                    m.ConfiguredClockSpeedMhz is { } cs ? $"{cs} MT/s" : ReportText.Dash, ReportText.Or(m.MemoryType), ReportText.Or(m.FormFactor), ReportText.Or(m.Manufacturer), ReportText.Or(m.PartNumber),
                ]));
            RawProperties(w, hw.MemoryModules);
        }

        if (hw.PhysicalDisks.Count > 0)
        {
            w.Heading(3, "Physical disks");
            w.Table(["Model", "Size", "Media", "Bus", "Firmware", "Health", "Partitions", "Serial"],
                hw.PhysicalDisks.Select(d => (IReadOnlyList<HtmlWriter.Cell>)
                [
                    ReportText.Or(d.Model, d.Name), ReportText.Bytes(d.SizeBytes), ReportText.Or(d.MediaType), ReportText.Or(d.BusType, d.InterfaceType), ReportText.Or(d.FirmwareRevision),
                    ReportText.Or(d.HealthStatusText), d.PartitionCount.ToString(CultureInfo.InvariantCulture), ReportText.Or(d.SerialNumber),
                ]));
            RawProperties(w, hw.PhysicalDisks);
        }

        if (hw.LogicalDrives.Count > 0)
        {
            w.Heading(3, "Drives");
            w.Table(["Drive", "Label", "File system", "Type", "Size", "Free", "Used"],
                hw.LogicalDrives.Select(d => (IReadOnlyList<HtmlWriter.Cell>)
                [
                    d.Name, ReportText.Or(d.VolumeName), ReportText.Or(d.FileSystem), ReportText.Or(d.DriveTypeText), ReportText.Bytes(d.SizeBytes), ReportText.Bytes(d.FreeSpaceBytes),
                    new HtmlWriter.Cell(ReportText.Percent(d.UsedPercent), d.UsedPercent >= 90 ? "bad" : d.UsedPercent >= 80 ? "warn" : null),
                ]));
        }

        if (hw.NetworkAdapters.Count > 0)
        {
            w.Heading(3, "Network adapters");
            w.Table(["Name", "Connection", "Status", "Speed", "MAC", "IP addresses", "DHCP"],
                hw.NetworkAdapters.Select(n => (IReadOnlyList<HtmlWriter.Cell>)
                [
                    n.Name, ReportText.Or(n.NetConnectionId), ReportText.Or(n.NetConnectionStatus, n.Status), n.SpeedBitsPerSecond is { } bps ? $"{bps / 1_000_000} Mbps" : ReportText.Dash,
                    ReportText.Or(n.MacAddress), string.Join(", ", n.IpAddresses), ReportText.YesNo(n.DhcpEnabled),
                ]));
            RawProperties(w, hw.NetworkAdapters);
        }

        if (hw.Monitors.Count > 0)
        {
            w.Heading(3, "Monitors");
            w.Table(["Name", "Manufacturer", "Product code", "Serial", "Made", "Native size"],
                hw.Monitors.Select(m => (IReadOnlyList<HtmlWriter.Cell>)
                [
                    m.Name, ReportText.Or(m.Manufacturer), ReportText.Or(m.ProductCode), ReportText.Or(m.SerialNumber), m.YearOfManufacture is { } y ? y.ToString(CultureInfo.InvariantCulture) : ReportText.Dash,
                    m.ScreenWidth is { } sw ? $"{sw} x {m.ScreenHeight}" : ReportText.Dash,
                ]));
        }

        if (hw.Batteries.Count > 0)
        {
            w.Heading(3, "Batteries");
            w.Table(["Name", "Chemistry", "Design capacity", "Full charge capacity", "Charge", "Status"],
                hw.Batteries.Select(b => (IReadOnlyList<HtmlWriter.Cell>)
                [
                    b.Name, ReportText.Or(b.Chemistry), Mwh(b.DesignCapacityMwh), Mwh(b.FullChargeCapacityMwh), ReportText.Percent(b.EstimatedChargePercent), ReportText.Or(b.BatteryStatusText),
                ]));
        }

        SimpleList(w, "Audio devices", hw.AudioDevices);
        SimpleList(w, "Input devices", hw.InputDevices);
        SimpleList(w, "USB devices", hw.UsbDevices);

        if (hw.PnpDevices.Count > 0)
        {
            w.OpenDetails($"All Plug and Play devices ({hw.PnpDevices.Count})");
            w.Table(["Name", "Class", "Manufacturer", "Status", "Problem code"],
                hw.PnpDevices.Take(MaxPnpRows).Select(p => (IReadOnlyList<HtmlWriter.Cell>)
                [
                    p.Name, ReportText.Or(p.PnpClass), ReportText.Or(p.Manufacturer), ReportText.Or(p.Status),
                    new HtmlWriter.Cell(p.ConfigManagerErrorCode is { } code && code != 0 ? code.ToString(CultureInfo.InvariantCulture) : "OK", p.ConfigManagerErrorCode is > 0 ? "bad" : null),
                ]));
            w.CloseDetails();
        }

        w.CloseSection();
    }

    public static void Performance(HtmlWriter w, PerformanceReport perf)
    {
        w.OpenSection("performance", "Performance", $"Score {perf.Score}/100 — {ReportText.ScoreLabel(perf.Score)}. " +
            $"Memory in use {ReportText.Percent(perf.MemoryUsedPercent)} of {ReportText.Bytes(perf.MemoryTotalBytes)}" +
            (perf.LastBootSeconds is { } boot ? $", last boot took {boot:0} s" : string.Empty) +
            (perf.ActivePowerPlan is { } plan ? $", power plan \"{plan}\"." : "."));

        w.Heading(3, "What is slowing the PC down");
        w.Table(["Severity", "Area", "Finding", "Details", "Impact"],
            perf.Findings.OrderByDescending(f => f.Severity).Select(f => (IReadOnlyList<HtmlWriter.Cell>)
            [
                new HtmlWriter.Cell(f.Severity.ToString(), SeverityClass(f.Severity)), f.Category.ToString(), f.Title, f.Detail, f.Impact,
            ]), "No slowdowns found.");

        if (perf.StartupItems.Count > 0)
        {
            w.Heading(3, "Startup items");
            w.Table(["Name", "Enabled", "Publisher", "Kind", "Location", "Command"],
                perf.StartupItems.Select(s => (IReadOnlyList<HtmlWriter.Cell>)
                [
                    s.Name, new HtmlWriter.Cell(s.Enabled ? "Yes" : "No", s.Enabled ? null : "muted"), ReportText.Or(s.Publisher), s.Kind, s.Location, s.Command,
                ]));
        }

        if (perf.TopProcesses.Count > 0)
        {
            w.Heading(3, "Busiest processes");
            w.Table(["Process", "CPU", "Memory", "Publisher"],
                perf.TopProcesses.Select(p => (IReadOnlyList<HtmlWriter.Cell>) [p.Name, ReportText.Percent(p.CpuPercent), ReportText.Bytes(p.WorkingSetBytes), ReportText.Or(p.Publisher)]));
        }

        if (perf.Drives.Count > 0)
        {
            w.Heading(3, "Drive space");
            w.Table(["Drive", "Type", "Free", "Total"],
                perf.Drives.Select(d => (IReadOnlyList<HtmlWriter.Cell>)
                [
                    d.Drive, d.MediaType, new HtmlWriter.Cell(ReportText.Bytes(d.Free), d.Total > 0 && d.Free < d.Total / 10 ? "bad" : null), ReportText.Bytes(d.Total),
                ]));
        }

        w.CloseSection();
    }

    public static void Drivers(HtmlWriter w, DriverScanResult drivers)
    {
        var flagged = drivers.Drivers.Where(d => d.HasProblem || !d.IsSigned || d.AgeDays > 3 * 365).ToList();
        w.OpenSection("drivers", "Drivers", $"{drivers.Drivers.Count} drivers scanned {ReportText.DateTime(drivers.Captured)}; {flagged.Count} worth a look (problem codes, unsigned or older than three years).");
        if (flagged.Count > 0)
        {
            w.Heading(3, "Worth a look");
            DriverTable(w, flagged);
        }

        w.OpenDetails($"All drivers ({drivers.Drivers.Count})");
        DriverTable(w, drivers.Drivers.OrderBy(d => d.DeviceClass).ThenBy(d => d.DeviceName).ToList());
        w.CloseDetails();
        w.CloseSection();
    }

    public static void Software(HtmlWriter w, SoftwareInventory software)
    {
        w.OpenSection("software", "Installed software", $"{software.Programs.Count} programs found {ReportText.DateTime(software.Captured)}.");
        w.Table(["Name", "Version", "Publisher", "Installed", "Size", "Source", "Note"],
            software.Programs.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Select(p => (IReadOnlyList<HtmlWriter.Cell>)
            [
                p.Name, ReportText.Or(p.Version), ReportText.Or(p.Publisher), ReportText.Date(p.InstallDate), ReportText.Bytes(p.SizeBytes), p.Source.ToString(),
                new HtmlWriter.Cell(p.IsProtected ? ReportText.Or(p.ProtectionReason, "Protected") : p.BundledWithId is not null ? "Installed alongside another program" : string.Empty, "muted"),
            ]), "No programs found.");
        if (software.Bundles.Count > 0)
        {
            w.Heading(3, "Installed together");
            w.Table(["Program", "Came with", "Why we think so"],
                software.Bundles.Select(b => (IReadOnlyList<HtmlWriter.Cell>) [b.Parent.Name, string.Join(", ", b.Children.Select(c => c.Name)), b.Reason]));
        }

        w.CloseSection();
    }

    public static void Sensors(HtmlWriter w, SensorSnapshot sensors)
    {
        var head = SnapshotSummary.Summarize(sensors);
        w.OpenSection("sensors", "Temperatures", $"Snapshot taken {ReportText.DateTime(sensors.Timestamp)}. Min and max are for the monitoring session.");
        w.KeyValues(
        [
            new("CPU", ReportText.Number(head.CpuTempC, "0", "°C")), new("GPU", ReportText.Number(head.GpuTempC, "0", "°C")),
            new("Hottest drive", ReportText.Number(head.HottestDriveC, "0", "°C")), new("CPU load", ReportText.Percent(head.CpuLoadPercent)),
            new("GPU load", ReportText.Percent(head.GpuLoadPercent)), new("Memory in use", ReportText.Percent(head.MemoryUsedPercent)),
            new("Power (CPU + GPU)", ReportText.Number(head.TotalPowerW, "0", "W")),
        ]);
        w.Table(["Part", "Sensor", "Now", "Min", "Max"],
            SnapshotSummary.Temperatures(sensors).Select(r => (IReadOnlyList<HtmlWriter.Cell>)
            [
                r.HardwareName, r.Name, new HtmlWriter.Cell(ReportText.Number(r.Value, "0.#", r.Unit), TempClass(r.Value, 80, 90)), ReportText.Number(r.Min, "0.#", r.Unit), ReportText.Number(r.Max, "0.#", r.Unit),
            ]), "No temperature sensors were available.");
        w.CloseSection();
    }

    private static void DriverTable(HtmlWriter w, IReadOnlyList<DriverInfo> drivers) =>
        w.Table(["Device", "Class", "Provider", "Version", "Date", "Age", "Signed", "Problem"],
            drivers.Select(d => (IReadOnlyList<HtmlWriter.Cell>)
            [
                d.DeviceName, d.DeviceClass, ReportText.Or(d.Provider), ReportText.Or(d.Version), ReportText.Date(d.Date),
                new HtmlWriter.Cell(d.AgeDays is { } age ? $"{age / 365} y {age % 365 / 30} m" : ReportText.Dash, d.AgeDays > 3 * 365 ? "warn" : null),
                new HtmlWriter.Cell(d.IsSigned ? "Yes" : "No", d.IsSigned ? null : "warn"),
                new HtmlWriter.Cell(d.HasProblem ? $"Code {d.ProblemCode}" : "None", d.HasProblem ? "bad" : null),
            ]));

    private static void SimpleList(HtmlWriter w, string title, IReadOnlyList<HardwareItem> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        w.Heading(3, title);
        w.Table(["Name", "Manufacturer", "Status"],
            items.Select(i => (IReadOnlyList<HtmlWriter.Cell>) [i.Name, ReportText.Or(i.Manufacturer), ReportText.Or(i.Status)]));
        RawProperties(w, items);
    }

    private static void RawProperties(HtmlWriter w, HardwareItem item) => RawProperties(w, [item]);

    private static void RawProperties(HtmlWriter w, IReadOnlyList<HardwareItem> items)
    {
        var withProps = items.Where(i => i.Properties.Count > 0).ToList();
        if (withProps.Count == 0)
        {
            return;
        }

        w.OpenDetails(withProps.Count == 1 ? "All properties" : $"All properties ({withProps.Count} devices)");
        foreach (var item in withProps)
        {
            if (withProps.Count > 1)
            {
                w.Paragraph(item.Name);
            }

            w.KeyValues(item.Properties.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)), skipEmpty: false);
        }

        w.CloseDetails();
    }

    private static string Mwh(int? mwh) => mwh.HasValue ? (mwh.Value / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " Wh" : ReportText.Dash;
}
