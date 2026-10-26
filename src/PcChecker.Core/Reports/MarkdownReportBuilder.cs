using System.Globalization;
using System.Text;
using PcChecker.Core.Common;
using PcChecker.Core.Drivers;
using PcChecker.Core.Hardware;
using PcChecker.Core.Health;
using PcChecker.Core.Performance;
using PcChecker.Core.Sensors;
using PcChecker.Core.Software;

namespace PcChecker.Core.Reports;

/// <summary>Builds the Markdown flavour of the report (same sections as the HTML one, GitHub-style tables). Pure.</summary>
public static class MarkdownReportBuilder
{
    public static string Build(ReportBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var sb = new StringBuilder(32 * 1024);
        var computer = bundle.Hardware?.System?.Name;
        sb.Append("# PC Checker report").Append(string.IsNullOrWhiteSpace(computer) ? string.Empty : " — " + ReportText.Markdown(computer)).AppendLine();
        sb.AppendLine();
        sb.Append("Generated ").Append(ReportText.DateTime(bundle.Generated)).Append(" by PC Checker ").Append(ReportText.Markdown(bundle.AppVersion)).AppendLine(".");
        sb.AppendLine();

        Summary(sb, bundle);
        if (bundle.Health is not null) { Health(sb, bundle.Health); }
        if (bundle.Hardware is not null) { Hardware(sb, bundle.Hardware); }
        if (bundle.Performance is not null) { Performance(sb, bundle.Performance); }
        if (bundle.Drivers is not null) { Drivers(sb, bundle.Drivers); }
        if (bundle.Software is not null) { Software(sb, bundle.Software); }
        if (bundle.Sensors is not null) { Sensors(sb, bundle.Sensors); }

        var warnings = new List<string>();
        warnings.AddRange(bundle.Hardware?.Warnings ?? []);
        warnings.AddRange(bundle.Health?.Warnings ?? []);
        warnings.AddRange(bundle.Performance?.Warnings ?? []);
        warnings.AddRange(bundle.Drivers?.Warnings ?? []);
        warnings.AddRange(bundle.Software?.Warnings ?? []);
        if (warnings.Count > 0)
        {
            sb.AppendLine("## Scan notes").AppendLine();
            foreach (var warning in warnings.Distinct())
            {
                sb.Append("- ").AppendLine(ReportText.Markdown(warning));
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static void Summary(StringBuilder sb, ReportBundle bundle)
    {
        sb.AppendLine("## Summary").AppendLine();
        if (bundle.Health is { } health)
        {
            sb.Append("- **Health score:** ").Append(health.Score).Append("/100 (").Append(ReportText.ScoreLabel(health.Score)).AppendLine(")");
        }

        if (bundle.Performance is { } perf)
        {
            sb.Append("- **Performance score:** ").Append(perf.Score).Append("/100, ").Append(perf.Findings.Count).AppendLine(" findings");
        }

        if (bundle.Drivers is { } drivers)
        {
            sb.Append("- **Drivers:** ").Append(drivers.Drivers.Count).Append(" scanned, ").Append(drivers.Drivers.Count(d => d.HasProblem)).AppendLine(" with problems");
        }

        if (bundle.Software is { } software)
        {
            sb.Append("- **Installed programs:** ").Append(software.Programs.Count).AppendLine();
        }

        if (bundle.Sensors is { } sensors)
        {
            var head = SnapshotSummary.Summarize(sensors);
            sb.Append("- **CPU / GPU temperature:** ").Append(ReportText.Number(head.CpuTempC, "0", "°C")).Append(" / ").AppendLine(ReportText.Number(head.GpuTempC, "0", "°C"));
        }

        if (bundle.Hardware is { } hw)
        {
            sb.AppendLine();
            KeyValues(sb,
            [
                ("Computer", hw.System?.Name), ("Make and model", ReportText.Join(hw.System?.Manufacturer, hw.System?.Model)),
                ("Processor", hw.Processors.FirstOrDefault()?.Name), ("Graphics", string.Join(", ", hw.Graphics.Select(g => g.Name))),
                ("Memory", hw.TotalMemoryBytes is { } total ? $"{ByteSize.Format(total)} in {hw.MemoryModules.Count} module(s)" : null),
                ("Operating system", ReportText.Join(hw.OperatingSystem?.Caption ?? hw.OperatingSystem?.Name, hw.OperatingSystem?.DisplayVersion)),
            ]);
        }

        sb.AppendLine();
    }

    private static void Health(StringBuilder sb, HealthReport health)
    {
        sb.AppendLine("## Health").AppendLine();
        Table(sb, ["Component", "Status", "Finding", "Details", "What to do"],
            health.Items.Select(i => new[] { i.Component, i.Status.ToString(), i.Title, i.Detail, i.Recommendation ?? string.Empty }));
        if (health.Disks.Count > 0)
        {
            sb.AppendLine("### Drives (SMART)").AppendLine();
            Table(sb, ["Drive", "Type", "Size", "Status", "Temp", "Power-on hours", "Reallocated", "Pending", "Wear"],
                health.Disks.Select(d => new[]
                {
                    d.Model, d.MediaType, ReportText.Bytes(d.SizeBytes), d.SmartStatusText ?? d.Status.ToString(), ReportText.Number(d.TemperatureC, "0", "°C"),
                    ReportText.Number(d.PowerOnHours), ReportText.Number(d.ReallocatedSectors), ReportText.Number(d.PendingSectors), ReportText.Percent(d.WearPercent),
                }));
        }

        if (health.Batteries.Count > 0)
        {
            sb.AppendLine("### Batteries").AppendLine();
            Table(sb, ["Battery", "Wear", "Cycles", "Status", "Charge"],
                health.Batteries.Select(b => new[] { b.Name, ReportText.Percent(b.WearPercent), ReportText.Number(b.CycleCount), b.Status, ReportText.Percent(b.ChargePercent) }));
        }

        if (health.DeviceProblems.Count > 0)
        {
            sb.AppendLine("### Devices with problems").AppendLine();
            Table(sb, ["Device", "Code", "Problem", "Suggestion"],
                health.DeviceProblems.Select(p => new[] { p.Name, p.ErrorCode.ToString(CultureInfo.InvariantCulture), p.Description, p.Suggestion }));
        }

        if (health.SystemErrors.Count > 0)
        {
            sb.AppendLine("### Recent system errors").AppendLine();
            Table(sb, ["Time", "Source", "Level", "Event", "Message"],
                health.SystemErrors.Take(50).Select(e => new[] { ReportText.DateTime(e.Time), e.Source, e.Level.ToString(), e.EventId.ToString(CultureInfo.InvariantCulture), e.Message }));
        }

        if (health.CrashDumps.Count > 0)
        {
            sb.AppendLine("### Crash dumps").AppendLine();
            Table(sb, ["Time", "File", "Size"], health.CrashDumps.Select(c => new[] { ReportText.DateTime(c.Time), c.Path, ReportText.Bytes(c.SizeBytes) }));
        }
    }

    private static void Hardware(StringBuilder sb, HardwareInventory hw)
    {
        sb.AppendLine("## Hardware").AppendLine();
        if (hw.Processors.Count > 0)
        {
            sb.AppendLine("### Processors").AppendLine();
            Table(sb, ["Name", "Cores", "Threads", "Max clock", "Socket"],
                hw.Processors.Select(c => new[] { c.Name, ReportText.Number(c.Cores), ReportText.Number(c.LogicalProcessors), ReportText.Megahertz(c.MaxClockMhz), c.Socket ?? ReportText.Dash }));
        }

        if (hw.Graphics.Count > 0)
        {
            sb.AppendLine("### Graphics").AppendLine();
            Table(sb, ["Name", "Video memory", "Driver", "Driver date"],
                hw.Graphics.Select(g => new[] { g.Name, ReportText.Bytes(g.AdapterRamBytes), g.DriverVersion ?? ReportText.Dash, ReportText.Date(g.DriverDate) }));
        }

        if (hw.MemoryModules.Count > 0)
        {
            sb.AppendLine("### Memory").AppendLine();
            Table(sb, ["Slot", "Capacity", "Rated", "Running at", "Type", "Part number"],
                hw.MemoryModules.Select(m => new[]
                {
                    m.DeviceLocator ?? m.BankLabel ?? ReportText.Dash, ReportText.Bytes(m.CapacityBytes), m.SpeedMhz is { } s ? $"{s} MT/s" : ReportText.Dash,
                    m.ConfiguredClockSpeedMhz is { } cs ? $"{cs} MT/s" : ReportText.Dash, m.MemoryType ?? ReportText.Dash, m.PartNumber ?? ReportText.Dash,
                }));
        }

        if (hw.PhysicalDisks.Count > 0)
        {
            sb.AppendLine("### Physical disks").AppendLine();
            Table(sb, ["Model", "Size", "Media", "Bus", "Health"],
                hw.PhysicalDisks.Select(d => new[] { d.Model ?? d.Name, ReportText.Bytes(d.SizeBytes), d.MediaType ?? ReportText.Dash, d.BusType ?? d.InterfaceType ?? ReportText.Dash, d.HealthStatusText ?? ReportText.Dash }));
        }

        if (hw.LogicalDrives.Count > 0)
        {
            sb.AppendLine("### Drives").AppendLine();
            Table(sb, ["Drive", "Label", "File system", "Size", "Free", "Used"],
                hw.LogicalDrives.Select(d => new[] { d.Name, d.VolumeName ?? ReportText.Dash, d.FileSystem ?? ReportText.Dash, ReportText.Bytes(d.SizeBytes), ReportText.Bytes(d.FreeSpaceBytes), ReportText.Percent(d.UsedPercent) }));
        }

        if (hw.NetworkAdapters.Count > 0)
        {
            sb.AppendLine("### Network adapters").AppendLine();
            Table(sb, ["Name", "Connection", "Status", "IP addresses"],
                hw.NetworkAdapters.Select(n => new[] { n.Name, n.NetConnectionId ?? ReportText.Dash, n.NetConnectionStatus ?? n.Status ?? ReportText.Dash, string.Join(", ", n.IpAddresses) }));
        }

        if (hw.Monitors.Count > 0)
        {
            sb.AppendLine("### Monitors").AppendLine();
            Table(sb, ["Name", "Manufacturer", "Serial"], hw.Monitors.Select(m => new[] { m.Name, m.Manufacturer ?? ReportText.Dash, m.SerialNumber ?? ReportText.Dash }));
        }
    }

    private static void Performance(StringBuilder sb, PerformanceReport perf)
    {
        sb.AppendLine("## Performance").AppendLine();
        sb.Append("Score ").Append(perf.Score).Append("/100. Memory in use ").Append(ReportText.Percent(perf.MemoryUsedPercent)).Append(" of ").Append(ReportText.Bytes(perf.MemoryTotalBytes)).AppendLine(".").AppendLine();
        Table(sb, ["Severity", "Area", "Finding", "Details", "Impact"],
            perf.Findings.OrderByDescending(f => f.Severity).Select(f => new[] { f.Severity.ToString(), f.Category.ToString(), f.Title, f.Detail, f.Impact }));
        if (perf.StartupItems.Count > 0)
        {
            sb.AppendLine("### Startup items").AppendLine();
            Table(sb, ["Name", "Enabled", "Publisher", "Kind", "Command"],
                perf.StartupItems.Select(s => new[] { s.Name, s.Enabled ? "Yes" : "No", s.Publisher ?? ReportText.Dash, s.Kind, s.Command }));
        }
    }

    private static void Drivers(StringBuilder sb, DriverScanResult drivers)
    {
        sb.AppendLine("## Drivers").AppendLine();
        sb.Append(drivers.Drivers.Count).AppendLine(" drivers scanned.").AppendLine();
        var flagged = drivers.Drivers.Where(d => d.HasProblem || !d.IsSigned || d.AgeDays > 3 * 365).ToList();
        if (flagged.Count > 0)
        {
            sb.AppendLine("### Worth a look").AppendLine();
            Table(sb, ["Device", "Class", "Provider", "Version", "Date", "Signed", "Problem"],
                flagged.Select(d => new[] { d.DeviceName, d.DeviceClass, d.Provider ?? ReportText.Dash, d.Version ?? ReportText.Dash, ReportText.Date(d.Date), d.IsSigned ? "Yes" : "No", d.HasProblem ? $"Code {d.ProblemCode}" : "None" }));
        }
    }

    private static void Software(StringBuilder sb, SoftwareInventory software)
    {
        sb.AppendLine("## Installed software").AppendLine();
        Table(sb, ["Name", "Version", "Publisher", "Installed", "Size"],
            software.Programs.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Select(p => new[] { p.Name, p.Version ?? ReportText.Dash, p.Publisher ?? ReportText.Dash, ReportText.Date(p.InstallDate), ReportText.Bytes(p.SizeBytes) }));
    }

    private static void Sensors(StringBuilder sb, SensorSnapshot sensors)
    {
        sb.AppendLine("## Temperatures").AppendLine();
        Table(sb, ["Part", "Sensor", "Now", "Min", "Max"],
            SnapshotSummary.Temperatures(sensors).Select(r => new[] { r.HardwareName, r.Name, ReportText.Number(r.Value, "0.#", r.Unit), ReportText.Number(r.Min, "0.#", r.Unit), ReportText.Number(r.Max, "0.#", r.Unit) }));
    }

    private static void KeyValues(StringBuilder sb, IEnumerable<(string Key, string? Value)> pairs)
    {
        sb.AppendLine("| Item | Value |").AppendLine("|---|---|");
        foreach (var (key, value) in pairs)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                sb.Append("| ").Append(ReportText.Markdown(key)).Append(" | ").Append(ReportText.Markdown(value)).AppendLine(" |");
            }
        }
    }

    /// <summary>Writes a GitHub-flavoured Markdown table; an empty row set becomes a one-line note.</summary>
    public static void Table(StringBuilder sb, IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows)
    {
        var list = rows.ToList();
        if (list.Count == 0)
        {
            sb.AppendLine("_Nothing to show._").AppendLine();
            return;
        }

        sb.Append("| ").Append(string.Join(" | ", headers.Select(ReportText.Markdown))).AppendLine(" |");
        sb.Append('|').Append(string.Concat(Enumerable.Repeat("---|", headers.Count))).AppendLine();
        foreach (var row in list)
        {
            sb.Append("| ").Append(string.Join(" | ", row.Select(ReportText.Markdown))).AppendLine(" |");
        }

        sb.AppendLine();
    }
}
