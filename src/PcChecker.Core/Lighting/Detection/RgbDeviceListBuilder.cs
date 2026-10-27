namespace PcChecker.Core.Lighting;

/// <summary>
/// Builds the Lighting page's one combined list: every device OpenRGB controls, plus every piece of RGB hardware the
/// detection pass found that OpenRGB did not list, each with a plain status and one line saying why and what to do.
/// Pure, so the wording and the matching are unit-tested.
/// </summary>
public static class RgbDeviceListBuilder
{
    public const string StatusReady = "Ready";
    public const string StatusNeedsSize = "Ready once a zone size is set";
    public const string StatusUnreadable = "Listed by OpenRGB, details unreadable";
    public const string StatusNeedsOpenRgb = "Found, control needs OpenRGB";
    public const string StatusPossible = "May have lighting, check with OpenRGB";

    /// <summary>"Found, but Corsair iCUE is using it".</summary>
    public static string StatusBlocked(string product) => $"Found, but {product} is using it";

    public static IReadOnlyList<RgbDeviceEntry> Build(IReadOnlyList<RgbDevice> openRgbDevices, RgbScanResult scan, OpenRgbAvailability availability)
    {
        ArgumentNullException.ThrowIfNull(openRgbDevices);
        ArgumentNullException.ThrowIfNull(scan);
        var list = new List<RgbDeviceEntry>();
        foreach (var d in openRgbDevices)
        {
            list.Add(FromOpenRgb(d));
        }

        foreach (var finding in scan.Findings)
        {
            if (IsListedByOpenRgb(finding, openRgbDevices))
            {
                continue;
            }

            list.Add(FromFinding(finding, scan, availability));
        }

        return list;
    }

    /// <summary>"5 RGB devices: 3 ready to control, 2 need OpenRGB." (or "No RGB devices found yet.")</summary>
    public static string Summary(IReadOnlyCollection<RgbDeviceEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            return "No RGB devices found yet.";
        }

        int ready = entries.Count(e => e.IsControllable);
        int blocked = entries.Count(e => e.State == RgbDeviceState.Blocked);
        int needOpenRgb = entries.Count(e => e.State == RgbDeviceState.NeedsOpenRgb);
        int unreadable = entries.Count(e => e.State == RgbDeviceState.Unreadable);
        var parts = new List<string> { $"{ready} ready to control" };
        if (needOpenRgb > 0)
        {
            parts.Add($"{needOpenRgb} need{(needOpenRgb == 1 ? "s" : string.Empty)} OpenRGB");
        }

        if (unreadable > 0)
        {
            parts.Add($"{unreadable} could not be read");
        }

        if (blocked > 0)
        {
            parts.Add($"{blocked} held by another app");
        }

