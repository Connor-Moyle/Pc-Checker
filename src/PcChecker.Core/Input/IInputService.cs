using PcChecker.Core.Common;
using PcChecker.Core.Drivers;
using PcChecker.Core.Hardware;

namespace PcChecker.Core.Input;

/// <summary>Keyboard and mouse settings, profiles and vendor software links.</summary>
public interface IInputService
{
    Task<MouseSettings> GetMouseAsync(CancellationToken cancellationToken);
    Task<OperationResult> ApplyMouseAsync(MouseSettings settings, CancellationToken cancellationToken);
    Task<KeyboardSettings> GetKeyboardAsync(CancellationToken cancellationToken);
    Task<OperationResult> ApplyKeyboardAsync(KeyboardSettings settings, CancellationToken cancellationToken);
    IReadOnlyList<InputProfile> BuiltInProfiles { get; }
    Task<OperationResult> ApplyProfileAsync(InputProfile profile, CancellationToken cancellationToken);
    IReadOnlyList<VendorLink> VendorSoftwareLinks(IEnumerable<InputDeviceInfo> devices);
    OperationResult OpenMouseSettings();
    OperationResult OpenKeyboardSettings();
    OperationResult OpenMouseControlPanel();
}

/// <summary>Measures the mouse polling rate (reports per second). Implemented with Raw Input in the app.</summary>
public interface IMousePollingMeter
{
    Task<int> MeasureAsync(int seconds, IProgress<int>? liveHz, CancellationToken cancellationToken);
}

/// <summary>Meter used when no Raw Input window is available (tests, demo, headless).</summary>
public sealed class NullMousePollingMeter : IMousePollingMeter
{
    public Task<int> MeasureAsync(int seconds, IProgress<int>? liveHz, CancellationToken cancellationToken) => Task.FromResult(0);
}
