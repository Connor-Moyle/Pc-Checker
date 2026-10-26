using PcChecker.Core.Common;

namespace PcChecker.Core.Displays;

/// <summary>Demo displays: a 240 Hz 1440p gaming monitor plus a 60 Hz 1080p side monitor.</summary>
public sealed class DemoDisplayService : IDisplayService
{
    private static readonly DisplayMode[] MainModes =
    [
        new(2560, 1440, 240, 32), new(2560, 1440, 144, 32), new(2560, 1440, 120, 32), new(2560, 1440, 60, 32),
        new(1920, 1080, 240, 32), new(1920, 1080, 60, 32), new(1280, 720, 60, 32),
    ];

    private static readonly DisplayMode[] SideModes = [new(1920, 1080, 60, 32), new(1680, 1050, 60, 32), new(1280, 720, 60, 32)];

    private readonly object _gate = new();
    private readonly List<DisplayInfo> _displays =
    [
        new(@"\\.\DISPLAY1", DemoData.MonitorName, "ASUS", "M9LMQS000001", true, new(2560, 1440, 240, 32), 100, 0, 0, 0, true, false,
            "DisplayPort", 27, DemoData.GpuName, MainModes, true, 70, 50, false),
        new(@"\\.\DISPLAY2", "Dell U2415", "Dell", "7MT0189J0ABS", false, new(1920, 1080, 60, 32), 100, 2560, 180, 0, false, false,
            "HDMI", 24, DemoData.GpuName, SideModes, true, 55, 50, false),
    ];

    public async Task<IReadOnlyList<DisplayInfo>> GetDisplaysAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            return _displays.ToList();
        }
    }

    public Task<OperationResult> SetModeAsync(string deviceName, DisplayMode mode, CancellationToken cancellationToken) =>
        Update(deviceName, d => d with { Current = DisplayModeMath.Closest(d.Modes, mode) ?? d.Current }, $"{deviceName} set to {mode} (demo).", cancellationToken);

    public async Task<OperationResult> SetPrimaryAsync(string deviceName, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            for (int i = 0; i < _displays.Count; i++)
            {
                _displays[i] = _displays[i] with { IsPrimary = string.Equals(_displays[i].DeviceName, deviceName, StringComparison.OrdinalIgnoreCase) };
            }
        }

        return OperationResult.Ok($"{deviceName} is now the main display (demo).");
    }

    public Task<OperationResult> SetOrientationAsync(string deviceName, int degrees, CancellationToken cancellationToken) =>
        Update(deviceName, d => d with { OrientationDegrees = ((degrees % 360) + 360) % 360 }, $"{deviceName} rotated to {degrees}° (demo).", cancellationToken);

    public Task<OperationResult> SetBrightnessAsync(string deviceName, int percent, CancellationToken cancellationToken) =>
        Update(deviceName, d => d with { Brightness = Math.Clamp(percent, 0, 100) }, $"Brightness set to {Math.Clamp(percent, 0, 100)}% (demo).", cancellationToken);

    public Task<OperationResult> SetContrastAsync(string deviceName, int percent, CancellationToken cancellationToken) =>
        Update(deviceName, d => d with { Contrast = Math.Clamp(percent, 0, 100) }, $"Contrast set to {Math.Clamp(percent, 0, 100)}% (demo).", cancellationToken);

    public Task<OperationResult> SetHdrAsync(string deviceName, bool enabled, CancellationToken cancellationToken) =>
        Update(deviceName, d => d.HdrSupported ? d with { HdrEnabled = enabled } : d, $"HDR {(enabled ? "on" : "off")} for {deviceName} (demo).", cancellationToken);

    public async Task<OperationResult> ApplyProfileAsync(DisplayProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        foreach (var (device, mode) in profile.Modes)
        {
            await SetModeAsync(device, mode, cancellationToken).ConfigureAwait(false);
        }

        if (profile.PrimaryDevice is not null)
        {
            await SetPrimaryAsync(profile.PrimaryDevice, cancellationToken).ConfigureAwait(false);
        }

        return OperationResult.Ok($"Profile \"{profile.Name}\" applied (demo).");
    }

    public OperationResult OpenWindowsDisplaySettings() => OperationResult.Ok("Would open Windows display settings (demo).");
    public OperationResult OpenNightLightSettings() => OperationResult.Ok("Would open Night light settings (demo).");
    public OperationResult OpenGpuControlPanel() => OperationResult.Ok("Would open the NVIDIA app (demo).");
    public OperationResult OpenColorCalibration() => OperationResult.Ok("Would open Display Color Calibration (demo).");

    private async Task<OperationResult> Update(string deviceName, Func<DisplayInfo, DisplayInfo> change, string message, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            int i = _displays.FindIndex(d => string.Equals(d.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
            if (i < 0)
            {
                return OperationResult.Fail($"No display named {deviceName}.");
            }

            _displays[i] = change(_displays[i]);
        }

        return OperationResult.Ok(message);
    }
}
