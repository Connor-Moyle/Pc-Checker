namespace PcChecker.Core.Fans;

/// <summary>Pure fan-curve logic: interpolation, hysteresis, ramp limiting and the built-in presets.</summary>
public static class FanCurveMath
{
    public const string PresetSilent = "Silent";
    public const string PresetBalanced = "Balanced";
    public const string PresetPerformance = "Performance";

    public static IReadOnlyList<string> Presets { get; } = [PresetSilent, PresetBalanced, PresetPerformance];

    /// <summary>
    /// Evaluates the curve at <paramref name="tempC"/>. When <paramref name="lastPercent"/> is given, the result
    /// only drops after the temperature falls by the hysteresis and moves at most <c>RampStepPercent</c> per call.
    /// </summary>
    public static double Evaluate(FanCurve curve, double tempC, double? lastPercent)
    {
        ArgumentNullException.ThrowIfNull(curve);
        double target = Interpolate(curve.Points, tempC);

        if (lastPercent is not { } last)
        {
            return Math.Clamp(target, 0, 100);
        }

        // Hysteresis: do not slow down unless the curve at (temp + hysteresis) is also below the last value.
        if (target < last && curve.HysteresisC > 0)
        {
            double withHysteresis = Interpolate(curve.Points, tempC + curve.HysteresisC);
            if (withHysteresis >= last)
            {
                return Math.Clamp(last, 0, 100);
            }

            target = withHysteresis;
        }

        // Ramp limiting.
        int step = Math.Max(1, curve.RampStepPercent);
        double delta = Math.Clamp(target - last, -step, step);
        return Math.Clamp(last + delta, 0, 100);
    }

    /// <summary>Linear interpolation between sorted points; clamps to the first/last point outside the range.</summary>
    public static double Interpolate(IReadOnlyList<FanCurvePoint> points, double tempC)
    {
        if (points is null || points.Count == 0)
        {
            return 50;
        }

        var sorted = points.OrderBy(p => p.TemperatureC).ToList();
        if (tempC <= sorted[0].TemperatureC)
        {
            return Math.Clamp(sorted[0].Percent, 0, 100);
        }

        var lastPoint = sorted[^1];
        if (tempC >= lastPoint.TemperatureC)
        {
            return Math.Clamp(lastPoint.Percent, 0, 100);
        }

        for (int i = 1; i < sorted.Count; i++)
        {
            var a = sorted[i - 1];
            var b = sorted[i];
            if (tempC <= b.TemperatureC)
            {
                double span = b.TemperatureC - a.TemperatureC;
                double t = span <= 0 ? 1 : (tempC - a.TemperatureC) / span;
                return Math.Clamp(a.Percent + (b.Percent - a.Percent) * t, 0, 100);
            }
        }

        return Math.Clamp(lastPoint.Percent, 0, 100);
    }

    /// <summary>A preset curve (Silent, Balanced or Performance) bound to a temperature sensor.</summary>
    public static FanCurve Default(string sensorId, string preset)
    {
        List<FanCurvePoint> points = (preset ?? PresetBalanced).Trim().ToLowerInvariant() switch
        {
            "silent" => [new(30, 25), new(40, 30), new(55, 40), new(70, 60), new(85, 100)],
            "performance" => [new(30, 40), new(40, 50), new(55, 70), new(65, 90), new(70, 100)],
            _ => [new(30, 30), new(40, 35), new(55, 50), new(70, 75), new(80, 100)],
        };

        return new FanCurve { TemperatureSensorId = sensorId ?? string.Empty, Points = points };
    }

    /// <summary>
    /// Returns a cleaned copy of the points: clamped to 0–100 on both axes, sorted by temperature, duplicate
    /// temperatures collapsed (last wins) and padded to at least two points so a curve is always drawable.
    /// </summary>
    public static List<FanCurvePoint> Normalize(IEnumerable<FanCurvePoint>? points)
    {
        var map = new SortedDictionary<double, double>();
        foreach (var p in points ?? [])
        {
            double t = Math.Round(Math.Clamp(p.TemperatureC, 0, 100), 1);
            map[t] = Math.Round(Math.Clamp(p.Percent, 0, 100), 1);
        }

        if (map.Count == 0)
        {
            return [new(30, 30), new(80, 100)];
        }

        if (map.Count == 1)
        {
            var only = map.First();
            map[only.Key < 100 ? 100 : 0] = only.Key < 100 ? 100 : only.Value;
        }

        return map.Select(kv => new FanCurvePoint(kv.Key, kv.Value)).ToList();
    }

