using PcChecker.Core.Sensors;

namespace PcChecker.Core.Fans;

public enum FanMode
{
    Auto,
    Manual,
    Curve,
}

/// <summary>A fan (or pump) the app can see, and whether it can be controlled.</summary>
public sealed record FanDevice(
    string Id,
    string Name,
    HardwareKind Hardware,
    string HardwareName,
    double? Rpm,
    double? Percent,
    bool Controllable,
    double MinPercent,
    double MaxPercent,
    FanMode Mode,
    string? ControlSensorId,
    string? RpmSensorId);

/// <summary>One point on a fan curve: at <paramref name="TemperatureC"/> run the fan at <paramref name="Percent"/>.</summary>
public sealed record FanCurvePoint(double TemperatureC, double Percent);

/// <summary>A temperature → fan speed curve. Mutable so it can be edited and saved.</summary>
public sealed class FanCurve
{
    public string TemperatureSensorId { get; set; } = string.Empty;
    public List<FanCurvePoint> Points { get; set; } = [];

    /// <summary>Degrees the temperature must fall before the fan slows down again.</summary>
    public int HysteresisC { get; set; } = 2;

    /// <summary>Largest change in percent applied per evaluation step.</summary>
    public int RampStepPercent { get; set; } = 5;
}

/// <summary>What a profile does with one fan.</summary>
public sealed class FanAssignment
{
    public FanMode Mode { get; set; } = FanMode.Auto;
    public double? ManualPercent { get; set; }
    public FanCurve? Curve { get; set; }
}

/// <summary>Named set of fan assignments keyed by fan id.</summary>
public sealed class FanProfile
{
    public string Name { get; set; } = string.Empty;
    public Dictionary<string, FanAssignment> Fans { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool IsBuiltIn { get; set; }
}

/// <summary>Fan settings stored in <c>AppSettings.Fans</c>.</summary>
public sealed class FanSettings
{
    public List<FanProfile> Profiles { get; set; } = [];
    public string? ActiveProfileName { get; set; }
    public bool RevertToAutoOnExit { get; set; } = true;
    public double MinimumPercent { get; set; } = 20;
    public bool ApplyOnStartup { get; set; }
}
