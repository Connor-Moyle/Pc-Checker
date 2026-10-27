using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using PcChecker.App.Converters;
using PcChecker.App.Localization;
using PcChecker.Core.Sensors;
using PcChecker.Core.Storage;

namespace PcChecker.App.ViewModels.Sensors;

/// <summary>
/// One sensor row on the Temperatures page. Rows are created once and updated in place every poll so the
/// lists never rebuild while the user scrolls.
/// </summary>
public sealed partial class SensorRowViewModel : ObservableObject
{
    [ObservableProperty]
    private double? _value;

    [ObservableProperty]
    private string _valueText = "—";

    [ObservableProperty]
    private string _minText = "—";

    [ObservableProperty]
    private string _maxText = "—";

    [ObservableProperty]
    private string _averageText = "—";

    [ObservableProperty]
    private Brush _valueBrush = TemperatureToBrushConverter.Unknown;

    [ObservableProperty]
    private IReadOnlyList<double> _history = [];

    [ObservableProperty]
    private double _warnAt = double.PositiveInfinity;

    [ObservableProperty]
    private double _critAt = double.PositiveInfinity;

    public SensorRowViewModel(SensorReading reading)
    {
        Id = reading.Id;
        Name = reading.Name;
        HardwareName = reading.HardwareName;
        Hardware = reading.Hardware;
        Kind = reading.Kind;
        Unit = reading.Unit;
        KindText = SensorMapping.DisplayName(reading.Kind);
        HardwareText = SensorMapping.DisplayName(reading.Hardware);
        if (DriveTemperatureRules.IsExtraDriveSensor(reading))
        {
            Note = Loc.F("Extra sensor on the drive. It runs hotter than the drive, so its limits are {0} °C higher.",
                DriveTemperatureRules.ExtraSensorMarginC);
            Tag = Loc.T("extra sensor");
        }
        else if (DriveTemperatureRules.IsDriveTemperature(reading))
        {
            Note = Loc.T("The drive's own temperature (NVMe composite or SMART). This is the value PC Checker reports for the drive.");
        }
    }

    /// <summary>One-line explanation shown as the row tooltip (drive sensors only), or null.</summary>
    public string? Note { get; }

    /// <summary>Short tag after the name for extra drive sensors, or null.</summary>
    public string? Tag { get; }

    public string Id { get; }

    public string Name { get; }

    public string HardwareName { get; }

    public HardwareKind Hardware { get; }

    public SensorKind Kind { get; }

    public string KindText { get; }

    public string HardwareText { get; }

    public string Unit { get; }

    public bool IsTemperature => Kind == SensorKind.Temperature;

    /// <summary>Applies the latest reading. Thresholds only colour temperature rows.</summary>
    public void Update(SensorReading reading, (double Warn, double Crit)? thresholds)
    {
        Value = reading.Value;
        ValueText = SensorMapping.FormatValue(reading.Value, Unit);
        if (thresholds is { } t)
        {
            WarnAt = t.Warn;
            CritAt = t.Crit;
        }

        ValueBrush = PickBrush(reading.Value, thresholds);
    }

    /// <summary>Applies statistics computed over the history window.</summary>
    public void UpdateStats(SensorMath.Stats? stats, IReadOnlyList<double> history)
    {
        if (stats is null)
        {
            MinText = MaxText = AverageText = "—";
        }
        else
        {
            MinText = SensorMapping.FormatValue(stats.Min, Unit);
            MaxText = SensorMapping.FormatValue(stats.Max, Unit);
            AverageText = SensorMapping.FormatValue(stats.Average, Unit);
        }

        History = history;
    }

    /// <summary>Applies the session min/max carried by the reading (used for non-temperature rows).</summary>
    public void UpdateSessionMinMax(SensorReading reading)
    {
        MinText = SensorMapping.FormatValue(reading.Min, Unit);
        MaxText = SensorMapping.FormatValue(reading.Max, Unit);
    }

    private Brush PickBrush(double? value, (double Warn, double Crit)? thresholds)
    {
        if (!IsTemperature)
        {
            return TemperatureToBrushConverter.Unknown;
        }

        if (value is not { } v || double.IsNaN(v))
        {
            return TemperatureToBrushConverter.Unknown;
        }

        if (thresholds is { } t)
        {
            if (v >= t.Crit)
            {
                return TemperatureToBrushConverter.Hot;
            }

            if (v >= t.Warn)
            {
                return TemperatureToBrushConverter.Warm;
            }
        }

        return v < 40 ? TemperatureToBrushConverter.Cool : TemperatureToBrushConverter.Normal;
    }
}
