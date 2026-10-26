using PcChecker.Core.Common;
using PcChecker.Core.Drivers;
using PcChecker.Core.Hardware;

namespace PcChecker.Core.Input;

/// <summary>Demo input service: remembers applied settings in memory.</summary>
public sealed class DemoInputService : IInputService
{
    private MouseSettings _mouse = new() { Speed = 10, EnhancePointerPrecision = true };
    private KeyboardSettings _keyboard = new();

    public IReadOnlyList<InputProfile> BuiltInProfiles => InputProfileCatalog.BuiltIn;

    public async Task<MouseSettings> GetMouseAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 50, 250).ConfigureAwait(false);
        return _mouse.Clone();
    }

    public async Task<OperationResult> ApplyMouseAsync(MouseSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        _mouse = settings.Clone();
        return OperationResult.Ok("Mouse settings applied (demo).");
    }

    public async Task<KeyboardSettings> GetKeyboardAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 50, 250).ConfigureAwait(false);
        return _keyboard.Clone();
    }

    public async Task<OperationResult> ApplyKeyboardAsync(KeyboardSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        _keyboard = settings.Clone();
        return OperationResult.Ok("Keyboard settings applied (demo).");
    }

    public async Task<OperationResult> ApplyProfileAsync(InputProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        _mouse = profile.Mouse.Clone();
        _keyboard = profile.Keyboard.Clone();
        return OperationResult.Ok($"Profile \"{profile.Name}\" applied (demo).");
    }

    public IReadOnlyList<VendorLink> VendorSoftwareLinks(IEnumerable<InputDeviceInfo> devices) => InputProfileCatalog.VendorLinksFor(devices);
    public OperationResult OpenMouseSettings() => OperationResult.Ok("Would open Mouse settings (demo).");
    public OperationResult OpenKeyboardSettings() => OperationResult.Ok("Would open Typing settings (demo).");
    public OperationResult OpenMouseControlPanel() => OperationResult.Ok("Would open the Mouse control panel (demo).");
}