    /// <summary>Plain-English problem with a curve, or null when it is usable.</summary>
    public static string? Validate(FanCurve? curve)
    {
        if (curve is null)
        {
            return "No curve was given.";
        }

        if (string.IsNullOrWhiteSpace(curve.TemperatureSensorId))
        {
            return "Choose which temperature the curve should follow.";
        }

        if (curve.Points is null || curve.Points.Count < 2)
        {
            return "A curve needs at least two points.";
        }

        if (curve.Points.Any(p => p.TemperatureC < 0 || p.TemperatureC > 100 || p.Percent < 0 || p.Percent > 100))
        {
            return "Curve points must stay between 0–100 °C and 0–100 %.";
        }

        if (curve.HysteresisC < 0 || curve.HysteresisC > 20)
        {
            return "Hysteresis must be between 0 and 20 °C.";
        }

        if (curve.RampStepPercent < 1 || curve.RampStepPercent > 100)
        {
            return "Ramp step must be between 1 and 100 % per second.";
        }

        return null;
    }

    /// <summary>Deep copy so editing a profile never changes a built-in or saved one in place.</summary>
    public static FanCurve Clone(FanCurve curve)
    {
        ArgumentNullException.ThrowIfNull(curve);
        return new FanCurve
        {
            TemperatureSensorId = curve.TemperatureSensorId,
            Points = curve.Points.Select(p => new FanCurvePoint(p.TemperatureC, p.Percent)).ToList(),
            HysteresisC = curve.HysteresisC,
            RampStepPercent = curve.RampStepPercent,
        };
    }

    /// <summary>Deep copy of a profile (see <see cref="Clone(FanCurve)"/>).</summary>
    public static FanProfile Clone(FanProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var copy = new FanProfile { Name = profile.Name, IsBuiltIn = profile.IsBuiltIn };
        foreach (var (id, a) in profile.Fans)
        {
            copy.Fans[id] = new FanAssignment { Mode = a.Mode, ManualPercent = a.ManualPercent, Curve = a.Curve is null ? null : Clone(a.Curve) };
        }

        return copy;
    }

    /// <summary>One line describing a preset for tooltips ("Silent: 25 % at 30 °C rising to 100 % at 85 °C").</summary>
    public static string DescribePreset(string preset)
    {
        var curve = Default(string.Empty, preset);
        var first = curve.Points[0];
        var last = curve.Points[^1];
        return $"{preset}: {first.Percent:0} % at {first.TemperatureC:0} °C rising to {last.Percent:0} % at {last.TemperatureC:0} °C";
    }

    /// <summary>The three built-in profiles, using the given CPU and GPU temperature sensors.</summary>
    public static IReadOnlyList<FanProfile> BuiltInProfiles(string cpuSensorId, string gpuSensorId,
        IEnumerable<string>? cpuFanIds = null, IEnumerable<string>? gpuFanIds = null)
    {
        var cpuFans = (cpuFanIds ?? ["fan.cpu"]).ToList();
        var gpuFans = (gpuFanIds ?? ["fan.gpu"]).ToList();
        var list = new List<FanProfile>();
        foreach (var preset in Presets)
        {
            var profile = new FanProfile { Name = preset, IsBuiltIn = true };
            foreach (var id in cpuFans)
            {
                profile.Fans[id] = new FanAssignment { Mode = FanMode.Curve, Curve = Default(cpuSensorId, preset) };
            }

            foreach (var id in gpuFans)
            {
                profile.Fans[id] = new FanAssignment { Mode = FanMode.Curve, Curve = Default(gpuSensorId, preset) };
            }

            list.Add(profile);
        }

        return list;
    }
}
