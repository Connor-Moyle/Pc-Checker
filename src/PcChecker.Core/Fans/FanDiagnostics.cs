using System.Globalization;
using System.Text;
using PcChecker.Core.Sensors;
using PcChecker.Core.Sensors.PawnIo;

namespace PcChecker.Core.Fans;

/// <summary>One hardware item the monitor found, flattened with its depth (sub-hardware such as the Super I/O chip is 1).</summary>
public sealed record FanHardwareNode(string Name, string Type, HardwareKind Kind, int Depth, int SensorCount, int FanCount, int ControlCount);

/// <summary>Everything the "Why is a fan missing?" panel and the copyable report are built from.</summary>
public sealed record FanDiagnosticInput(
    PawnIoStatus PawnIo,
    bool MonitorOpen,
    string? MonitorProblem,
    IReadOnlyList<FanHardwareNode> Hardware,
    IReadOnlyList<FanSensorInfo> Sensors,
    IReadOnlyList<FanPair> Pairs,
    IReadOnlyList<ConflictingApp> Conflicts,
    bool IsLaptop);

/// <summary>A plain-English finding: what is wrong and what to do about it.</summary>
public sealed record FanFinding(string Title, string Advice, bool IsProblem);

/// <summary>The built diagnostics: findings for the page, the fan controller chips found, and the copyable text.</summary>
public sealed record FanDiagnosticReport(
    PawnIoStatus PawnIo,
    string? MotherboardName,
    IReadOnlyList<string> FanControllerChips,
    IReadOnlyList<FanFinding> Findings,
    IReadOnlyList<FanSensorRow> SensorRows,
    IReadOnlyList<ConflictingApp> Conflicts,
    string Text)
{
    /// <summary>"Nuvoton NCT6798D", or the plain message when the motherboard has no fan controller chip.</summary>
    public string ChipText => FanControllerChips.Count > 0 ? string.Join(", ", FanControllerChips) : FanDiagnostics.NoChipText;
}

/// <summary>One fan or control sensor as the diagnostics list shows it.</summary>
public sealed record FanSensorRow(string HardwareName, string Name, string Kind, int Index, string Value, string Pairing);

/// <summary>
/// Pure builder for the fan diagnostics. The report holds only hardware and sensor names, values and states plus the
/// names of running programs from <see cref="FanConflictCatalog"/>: no user, computer or file names, no serial numbers.
/// </summary>
public static class FanDiagnostics
{
    public const string NoChipText = "no fan controller chip found";

    /// <summary>Why a fan that is visible may still not be controllable, in plain English.</summary>
    public static IReadOnlyList<string> WhyNoControl { get; } =
    [
        "The BIOS is in charge: Smart Fan, Q-Fan or Fan Xpert control can take the header back. In the BIOS fan settings set the header to manual (or turn off \"full speed\" / \"Q-Fan\" for it), save and restart.",
        "DC or PWM mode: a 3-pin fan on a header set to PWM, or a 4-pin fan on a header set to DC, ignores speed changes. Set the header's mode in the BIOS to match the fan.",
        "Fans on a hub, a fan controller or an all-in-one cooler driven over USB (Corsair, NZXT, Lian Li, Thermaltake and others) are only controllable in their maker's app.",
        "Laptops and many pre-built PCs keep their fans behind the embedded controller; only the maker's utility or the BIOS can change them.",
        "Another fan or RGB program can hold the sensor chip and keep setting its own speeds. Close it while you use fan control here.",
    ];

    public static FanDiagnosticReport Build(FanDiagnosticInput input, DateTimeOffset? captured = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        var board = input.Hardware.FirstOrDefault(h => h.Kind == HardwareKind.Motherboard && h.Depth == 0);
        var chips = input.Hardware.Where(h => h.Kind == HardwareKind.Motherboard && h.Depth > 0).Select(h => h.Name).Distinct().ToList();
        var rows = BuildRows(input);
        var findings = Findings(input, board, chips);
        var text = BuildText(input, board, chips, rows, findings, captured);
        return new FanDiagnosticReport(input.PawnIo, board?.Name, chips, findings, rows, input.Conflicts, text);
    }

    public static IReadOnlyList<FanFinding> Findings(FanDiagnosticInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var board = input.Hardware.FirstOrDefault(h => h.Kind == HardwareKind.Motherboard && h.Depth == 0);
        var chips = input.Hardware.Where(h => h.Kind == HardwareKind.Motherboard && h.Depth > 0).Select(h => h.Name).Distinct().ToList();
        return Findings(input, board, chips);
    }

