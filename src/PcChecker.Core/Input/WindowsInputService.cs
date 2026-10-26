// TODO(input): input module — SystemParametersInfo get/set, registry persistence, accessibility structs, IChangeGuard.
using PcChecker.Core.Common;
using PcChecker.Core.Drivers;
using PcChecker.Core.Hardware;

namespace PcChecker.Core.Input;

/// <summary>Windows implementation of <see cref="IInputService"/>.</summary>
public sealed class WindowsInputService : IInputService
{
    private const string NotImplemented = "Keyboard and mouse settings are not implemented yet.";

    public IReadOnlyList<InputProfile> BuiltInProfiles => InputProfileCatalog.BuiltIn;

    public Task<MouseSettings> GetMouseAsync(CancellationToken cancellationToken) => Task.FromResult(new MouseSettings());
    public Task<OperationResult> ApplyMouseAsync(MouseSettings settings, CancellationToken cancellationToken) => Task.FromResult(OperationResult.Fail(NotImplemented));
    public Task<KeyboardSettings> GetKeyboardAsync(CancellationToken cancellationToken) => Task.FromResult(new KeyboardSettings());
    public Task<OperationResult> ApplyKeyboardAsync(KeyboardSettings settings, CancellationToken cancellationToken) => Task.FromResult(OperationResult.Fail(NotImplemented));
    public Task<OperationResult> ApplyProfileAsync(InputProfile profile, CancellationToken cancellationToken) => Task.FromResult(OperationResult.Fail(NotImplemented));
    public IReadOnlyList<VendorLink> VendorSoftwareLinks(IEnumerable<InputDeviceInfo> devices) => InputProfileCatalog.VendorLinksFor(devices);
    public OperationResult OpenMouseSettings() => ShellLauncher.Open("ms-settings:mousetouchpad");
    public OperationResult OpenKeyboardSettings() => ShellLauncher.Open("ms-settings:typing");
    public OperationResult OpenMouseControlPanel() => ShellLauncher.Open("control.exe", "main.cpl");
}
