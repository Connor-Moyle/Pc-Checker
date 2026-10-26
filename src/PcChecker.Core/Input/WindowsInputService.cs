using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Drivers;
using PcChecker.Core.Hardware;
using PcChecker.Core.Restore;

namespace PcChecker.Core.Input;

/// <summary>
/// Windows implementation of <see cref="IInputService"/>: reads and writes the mouse and keyboard parameters through
/// <c>SystemParametersInfo</c>, mirrors them into the <c>HKCU\Control Panel</c> keys so they survive sign-out, and runs
/// every change through <see cref="IChangeGuard"/> (journal + log). Restore points are skipped on purpose: these are
/// per-user settings that are instantly reversible by applying the "Windows default" profile.
/// </summary>
public sealed partial class WindowsInputService : IInputService
{
    private const string Module = "Input";
    private const string UndoHint = "Apply the Windows default profile on the Keyboard & Mouse page.";

    private readonly IChangeGuard _guard;
    private readonly ILogger<WindowsInputService> _logger;

    public WindowsInputService(IChangeGuard guard, ILogger<WindowsInputService> logger)
    {
        _guard = guard;
        _logger = logger;
    }

    public IReadOnlyList<InputProfile> BuiltInProfiles => InputProfileCatalog.BuiltIn;

    public Task<MouseSettings> GetMouseAsync(CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return Task.FromResult(new MouseSettings());
        }

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return ReadMouse();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read the current mouse settings; showing Windows defaults.");
                return new MouseSettings();
            }
        }, cancellationToken);
    }

    public Task<KeyboardSettings> GetKeyboardAsync(CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return Task.FromResult(new KeyboardSettings());
        }

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return ReadKeyboard();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read the current keyboard settings; showing Windows defaults.");
                return new KeyboardSettings();
            }
        }, cancellationToken);
    }

    public Task<OperationResult> ApplyMouseAsync(MouseSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!PlatformGuard.IsWindows)
        {
            return Task.FromResult(OperationResult.Fail("Mouse settings can only be changed on Windows."));
        }

        var target = InputMath.Normalize(settings);
        return _guard.RunGuardedAsync("Apply mouse settings", Options("Mouse settings"),
            ct => Task.Run(() => WriteMouse(target), ct), null, cancellationToken);
    }

    public Task<OperationResult> ApplyKeyboardAsync(KeyboardSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!PlatformGuard.IsWindows)
        {
            return Task.FromResult(OperationResult.Fail("Keyboard settings can only be changed on Windows."));
        }

        var target = InputMath.Normalize(settings);
        return _guard.RunGuardedAsync("Apply keyboard settings", Options("Keyboard settings"),
            ct => Task.Run(() => WriteKeyboard(target), ct), null, cancellationToken);
    }

    public Task<OperationResult> ApplyProfileAsync(InputProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!PlatformGuard.IsWindows)
        {
            return Task.FromResult(OperationResult.Fail("Keyboard and mouse profiles can only be applied on Windows."));
        }

        var mouse = InputMath.Normalize(profile.Mouse);
        var keyboard = InputMath.Normalize(profile.Keyboard);
        return _guard.RunGuardedAsync($"Apply keyboard & mouse profile \"{profile.Name}\"", Options($"Profile {profile.Name}"),
            ct => Task.Run(() =>
            {
                var mouseResult = WriteMouse(mouse);
                var keyboardResult = WriteKeyboard(keyboard);
                if (mouseResult.Success && keyboardResult.Success)
                {
                    return OperationResult.Ok($"Profile \"{profile.Name}\" applied.");
                }

                var problems = new List<string>();
                if (!mouseResult.Success)
                {
                    problems.Add(mouseResult.Message);
                }

                if (!keyboardResult.Success)
                {
                    problems.Add(keyboardResult.Message);
                }

                return OperationResult.Fail($"Profile \"{profile.Name}\" was only partly applied: {string.Join(" ", problems)}");
            }, ct), null, cancellationToken);
    }

    public IReadOnlyList<VendorLink> VendorSoftwareLinks(IEnumerable<InputDeviceInfo> devices) => InputProfileCatalog.VendorLinksFor(devices);

    public OperationResult OpenMouseSettings() => ShellLauncher.Open("ms-settings:mousetouchpad");

    public OperationResult OpenKeyboardSettings() => ShellLauncher.Open("ms-settings:typing");

    public OperationResult OpenMouseControlPanel() => ShellLauncher.Open("control.exe", "main.cpl");

    private static ChangeOptions Options(string target) => new(false, false, Module, target, true, UndoHint);
}