    private static List<FanFinding> Findings(FanDiagnosticInput input, FanHardwareNode? board, IReadOnlyList<string> chips)
    {
        var list = new List<FanFinding>();
        var boardFans = input.Sensors.Where(s => s.Hardware == HardwareKind.Motherboard).ToList();

        if (!input.MonitorOpen)
        {
            list.Add(new FanFinding("The hardware monitor did not start",
                (input.MonitorProblem ?? "Run PC Checker as administrator.") + " Then click \"Reload sensors\".", true));
        }

        switch (input.PawnIo.State)
        {
            case PawnIoState.NotInstalled:
                list.Add(new FanFinding("PawnIO is not installed",
                    "PC Checker reads the motherboard fan headers through the free, signed PawnIO driver. Without it only graphics card fans (read through the graphics driver) appear. Click \"Install PawnIO\", or run \"winget install namazso.PawnIO\".", true));
                break;
            case PawnIoState.InstalledNotRunning:
                list.Add(new FanFinding("PawnIO is installed but not running",
                    "Restart the PC so the driver starts, then click \"Reload sensors\". If it still does not start, reinstall PawnIO and check that antivirus software is not blocking it.", true));
                break;
        }

        bool driverReady = input.PawnIo.State is PawnIoState.Running or PawnIoState.NotSupported or PawnIoState.Unknown;
        if (input.MonitorOpen && driverReady && board is not null && chips.Count == 0)
        {
            list.Add(new FanFinding("No fan controller chip was found on the motherboard",
                input.IsLaptop
                    ? "This is a laptop: its fans are run by the embedded controller and usually cannot be read or changed by other programs."
                    : "PawnIO is running, but the motherboard's sensor chip was not recognised. The chip may be too new for the built-in hardware library, or another program may be holding it. Close the programs listed below, click \"Reload sensors\", and check for a newer PC Checker.", true));
        }
        else if (input.MonitorOpen && chips.Count > 0 && boardFans.Count == 0)
        {
            list.Add(new FanFinding($"{string.Join(", ", chips)} reports no fan headers",
                "The chip was found but lists no fan sensors for this board. Fan headers may be wired to a second chip or to the embedded controller the library cannot read yet.", true));
        }

        var emptyHeaders = boardFans.Where(s => s.Kind == SensorKind.Fan && s.Value is { } v && v < 1).ToList();
        if (emptyHeaders.Count > 0)
        {
            list.Add(new FanFinding(emptyHeaders.Count == 1 ? "1 motherboard header reads 0 RPM" : $"{emptyHeaders.Count} motherboard headers read 0 RPM",
                "Usually nothing is plugged into them. They are hidden on the Fans page until you turn on \"Show empty headers\". A fan that is plugged in but reads 0 RPM may be stopped by the BIOS (zero-RPM mode), or plugged into a hub whose speed signal goes to another header.", false));
        }

        var unpairedControls = input.Pairs.Where(p => p.Rpm is null).ToList();
        if (unpairedControls.Count > 0)
        {
            list.Add(new FanFinding(unpairedControls.Count == 1 ? "1 control without RPM reading" : $"{unpairedControls.Count} controls without RPM reading",
                "These outputs can be driven, but no speed sensor could be matched to them with confidence, so PC Checker shows them without a speed instead of guessing.", false));
        }

        var readOnly = FanPairing.Unpaired(input.Sensors, input.Pairs);
        if (readOnly.Count > 0)
        {
            list.Add(new FanFinding(readOnly.Count == 1 ? "1 fan is read-only" : $"{readOnly.Count} fans are read-only",
                "Their speed is shown, but the hardware offers no matching control. See \"Why a fan cannot be controlled\" below.", false));
        }

        foreach (var app in input.Conflicts)
        {
            list.Add(new FanFinding($"{app.Product} is running",
                "It also talks to the fan controller and can override or garble what PC Checker sets. Close it (and its tray icon or service) while using fan control, then click \"Reload sensors\".", true));
        }

        if (list.Count == 0 || list.All(f => !f.IsProblem))
        {
            list.Insert(0, new FanFinding("No problems found", "Every fan the hardware reports is listed. A fan still missing is most likely on a hub, an all-in-one cooler or a controller only its maker's app can read.", false));
        }

        return list;
    }

