using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using PcChecker.Core.Common;
using static PcChecker.Core.Input.InputNativeMethods;

namespace PcChecker.Core.Input;

/// <summary>The SystemParametersInfo / registry half of <see cref="WindowsInputService"/>.</summary>
public sealed partial class WindowsInputService
{
    private const string MouseKey = @"Control Panel\Mouse";
    private const string DesktopKey = @"Control Panel\Desktop";
    private const string KeyboardKey = @"Control Panel\Keyboard";

    private MouseSettings ReadMouse()
    {
        var s = new MouseSettings();
        var warnings = new List<string>();

        int speed = 0;
        if (SystemParametersInfo(SpiGetMouseSpeed, 0, ref speed, 0) && speed is >= InputMath.MinSpeed and <= InputMath.MaxSpeed)
        {
            s.Speed = speed;
        }
        else
        {
            warnings.Add("pointer speed");
        }

        var triplet = new int[3];
        if (SystemParametersInfo(SpiGetMouse, 0, triplet, 0))
        {
            s.EnhancePointerPrecision = InputMath.PrecisionFromThresholds(triplet);
        }
        else
        {
            warnings.Add("pointer precision");
        }

        uint dblClick = GetDoubleClickTime();
        s.DoubleClickMs = dblClick is >= InputMath.MinDoubleClickMs and <= InputMath.MaxDoubleClickMs ? (int)dblClick : 500;
        s.SwapButtons = GetSystemMetrics(SmSwapButton) != 0;

        uint lines = 3;
        if (SystemParametersInfo(SpiGetWheelScrollLines, 0, ref lines, 0))
        {
            s.ScrollOneScreen = lines == WheelPageScroll;
            s.ScrollLines = s.ScrollOneScreen ? 3 : (int)Math.Clamp(lines, 1, 100);
        }
        else
        {
            warnings.Add("wheel scroll lines");
        }

        int trails = 0;
        if (SystemParametersInfo(SpiGetMouseTrails, 0, ref trails, 0))
        {
            s.PointerTrails = trails < 2 ? 0 : Math.Min(trails, 7);
        }

        int vanish = 0;
        if (SystemParametersInfo(SpiGetMouseVanish, 0, ref vanish, 0))
        {
            s.HidePointerWhileTyping = vanish != 0;
        }

        int snap = 0;
        if (SystemParametersInfo(SpiGetSnapToDefButton, 0, ref snap, 0))
        {
            s.SnapToDefaultButton = snap != 0;
        }

        if (warnings.Count > 0)
        {
            _logger.LogWarning("Some mouse settings could not be read and show defaults: {Items}.", string.Join(", ", warnings));
        }

        return s;
    }

    private KeyboardSettings ReadKeyboard()
    {
        var s = new KeyboardSettings();
        int delay = 1;
        if (SystemParametersInfo(SpiGetKeyboardDelay, 0, ref delay, 0))
        {
            s.RepeatDelay = Math.Clamp(delay, 0, InputMath.MaxRepeatDelay);
        }

        int rate = 31;
        if (SystemParametersInfo(SpiGetKeyboardSpeed, 0, ref rate, 0))
        {
            s.RepeatRate = Math.Clamp(rate, 0, InputMath.MaxRepeatRate);
        }

        uint blink = GetCaretBlinkTime();
        s.CursorBlinkMs = blink == uint.MaxValue || blink == 0 ? -1 : (int)blink;

        var sticky = new StickyKeys { cbSize = (uint)Marshal.SizeOf<StickyKeys>() };
        if (SystemParametersInfo(SpiGetStickyKeys, sticky.cbSize, ref sticky, 0))
        {
            s.StickyKeys = (sticky.dwFlags & SkfStickyKeysOn) != 0;
        }

        var filter = new FilterKeys { cbSize = (uint)Marshal.SizeOf<FilterKeys>() };
        if (SystemParametersInfo(SpiGetFilterKeys, filter.cbSize, ref filter, 0))
        {
            s.FilterKeys = (filter.dwFlags & FkfFilterKeysOn) != 0;
        }

        var toggle = new ToggleKeys { cbSize = (uint)Marshal.SizeOf<ToggleKeys>() };
        if (SystemParametersInfo(SpiGetToggleKeys, toggle.cbSize, ref toggle, 0))
        {
            s.ToggleKeys = (toggle.dwFlags & TkfToggleKeysOn) != 0;
        }

        return s;
    }

