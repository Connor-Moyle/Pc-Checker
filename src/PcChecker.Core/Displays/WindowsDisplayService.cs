// TODO(displays): displays module — EnumDisplayDevices/Settings, QueryDisplayConfig, DDC/CI, ChangeDisplaySettingsEx, HDR.
using PcChecker.Core.Common;

namespace PcChecker.Core.Displays;

/// <summary>Windows implementation of <see cref="IDisplayService"/>.</summary>
public sealed class WindowsDisplayService : IDisplayService
{
    private const string NotImplemented = "Display management is not implemented yet.";

    public Task<IReadOnlyList<DisplayInfo>> GetDisplaysAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DisplayInfo>>([]);
    public Task<OperationResult> SetModeAsync(string deviceName, DisplayMode mode, CancellationToken cancellationToken) => Task.FromResult(OperationResult.Fail(NotImplemented));
    public Task<OperationResult> SetPrimaryAsync(string deviceName, CancellationToken cancellationToken) => Task.FromResult(OperationResult.Fail(NotImplemented));
    public Task<OperationResult> SetOrientationAsync(string deviceName, int degrees, CancellationToken cancellationToken) => Task.FromResult(OperationResult.Fail(NotImplemented));
    public Task<OperationResult> SetBrightnessAsync(string deviceName, int percent, CancellationToken cancellationToken) => Task.FromResult(OperationResult.Fail(NotImplemented));
    public Task<OperationResult> SetContrastAsync(string deviceName, int percent, CancellationToken cancellationToken) => Task.FromResult(OperationResult.Fail(NotImplemented));
    public Task<OperationResult> SetHdrAsync(string deviceName, bool enabled, CancellationToken cancellationToken) => Task.FromResult(OperationResult.Fail(NotImplemented));
    public Task<OperationResult> ApplyProfileAsync(DisplayProfile profile, CancellationToken cancellationToken) => Task.FromResult(OperationResult.Fail(NotImplemented));
    public OperationResult OpenWindowsDisplaySettings() => ShellLauncher.Open("ms-settings:display");
    public OperationResult OpenNightLightSettings() => ShellLauncher.Open("ms-settings:nightlight");
    public OperationResult OpenGpuControlPanel() => OperationResult.Fail(NotImplemented);
    public OperationResult OpenColorCalibration() => ShellLauncher.Open("dccw.exe");
}