    private static List<FanSensorRow> BuildRows(FanDiagnosticInput input)
    {
        var pairedRpm = input.Pairs.Where(p => p.Rpm is not null).ToDictionary(p => p.Rpm!.Id, p => p, StringComparer.OrdinalIgnoreCase);
        var byControl = input.Pairs.ToDictionary(p => p.Control.Id, p => p, StringComparer.OrdinalIgnoreCase);
        var rows = new List<FanSensorRow>();
        foreach (var s in input.Sensors.OrderBy(s => s.Hardware).ThenBy(s => s.HardwareName, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Kind).ThenBy(s => s.Index))
        {
            string pairing;
            string value;
            if (s.Kind == SensorKind.Control)
            {
                value = s.Value is { } p ? p.ToString("0", CultureInfo.InvariantCulture) + " %" : "no reading";
                pairing = byControl.TryGetValue(s.Id, out var pair) && pair.Rpm is { } rpm
                    ? $"paired with \"{rpm.Name}\" ({pair.Reason})"
                    : "control without RPM reading";
            }
            else
            {
                value = s.Value is { } r ? r.ToString("0", CultureInfo.InvariantCulture) + " RPM" : "no reading";
                pairing = pairedRpm.TryGetValue(s.Id, out var pair)
                    ? $"paired with \"{pair.Control.Name}\" ({pair.Reason})"
                    : "read-only (no matching control)";
            }

            rows.Add(new FanSensorRow(s.HardwareName, s.Name, s.Kind == SensorKind.Control ? "Control" : "Fan", s.Index, value, pairing));
        }

        return rows;
    }

    private static string BuildText(FanDiagnosticInput input, FanHardwareNode? board, IReadOnlyList<string> chips,
        IReadOnlyList<FanSensorRow> rows, IReadOnlyList<FanFinding> findings, DateTimeOffset? captured)
    {
        var sb = new StringBuilder();
        sb.AppendLine("PC Checker fan diagnostic report");
        if (captured is { } time)
        {
            sb.AppendLine("Created: " + time.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        }

        sb.AppendLine();
        sb.AppendLine("PawnIO driver: " + input.PawnIo.Summary);
        sb.AppendLine("Hardware monitor: " + (input.MonitorOpen ? "running" : "not running" + (input.MonitorProblem is { } p ? " (" + p + ")" : string.Empty)));
        sb.AppendLine("Motherboard: " + (board?.Name ?? "not detected"));
        sb.AppendLine("Fan controller chip: " + (chips.Count > 0 ? string.Join(", ", chips) : NoChipText));
        sb.AppendLine("PC type: " + (input.IsLaptop ? "laptop" : "desktop"));

        sb.AppendLine();
        sb.AppendLine("Hardware found:");
        if (input.Hardware.Count == 0)
        {
            sb.AppendLine("  (none)");
        }

        foreach (var node in input.Hardware)
        {
            sb.Append(new string(' ', 2 + (node.Depth * 2)))
              .Append(node.Name).Append(" [").Append(node.Type).Append("] ")
              .Append(node.SensorCount.ToString(CultureInfo.InvariantCulture)).Append(" sensors, ")
              .Append(node.FanCount.ToString(CultureInfo.InvariantCulture)).Append(" fan, ")
              .Append(node.ControlCount.ToString(CultureInfo.InvariantCulture)).AppendLine(" control");
        }

        sb.AppendLine();
        sb.AppendLine("Fan and control sensors:");
        if (rows.Count == 0)
        {
            sb.AppendLine("  (none)");
        }

        foreach (var row in rows)
        {
            sb.Append("  ").Append(row.HardwareName).Append(" | ").Append(row.Kind).Append(" #").Append(row.Index.ToString(CultureInfo.InvariantCulture))
              .Append(" \"").Append(row.Name).Append("\" | ").Append(row.Value).Append(" | ").AppendLine(row.Pairing);
        }

        sb.AppendLine();
        sb.AppendLine("Other fan or monitoring software running:");
        if (input.Conflicts.Count == 0)
        {
            sb.AppendLine("  none found");
        }

        foreach (var app in input.Conflicts)
        {
            sb.Append("  ").Append(app.Product).Append(" (").Append(app.ProcessName).AppendLine(") - close it while using fan control");
        }

        sb.AppendLine();
        sb.AppendLine("Findings:");
        foreach (var f in findings)
        {
            sb.Append("  - ").Append(f.Title).Append(": ").AppendLine(f.Advice);
        }

        sb.AppendLine();
        sb.AppendLine("Why a fan cannot be controlled:");
        foreach (var reason in WhyNoControl)
        {
            sb.Append("  - ").AppendLine(reason);
        }

        return sb.ToString().TrimEnd() + Environment.NewLine;
    }
}
