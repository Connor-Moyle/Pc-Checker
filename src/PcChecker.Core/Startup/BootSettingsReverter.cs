using Microsoft.Win32;
using PcChecker.Core.Common;
using PcChecker.Core.Optimization;

namespace PcChecker.Core.Startup;

/// <summary>Remembers boot settings from before an Optimize action changed them, so Revert can put them back.</summary>
public interface IBootSettingsStore
{
    int? Get(string name);

    void Set(string name, int value);

    void Remove(string name);
}

/// <summary>
/// Keeps the remembered values in HKCU\Software\PcChecker\Optimization (next to the remembered power plan) so Revert
/// still works after the app has been restarted. Off Windows the values are only kept in memory.
/// </summary>
public sealed class RegistryBootSettingsStore : IBootSettingsStore
{
    public const string Key = @"Software\PcChecker\Optimization";

    private readonly Dictionary<string, int> _memory = new(StringComparer.OrdinalIgnoreCase);

    public int? Get(string name) =>
        PlatformGuard.IsWindows ? RegistryTweaks.GetDword(RegistryHive.CurrentUser, Key, "Previous" + name)
        : _memory.TryGetValue(name, out int value) ? value : null;

    public void Set(string name, int value)
    {
        if (PlatformGuard.IsWindows)
        {
            RegistryTweaks.SetDword(RegistryHive.CurrentUser, Key, "Previous" + name, value);
        }
        else
        {
            _memory[name] = value;
        }
    }

    public void Remove(string name)
    {
        if (PlatformGuard.IsWindows)
        {
            RegistryTweaks.DeleteValue(RegistryHive.CurrentUser, Key, "Previous" + name);
        }
        else
        {
            _memory.Remove(name);
        }
    }
}

/// <summary>
/// Applies and reverts the boot menu timeout and fast startup for the Optimize page. The values from before the
/// first change are remembered, and Revert restores exactly those instead of fixed defaults: the timeout the user had
/// (not always 30 s), and fast startup only when it was really on. Hibernation is never turned on by a Revert unless
/// it was on before. The changes themselves go through <see cref="IStartupService"/>, which guards and journals them.
/// </summary>
public sealed class BootSettingsReverter
{
    public const string BootTimeoutName = "BootTimeout";
    public const string FastStartupName = "HiberbootEnabled";
    public const string HibernationName = "HibernateEnabled";
    public const int DefaultTimeoutSeconds = 30;

    private readonly IStartupService _startup;
    private readonly IBootSettingsStore _store;

    public BootSettingsReverter(IStartupService startup, IBootSettingsStore store)
    {
        _startup = startup;
        _store = store;
    }

    public async Task<OperationResult> SetBootTimeoutAsync(int seconds, CancellationToken cancellationToken)
    {
        bool remembered = false;
        if (_store.Get(BootTimeoutName) is null && await TryReadAsync(cancellationToken).ConfigureAwait(false) is { } boot)
        {
            _store.Set(BootTimeoutName, boot.TimeoutSeconds);
            remembered = true;
        }

        var result = await _startup.SetBootTimeoutAsync(seconds, cancellationToken).ConfigureAwait(false);
        if (!result.Success && remembered)
        {
            _store.Remove(BootTimeoutName);
        }

        return result;
    }

    public async Task<OperationResult> RevertBootTimeoutAsync(CancellationToken cancellationToken)
    {
        int target = _store.Get(BootTimeoutName) ?? DefaultTimeoutSeconds;
        var result = await _startup.SetBootTimeoutAsync(target, cancellationToken).ConfigureAwait(false);
        if (result.Success)
        {
            _store.Remove(BootTimeoutName);
        }

        return result;
    }

    public async Task<OperationResult> SetFastStartupAsync(bool enabled, CancellationToken cancellationToken)
    {
        bool remembered = false;
        if (_store.Get(FastStartupName) is null && await TryReadAsync(cancellationToken).ConfigureAwait(false) is { } boot)
        {
            _store.Set(FastStartupName, boot.FastStartup ? 1 : 0);
            _store.Set(HibernationName, boot.HibernationEnabled ? 1 : 0);
            remembered = true;
        }

        var result = await _startup.SetFastStartupAsync(enabled, cancellationToken).ConfigureAwait(false);
        if (!result.Success && remembered)
        {
            Forget();
        }

        return result;
    }

    /// <summary>Undoes a fast startup change that turned it on (<paramref name="appliedEnabled"/>) or off.</summary>
    public async Task<OperationResult> RevertFastStartupAsync(bool appliedEnabled, CancellationToken cancellationToken)
    {
        int? wasOn = _store.Get(FastStartupName);
        int? hibernationBefore = _store.Get(HibernationName);
        if (appliedEnabled)
        {
            if (wasOn == 1)
            {
                Forget();
                return OperationResult.Ok("Fast startup was already on before, so it stays on.");
            }

            var off = await _startup.SetFastStartupAsync(false, cancellationToken).ConfigureAwait(false);
            if (!off.Success)
            {
                return off;
            }

            Forget();
            return hibernationBefore == 0
                ? off with { Message = off.Message + " Hibernation, which was turned on for fast startup, is still on; use \"Turn off hibernation\" if you do not need it." }
                : off;
        }

        if (wasOn == 0)
        {
            Forget();
            return OperationResult.Ok("Fast startup was already off before, so it stays off.");
        }

        // Fast startup cannot work without hibernation. If hibernation was off before (only HiberbootEnabled was
        // left at 1), turning fast startup "back on" would create a multi-GB hiberfil.sys the user removed on purpose.
        hibernationBefore ??= await TryReadAsync(cancellationToken).ConfigureAwait(false) is { } boot ? (boot.HibernationEnabled ? 1 : 0) : null;
        if (hibernationBefore == 0)
        {
            Forget();
            return OperationResult.Ok("Fast startup stays off: hibernation was off before, and fast startup does not work without it. Hibernation was left off.");
        }

        var on = await _startup.SetFastStartupAsync(true, cancellationToken).ConfigureAwait(false);
        if (on.Success)
        {
            Forget();
        }

        return on;
    }

    private void Forget()
    {
        _store.Remove(FastStartupName);
        _store.Remove(HibernationName);
    }

    private async Task<BootConfig?> TryReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _startup.GetBootConfigAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }
}