    private OperationResult WriteMouse(MouseSettings s)
    {
        var failures = new List<string>();

        Check(SystemParametersInfo(SpiSetMouseSpeed, 0, (IntPtr)s.Speed, SpifPersist), "pointer speed", failures);
        Check(SystemParametersInfo(SpiSetMouse, 0, InputMath.MouseThresholds(s.EnhancePointerPrecision), SpifPersist), "pointer precision", failures);
        Check(SystemParametersInfo(SpiSetDoubleClickTime, (uint)s.DoubleClickMs, IntPtr.Zero, SpifPersist), "double-click speed", failures);
        SwapMouseButton(s.SwapButtons);
        uint lines = s.ScrollOneScreen ? WheelPageScroll : (uint)s.ScrollLines;
        Check(SystemParametersInfo(SpiSetWheelScrollLines, lines, IntPtr.Zero, SpifPersist), "wheel scrolling", failures);
        Check(SystemParametersInfo(SpiSetMouseTrails, (uint)(s.PointerTrails < 2 ? 0 : s.PointerTrails), IntPtr.Zero, SpifPersist), "pointer trails", failures);
        Check(SystemParametersInfo(SpiSetMouseVanish, 0, (IntPtr)(s.HidePointerWhileTyping ? 1 : 0), SpifPersist), "hide pointer while typing", failures);
        Check(SystemParametersInfo(SpiSetSnapToDefButton, (uint)(s.SnapToDefaultButton ? 1 : 0), IntPtr.Zero, SpifPersist), "snap to default button", failures);

        PersistMouseRegistry(s);

        if (failures.Count > 0)
        {
            return OperationResult.Fail("Windows rejected some mouse settings: " + string.Join("; ", failures));
        }

        return OperationResult.Ok("Mouse settings applied.");
    }

    private OperationResult WriteKeyboard(KeyboardSettings s)
    {
        var failures = new List<string>();

        Check(SystemParametersInfo(SpiSetKeyboardDelay, (uint)s.RepeatDelay, IntPtr.Zero, SpifPersist), "repeat delay", failures);
        Check(SystemParametersInfo(SpiSetKeyboardSpeed, (uint)s.RepeatRate, IntPtr.Zero, SpifPersist), "repeat rate", failures);
        Check(SetCaretBlinkTime(s.CursorBlinkMs <= 0 ? uint.MaxValue : (uint)s.CursorBlinkMs), "cursor blink rate", failures);

        var sticky = new StickyKeys { cbSize = (uint)Marshal.SizeOf<StickyKeys>() };
        if (SystemParametersInfo(SpiGetStickyKeys, sticky.cbSize, ref sticky, 0))
        {
            sticky.dwFlags = s.StickyKeys ? sticky.dwFlags | SkfStickyKeysOn : sticky.dwFlags & ~SkfStickyKeysOn;
            Check(SystemParametersInfo(SpiSetStickyKeys, sticky.cbSize, ref sticky, SpifPersist), "Sticky Keys", failures);
        }

        var filter = new FilterKeys { cbSize = (uint)Marshal.SizeOf<FilterKeys>() };
        if (SystemParametersInfo(SpiGetFilterKeys, filter.cbSize, ref filter, 0))
        {
            filter.dwFlags = s.FilterKeys ? filter.dwFlags | FkfFilterKeysOn : filter.dwFlags & ~FkfFilterKeysOn;
            Check(SystemParametersInfo(SpiSetFilterKeys, filter.cbSize, ref filter, SpifPersist), "Filter Keys", failures);
        }

        var toggle = new ToggleKeys { cbSize = (uint)Marshal.SizeOf<ToggleKeys>() };
        if (SystemParametersInfo(SpiGetToggleKeys, toggle.cbSize, ref toggle, 0))
        {
            toggle.dwFlags = s.ToggleKeys ? toggle.dwFlags | TkfToggleKeysOn : toggle.dwFlags & ~TkfToggleKeysOn;
            Check(SystemParametersInfo(SpiSetToggleKeys, toggle.cbSize, ref toggle, SpifPersist), "Toggle Keys", failures);
        }

        PersistKeyboardRegistry(s);

        if (failures.Count > 0)
        {
            return OperationResult.Fail("Windows rejected some keyboard settings: " + string.Join("; ", failures));
        }

        return OperationResult.Ok("Keyboard settings applied.");
    }

