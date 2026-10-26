using PcChecker.Core.Common;
using PcChecker.Core.Sensors;

namespace PcChecker.App.ViewModels.Sensors;

/// <summary>A temperature alert as shown in the "Recent alerts" list.</summary>
public sealed class AlertRowViewModel
{
    public AlertRowViewModel(TemperatureAlert alert, string? sensorName)
    {
        Alert = alert;
        Severity = alert.Severity;
        TimeText = alert.Timestamp.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture);
        Title = alert.Severity == Severity.Critical ? "Critical temperature" : "High temperature";
        string sensor = string.IsNullOrWhiteSpace(sensorName) ? alert.SensorId : sensorName;
        Message = $"{alert.HardwareName} · {sensor} reached {alert.Value:0.#} °C (limit {alert.Threshold:0.#} °C).";
    }

    public TemperatureAlert Alert { get; }

    public Severity Severity { get; }

    public string TimeText { get; }

    public string Title { get; }

    public string Message { get; }
}
