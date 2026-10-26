using System.Runtime.InteropServices;
using System.Windows.Interop;
using Microsoft.Extensions.Logging;
using PcChecker.App.Helpers;
using PcChecker.Core.Common;

namespace PcChecker.App.Services;

/// <summary>
/// Global hotkeys via <c>RegisterHotKey</c> on a hidden message-only window, so they keep working while the main
/// window is minimised to the tray and while a game has the foreground. Gesture text is parsed by <see cref="HotkeyGesture"/>.
/// </summary>
public sealed class HotkeyService : IHotkeyService, IDisposable
{
    private readonly ILogger<HotkeyService> _logger;
    private readonly Dictionary<string, Registration> _registrations = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private HwndSource? _source;
    private int _nextId = 0x5000;

    public HotkeyService(ILogger<HotkeyService> logger)
    {
        _logger = logger;
    }

    /// <summary>Why the most recent <see cref="Register"/> call failed, in plain English; null after a success.</summary>
    public string? LastError { get; private set; }

    public bool Register(string id, string hotkeyText, Action callback)
    {
        LastError = null;
        if (string.IsNullOrWhiteSpace(id) || callback is null)
        {
            LastError = "A hotkey needs an id and a callback.";
            return false;
        }

        if (!HotkeyGesture.TryParse(hotkeyText, out var gesture, out var parseError))
        {
            LastError = parseError;
            _logger.LogWarning("Hotkey {Id} rejected: {Error}", id, parseError);
            return false;
        }

        lock (_gate)
        {
            // Re-registering the same id replaces the previous binding.
            UnregisterCore(id);

            var source = EnsureSource();
            if (source is null)
            {
                LastError = "Global hotkeys are not available in this session.";
                return false;
            }

            int nativeId = _nextId++;
            var modifiers = (NativeMethods.HotkeyModifiers)(uint)gesture.Modifiers | NativeMethods.HotkeyModifiers.NoRepeat;
            bool ok;
            int error = 0;
            try
            {
                ok = NativeMethods.RegisterHotKey(source.Handle, nativeId, modifiers, gesture.VirtualKey);
                if (!ok)
                {
                    error = Marshal.GetLastWin32Error();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "RegisterHotKey failed for {Gesture}.", gesture);
                LastError = "Windows refused the hotkey: " + ex.Message;
                return false;
            }

            if (!ok)
            {
                LastError = error == NativeMethods.ErrorHotkeyAlreadyRegistered
                    ? $"{gesture} is already used by another app. Pick a different combination."
                    : $"Windows could not register {gesture} (error {error}).";
                _logger.LogWarning("RegisterHotKey({Gesture}) failed with Win32 error {Error}.", gesture, error);
                return false;
            }

            _registrations[id] = new Registration(nativeId, gesture, callback);
            _logger.LogInformation("Hotkey {Id} registered as {Gesture}.", id, gesture);
            return true;
        }
    }

    public void Unregister(string id)
    {
        lock (_gate)
        {
            UnregisterCore(id);
        }
    }

    public bool IsRegistered(string id)
    {
        lock (_gate)
        {
            return _registrations.ContainsKey(id);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var key in _registrations.Keys.ToList())
            {
                UnregisterCore(key);
            }

            if (_source is not null)
            {
                try
                {
                    _source.RemoveHook(WndProc);
                    _source.Dispose();
                }
                catch
                {
                    // Shutdown must not fail because of the hotkey window.
                }

                _source = null;
            }
        }
    }

    private void UnregisterCore(string id)
    {
        if (!_registrations.Remove(id, out var registration) || _source is null)
        {
            return;
        }

        try
        {
            NativeMethods.UnregisterHotKey(_source.Handle, registration.NativeId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "UnregisterHotKey failed for {Id}.", id);
        }
    }

    private HwndSource? EnsureSource()
    {
        if (_source is not null)
        {
            return _source;
        }

        try
        {
            var parameters = new HwndSourceParameters("PcChecker hotkeys")
            {
                WindowStyle = 0,
                ExtendedWindowStyle = 0,
                Width = 0,
                Height = 0,
                PositionX = 0,
                PositionY = 0,
                ParentWindow = NativeMethods.HwndMessage,
            };
            _source = new HwndSource(parameters);
            _source.AddHook(WndProc);
            return _source;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not create the hotkey message window.");
            return null;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != NativeMethods.WmHotkey)
        {
            return IntPtr.Zero;
        }

        int nativeId = wParam.ToInt32();
        Registration? hit;
        lock (_gate)
        {
            hit = _registrations.Values.FirstOrDefault(r => r.NativeId == nativeId);
        }

        if (hit is null)
        {
            return IntPtr.Zero;
        }

        handled = true;
        try
        {
            hit.Callback();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Hotkey {Gesture} handler failed.", hit.Gesture);
        }

        return IntPtr.Zero;
    }

    private sealed record Registration(int NativeId, HotkeyGesture Gesture, Action Callback);
}
