namespace PcChecker.Core.Fans;

/// <summary>A running program known to read or drive the same fan controller as PC Checker.</summary>
public sealed record ConflictingApp(string Product, string ProcessName);

/// <summary>
/// Programs that also talk to the motherboard sensor chip, the embedded controller or the fan hubs. Two programs
/// driving the same chip fight over it (speeds jump back, readings turn into garbage), and vendor suites often keep
/// fans under their own control. Matching is on process names only, compared without case, spaces or punctuation:
/// an exact name, or a name starting with a specific prefix. Pure.
/// </summary>
public static class FanConflictCatalog
{
    private sealed record Entry(string Product, string[] Exact, string[] Prefixes);

    private static readonly Entry[] Entries =
    [
        new("ASUS Armoury Crate", ["armourysocketserver", "armourysvc", "lightingservice"], ["armourycrate"]),
        new("ASUS AI Suite / Fan Xpert", ["aisuite3"], ["asusfancontrol", "fanxpert", "aisuite"]),
        new("Corsair iCUE", [], ["icue", "corsairservice", "corsaircpuidservice"]),
        new("NZXT CAM", [], ["nzxtcam"]),
        new("MSI Center / Dragon Center", ["dragoncenter"], ["msicenter", "msicentralserver", "msicentralservice", "msidragoncenter"]),
        new("Gigabyte Control Center / RGB Fusion / SIV", ["siv"], ["rgbfusion", "easytune", "gigabytecontrolcenter"]),
        new("ASRock Polychrome / Phantom Gaming Tuning", ["pgtuning"], ["asrpolychrome", "polychromergb", "phantomgamingtuning", "asrockpolychrome"]),
        new("SignalRGB", [], ["signalrgb"]),
        new("Fan Control (Rem0o)", ["fancontrol"], []),
        new("HWiNFO", [], ["hwinfo"]),
        new("Argus Monitor", [], ["argusmonitor"]),
        new("SpeedFan", ["speedfan"], []),
        new("Libre Hardware Monitor / Open Hardware Monitor", [], ["librehardwaremonitor", "openhardwaremonitor"]),
    ];

    /// <summary>Every product in the catalog, for the advice text.</summary>
    public static IReadOnlyList<string> Products { get; } = Entries.Select(e => e.Product).ToList();

    /// <summary>
    /// The running programs that match, one per product (the first matching process names it), in catalog order.
    /// <paramref name="processNames"/> are process names with or without ".exe".
    /// </summary>
    public static IReadOnlyList<ConflictingApp> Match(IEnumerable<string?> processNames)
    {
        ArgumentNullException.ThrowIfNull(processNames);
        var running = processNames.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => (Raw: StripExe(n!.Trim()), Key: Normalize(n!)))
            .Where(p => p.Key.Length > 0).ToList();
        var result = new List<ConflictingApp>();
        foreach (var entry in Entries)
        {
            var hit = running.FirstOrDefault(p => entry.Exact.Contains(p.Key) || entry.Prefixes.Any(prefix => p.Key.StartsWith(prefix, StringComparison.Ordinal)));
            if (hit.Key is not null)
            {
                result.Add(new ConflictingApp(entry.Product, hit.Raw));
            }
        }

        return result;
    }

    /// <summary>Lower-case letters and digits only, without a trailing ".exe".</summary>
    public static string Normalize(string processName)
    {
        ArgumentNullException.ThrowIfNull(processName);
        var name = StripExe(processName.Trim());
        return new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }

    private static string StripExe(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
}