    /// <summary>Mirrors the values into HKCU so they are re-applied at the next sign-in exactly as set here.</summary>
    private void PersistMouseRegistry(MouseSettings s)
    {
        try
        {
            using var mouse = Registry.CurrentUser.CreateSubKey(MouseKey, writable: true);
            if (mouse is not null)
            {
                var thresholds = InputMath.MouseThresholds(s.EnhancePointerPrecision);
                mouse.SetValue("MouseSensitivity", Text(s.Speed), RegistryValueKind.String);
                mouse.SetValue("MouseThreshold1", Text(thresholds[0]), RegistryValueKind.String);
                mouse.SetValue("MouseThreshold2", Text(thresholds[1]), RegistryValueKind.String);
                mouse.SetValue("MouseSpeed", Text(thresholds[2]), RegistryValueKind.String);
                mouse.SetValue("DoubleClickSpeed", Text(s.DoubleClickMs), RegistryValueKind.String);
                mouse.SetValue("SwapMouseButtons", s.SwapButtons ? "1" : "0", RegistryValueKind.String);
                mouse.SetValue("MouseTrails", Text(s.PointerTrails < 2 ? 0 : s.PointerTrails), RegistryValueKind.String);
                mouse.SetValue("SnapToDefaultButton", s.SnapToDefaultButton ? "1" : "0", RegistryValueKind.String);
            }

            using var desktop = Registry.CurrentUser.CreateSubKey(DesktopKey, writable: true);
            desktop?.SetValue("WheelScrollLines", s.ScrollOneScreen ? "-1" : Text(s.ScrollLines), RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Mouse settings were applied but could not be written to the registry; they may reset at the next sign-in.");
        }
    }

    private void PersistKeyboardRegistry(KeyboardSettings s)
    {
        try
        {
            using var keyboard = Registry.CurrentUser.CreateSubKey(KeyboardKey, writable: true);
            if (keyboard is not null)
            {
                keyboard.SetValue("KeyboardDelay", Text(s.RepeatDelay), RegistryValueKind.String);
                keyboard.SetValue("KeyboardSpeed", Text(s.RepeatRate), RegistryValueKind.String);
            }

            using var desktop = Registry.CurrentUser.CreateSubKey(DesktopKey, writable: true);
            desktop?.SetValue("CursorBlinkRate", Text(s.CursorBlinkMs <= 0 ? -1 : s.CursorBlinkMs), RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Keyboard settings were applied but could not be written to the registry; they may reset at the next sign-in.");
        }
    }

    private void Check(bool ok, string what, List<string> failures)
    {
        if (ok)
        {
            return;
        }

        int code = Marshal.GetLastWin32Error();
        string reason = code == 0 ? "no error code" : new Win32Exception(code).Message;
        failures.Add($"{what} ({reason})");
        _logger.LogWarning("SystemParametersInfo failed for {What}: {Reason}", what, reason);
    }

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
}
