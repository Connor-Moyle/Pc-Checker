using PcChecker.Core.Common;

namespace PcChecker.Core.Displays;

/// <summary>Monitor enumeration and control: modes, primary, orientation, brightness/contrast (DDC/CI), HDR, profiles.</summary>
public interface IDisplayService
{
    Task<IReadOnlyList<DisplayInfo>> GetDisplaysAsync(CancellationToken cancellationToken);
    Task<OperationResult> SetModeAsync(string deviceName, DisplayMode mode, CancellationToken cancellationToken);
    Task<OperationResult> SetPrimaryAsync(string deviceName, CancellationToken cancellationToken);
    Task<OperationResult> SetOrientationAsync(string deviceName, int degrees, CancellationToken cancellationToken);
    Task<OperationResult> SetBrightnessAsync(string deviceName, int percent, CancellationToken cancellationToken);
    Task<OperationResult> SetContrastAsync(string deviceName, int percent, CancellationToken cancellationToken);
    Task<OperationResult> SetHdrAsync(string deviceName, bool enabled, CancellationToken cancellationToken);
    Task<OperationResult> ApplyProfileAsync(DisplayProfile profile, CancellationToken cancellationToken);
    OperationResult OpenWindowsDisplaySettings();
    OperationResult OpenNightLightSettings();
    OperationResult OpenGpuControlPanel();
    OperationResult OpenColorCalibration();
}