        return $"{entries.Count} RGB device{(entries.Count == 1 ? string.Empty : "s")}: {string.Join(", ", parts)}.";
    }

    /// <summary>True when OpenRGB already lists the hardware the detection pass found (so it is not shown twice).</summary>
    public static bool IsListedByOpenRgb(RgbHardwareFinding finding, IReadOnlyList<RgbDevice> openRgbDevices)
    {
        ArgumentNullException.ThrowIfNull(finding);
        foreach (var d in openRgbDevices)
        {
            if (finding.VendorId is { } vid && finding.ProductId is { } pid && finding.Source is RgbFindingSource.Usb or RgbFindingSource.DynamicLighting
                && RgbDetectionRules.TryParseUsbIds(d.Location, out var v, out var p) && v == vid && p == pid)
            {
                return true;
            }

            if (string.Equals(d.Name, finding.Name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return finding.Source switch
        {
            RgbFindingSource.GraphicsCard => openRgbDevices.Any(d => d.Type == OpenRgbDeviceTypes.Gpu),
            RgbFindingSource.Memory => openRgbDevices.Any(d => d.Type == OpenRgbDeviceTypes.Ram),
            RgbFindingSource.Motherboard => openRgbDevices.Any(d => d.Type == OpenRgbDeviceTypes.Motherboard),
            _ => false,
        };
    }

    public static RgbDeviceEntry FromOpenRgb(RgbDevice d)
    {
        ArgumentNullException.ThrowIfNull(d);
        string key = "openrgb:" + d.Index;
        if (d.IsReadOnly)
        {
            return new RgbDeviceEntry(key, d.Name, d.Vendor, d.Type, RgbDeviceState.Unreadable, StatusUnreadable,
                d.Problem + " Updating OpenRGB usually fixes this.", "OpenRGB", d.Index, RgbFindingConfidence.Certain);
        }

        var unsized = d.Zones.Where(z => z.NeedsSize).Select(z => z.Name).ToList();
        if (unsized.Count > 0)
        {
            string zones = unsized.Count == 1 ? $"\"{unsized[0]}\" has" : $"{unsized.Count} zones have";
            return new RgbDeviceEntry(key, d.Name, d.Vendor, d.Type, RgbDeviceState.NeedsSetup, StatusNeedsSize,
                $"{zones} no LEDs set yet. Addressable headers start at 0: enter how many LEDs are connected under Zones in its card.",
                "OpenRGB", d.Index, RgbFindingConfidence.Certain);
        }

        return new RgbDeviceEntry(key, d.Name, d.Vendor, d.Type, RgbDeviceState.Controllable, StatusReady,
            "Controlled through OpenRGB. Set its colour and effect in its card below.", "OpenRGB", d.Index, RgbFindingConfidence.Certain);
    }

    public static RgbDeviceEntry FromFinding(RgbHardwareFinding f, RgbScanResult scan, OpenRgbAvailability availability)
    {
        ArgumentNullException.ThrowIfNull(f);
        ArgumentNullException.ThrowIfNull(scan);
        string key = "found:" + f.Key;
        if (RgbConflictRules.ConflictFor(f.Vendor, scan.Conflicts) is { } conflict && f.Source != RgbFindingSource.Motherboard)
        {
            return new RgbDeviceEntry(key, f.Name, f.Vendor, f.Type, RgbDeviceState.Blocked, StatusBlocked(conflict.App.Product),
                $"{conflict.App.Why} Close it for this session (below) and press Rescan; control then needs OpenRGB.",
                f.Detail, null, f.Confidence);
        }

        string status = f.Confidence == RgbFindingConfidence.Possible ? StatusPossible : StatusNeedsOpenRgb;
        return new RgbDeviceEntry(key, f.Name, f.Vendor, f.Type, RgbDeviceState.NeedsOpenRgb, status, Reason(f, scan, availability), f.Detail, null, f.Confidence);
    }

    /// <summary>One line: why this device is not controllable yet and the next step.</summary>
    public static string Reason(RgbHardwareFinding f, RgbScanResult scan, OpenRgbAvailability availability)
    {
        ArgumentNullException.ThrowIfNull(f);
        ArgumentNullException.ThrowIfNull(scan);
        string why = f.Source switch
        {
            RgbFindingSource.Memory => "Memory lighting sits on the SMBus, which only OpenRGB can reach.",
            RgbFindingSource.GraphicsCard => "Graphics card lighting sits on the card's own I2C bus, which only OpenRGB can reach.",
            RgbFindingSource.Motherboard => $"Many {f.Vendor} boards have lighting on the SMBus or a Super I/O chip, which PC Checker cannot see directly.",
            RgbFindingSource.DynamicLighting => "A Windows Dynamic Lighting device (Settings > Personalization > Dynamic Lighting can also set it).",
            _ => f.Confidence == RgbFindingConfidence.Likely
                ? "Its USB ID matches an RGB controller OpenRGB supports (the chip maker's ID is shared, so this is likely, not certain)."
                : "Its USB ID matches an RGB controller OpenRGB supports.",
        };

        bool needsSmbus = f.Source is RgbFindingSource.Memory or RgbFindingSource.Motherboard;
        string next = availability switch
        {
            OpenRgbAvailability.NotInstalled => "Install OpenRGB to control it.",
            OpenRgbAvailability.InstalledNotRunning => "OpenRGB is installed but not running: press Start OpenRGB.",
            OpenRgbAvailability.RunningWithoutServer => "OpenRGB is open, but its SDK server is off: press Start Server in its SDK Server tab.",
            OpenRgbAvailability.Unreachable => "PC Checker cannot reach OpenRGB's SDK server: check the address under Connection.",
            _ when needsSmbus && scan.SmbusDriverReady == false =>
                "OpenRGB is running but did not list it: it needs the PawnIO driver for the SMBus. Install PawnIO, then press Rescan Devices in OpenRGB.",
            _ when needsSmbus && !scan.IsAdministrator =>
                "OpenRGB is running but did not list it: run OpenRGB as administrator so it can reach the SMBus.",
            _ => "OpenRGB is running but did not list it. Press Rescan Devices in OpenRGB; if it stays missing, OpenRGB may not support this model yet.",
        };

        return why + " " + next;
    }
}
