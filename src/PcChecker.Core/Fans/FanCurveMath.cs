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
