using System.Text.RegularExpressions;
using PcChecker.Core.Sensors;

namespace PcChecker.Core.Fans;

/// <summary>
/// Pure heuristic that matches every fan <em>control</em> sensor (the PWM output) with the RPM sensor measuring the
/// same fan. Hardware monitors expose them as two separate sensors on the same chip; the index usually lines up,
/// and when it does not the names ("Fan #2" / "Fan Control #2", "GPU Fan 1") give it away.
/// </summary>
public static partial class FanPairing
{
    /// <summary>Pairs controls with RPM sensors. Every control appears exactly once; an RPM sensor is used at most once.</summary>
    public static IReadOnlyList<FanPair> Pair(IEnumerable<FanSensorInfo> sensors)
    {
        ArgumentNullException.ThrowIfNull(sensors);
        var all = sensors.ToList();
        var controls = all.Where(s => s.Kind == SensorKind.Control).OrderBy(s => s.HardwareId).ThenBy(s => s.Index).ToList();
        var rpms = all.Where(s => s.Kind == SensorKind.Fan).ToList();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<FanPair>(controls.Count);

        // Pass 1: same hardware and same index (the common SuperIO and GPU layout).
        var pending = new List<FanSensorInfo>();
        foreach (var control in controls)
        {
            var match = rpms.FirstOrDefault(r => !used.Contains(r.Id) && SameHardware(r, control) && r.Index == control.Index);
            if (match is not null)
            {
                used.Add(match.Id);
                result.Add(new FanPair(control, match, "same hardware and index"));
            }
            else
            {
                pending.Add(control);
            }
        }

        // Pass 2: same hardware and the same number or trailing word in the name ("Fan #3" ↔ "Fan Control #3").
        var stillPending = new List<FanSensorInfo>();
        foreach (var control in pending)
        {
            var key = NameKey(control.Name);
            var match = rpms.FirstOrDefault(r => !used.Contains(r.Id) && SameHardware(r, control) && key is not null && NameKey(r.Name) == key);
            if (match is not null)
            {
                used.Add(match.Id);
                result.Add(new FanPair(control, match, "same hardware and name"));
            }
            else
            {
                stillPending.Add(control);
            }
        }

        // Pass 3: same hardware, in order of appearance (first free control ↔ first free RPM sensor).
        foreach (var group in stillPending.GroupBy(c => c.HardwareId, StringComparer.OrdinalIgnoreCase))
        {
            var candidates = rpms.Where(r => !used.Contains(r.Id) && string.Equals(r.HardwareId, group.Key, StringComparison.OrdinalIgnoreCase))
                .OrderBy(r => r.Index).ToList();
            int next = 0;
            foreach (var control in group.OrderBy(c => c.Index))
            {
                if (next < candidates.Count)
                {
                    var match = candidates[next++];
                    used.Add(match.Id);
                    result.Add(new FanPair(control, match, "same hardware, by order"));
                }
                else
                {
                    result.Add(new FanPair(control, null, "no matching RPM sensor"));
                }
            }
        }

        return result.OrderBy(p => p.Control.HardwareId).ThenBy(p => p.Control.Index).ToList();
    }

    /// <summary>RPM sensors that no control was paired with: fans the app can see but not drive.</summary>
    public static IReadOnlyList<FanSensorInfo> Unpaired(IEnumerable<FanSensorInfo> sensors, IReadOnlyList<FanPair> pairs)
    {
        var used = new HashSet<string>(pairs.Where(p => p.Rpm is not null).Select(p => p.Rpm!.Id), StringComparer.OrdinalIgnoreCase);
        return sensors.Where(s => s.Kind == SensorKind.Fan && !used.Contains(s.Id)).ToList();
    }

    /// <summary>
    /// A friendly name for a fan: strips "Control" from PWM sensor names and adds the number so "Fan #1" on a
    /// motherboard becomes "Fan 1 (motherboard)" and the first motherboard header is called "CPU fan".
    /// </summary>
    public static string FriendlyName(FanSensorInfo control, FanSensorInfo? rpm)
    {
        string raw = (rpm?.Name ?? control.Name).Trim();
        raw = ControlWordRegex().Replace(raw, string.Empty).Trim();
        raw = HashRegex().Replace(raw, " $1").Trim();
        raw = Regex.Replace(raw, @"\s{2,}", " ");

        if (control.Hardware is HardwareKind.Gpu)
        {
            return raw.Contains("fan", StringComparison.OrdinalIgnoreCase) ? "GPU " + LowerFirst(raw.Replace("GPU ", string.Empty, StringComparison.OrdinalIgnoreCase)) : "GPU fan " + (control.Index + 1);
        }

        if (control.Hardware is HardwareKind.Motherboard && raw.Equals("Fan 1", StringComparison.OrdinalIgnoreCase))
        {
            return "CPU fan (header 1)";
        }

        if (control.Hardware is HardwareKind.Motherboard && Regex.IsMatch(raw, @"^Fan \d+$", RegexOptions.IgnoreCase))
        {
            return raw + " (motherboard header)";
        }

        return string.IsNullOrWhiteSpace(raw) ? "Fan " + (control.Index + 1) : raw;
    }

    private static bool SameHardware(FanSensorInfo a, FanSensorInfo b) =>
        string.Equals(a.HardwareId, b.HardwareId, StringComparison.OrdinalIgnoreCase);

    /// <summary>The distinguishing part of a sensor name: its number if it has one, otherwise the lower-cased name without "control".</summary>
    internal static string? NameKey(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var m = NumberRegex().Match(name);
        if (m.Success)
        {
            return m.Groups[1].Value;
        }

        var cleaned = ControlWordRegex().Replace(name, string.Empty).Trim().ToLowerInvariant();
        return cleaned.Length == 0 ? null : cleaned;
    }

    private static string LowerFirst(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];

    [GeneratedRegex(@"\bcontrol\b", RegexOptions.IgnoreCase)]
    private static partial Regex ControlWordRegex();

    [GeneratedRegex(@"#\s*(\d+)")]
    private static partial Regex HashRegex();

    [GeneratedRegex(@"(\d+)\s*$")]
    private static partial Regex NumberRegex();
}
