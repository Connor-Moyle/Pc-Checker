using System.Text.RegularExpressions;
using PcChecker.Core.Sensors;

namespace PcChecker.Core.Fans;

/// <summary>
/// Pure rules that match every fan <em>control</em> sensor (the PWM output) with the RPM sensor measuring the same fan.
/// Hardware monitors expose them as two separate sensors on the same chip. A pair is only made when it is certain; a
/// wrong pair would show one header's speed under another header's slider, so anything unclear is left unpaired: the
/// control is listed as a "control without RPM reading" and the RPM sensor as a read-only fan.
/// </summary>
public static partial class FanPairing
{
    /// <summary>The <see cref="FanPair.Reason"/> of a control no RPM sensor could be matched to with confidence.</summary>
    public const string NoConfidentMatch = "control without RPM reading";

    /// <summary>
    /// Pairs controls with RPM sensors. Every control appears exactly once; an RPM sensor is used at most once.
    /// Pass 1: the same hardware and the same channel index (Super I/O chips and graphics cards number a header's speed
    /// input and its PWM output alike), when that index is unique on both sides. Pass 2: the same hardware and the same
    /// name key ("Fan #3" / "Fan Control #3"), when exactly one unpaired control and one free RPM sensor share it.
    /// Nothing is paired by position or across hardware.
    /// </summary>
    public static IReadOnlyList<FanPair> Pair(IEnumerable<FanSensorInfo> sensors)
    {
        ArgumentNullException.ThrowIfNull(sensors);
        var all = sensors.ToList();
        var controls = all.Where(s => s.Kind == SensorKind.Control)
            .OrderBy(s => s.HardwareId, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Index).ToList();
        var rpms = all.Where(s => s.Kind == SensorKind.Fan).ToList();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<FanPair>(controls.Count);

        var pending = new List<FanSensorInfo>();
        foreach (var control in controls)
        {
            int controlsWithIndex = controls.Count(c => SameHardware(c, control) && c.Index == control.Index);
            var candidates = rpms.Where(r => SameHardware(r, control) && r.Index == control.Index).ToList();
            if (controlsWithIndex == 1 && candidates.Count == 1 && !used.Contains(candidates[0].Id))
            {
                used.Add(candidates[0].Id);
                result.Add(new FanPair(control, candidates[0], "same hardware and index"));
            }
            else
            {
                pending.Add(control);
            }
        }

        foreach (var control in pending)
        {
            var key = NameKey(control.Name);
            FanSensorInfo? match = null;
            if (key is not null)
            {
                int controlsWithKey = pending.Count(c => SameHardware(c, control) && NameKey(c.Name) == key);
                var candidates = rpms.Where(r => !used.Contains(r.Id) && SameHardware(r, control) && NameKey(r.Name) == key).ToList();
                if (controlsWithKey == 1 && candidates.Count == 1)
                {
                    match = candidates[0];
                }
            }

            if (match is not null)
            {
                used.Add(match.Id);
                result.Add(new FanPair(control, match, "same hardware and name"));
            }
            else
            {
                result.Add(new FanPair(control, null, NoConfidentMatch));
            }
        }

        return result.OrderBy(p => p.Control.HardwareId, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Control.Index).ToList();
    }

    /// <summary>RPM sensors that no control was paired with: fans the app can see but not drive.</summary>
    public static IReadOnlyList<FanSensorInfo> Unpaired(IEnumerable<FanSensorInfo> sensors, IReadOnlyList<FanPair> pairs)
    {
        var used = new HashSet<string>(pairs.Where(p => p.Rpm is not null).Select(p => p.Rpm!.Id), StringComparer.OrdinalIgnoreCase);
        return sensors.Where(s => s.Kind == SensorKind.Fan && !used.Contains(s.Id)).ToList();
    }

    /// <summary>
    /// A friendly name for a fan: strips "Control" from PWM sensor names and adds the number so "Fan #1" on a
    /// motherboard becomes "Fan 1 (motherboard header)". Which numbered header is the CPU fan differs from board to
    /// board, so a numbered header is never called "CPU fan" unless the chip itself names it so.
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

        if (control.Hardware is HardwareKind.Motherboard && Regex.IsMatch(raw, @"^Fan \d+$", RegexOptions.IgnoreCase))
        {
            return raw + " (motherboard header)";
        }

        return string.IsNullOrWhiteSpace(raw) ? "Fan " + (control.Index + 1) : raw;
    }

    /// <summary>A friendly name for a read-only RPM sensor (same rules as <see cref="FriendlyName(FanSensorInfo, FanSensorInfo?)"/>).</summary>
    public static string FriendlyName(FanSensorInfo rpm) => FriendlyName(rpm, rpm);

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
