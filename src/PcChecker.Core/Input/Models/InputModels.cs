namespace PcChecker.Core.Input;

/// <summary>Windows mouse settings (mirrors the Mouse control panel and SystemParametersInfo values).</summary>
public sealed class MouseSettings
{
    /// <summary>Pointer speed 1–20 (10 = the "6/11" default).</summary>
    public int Speed { get; set; } = 10;
    public bool EnhancePointerPrecision { get; set; } = true;
    public int DoubleClickMs { get; set; } = 500;
    public bool SwapButtons { get; set; }
    public int ScrollLines { get; set; } = 3;
    public bool ScrollOneScreen { get; set; }
    public int PointerTrails { get; set; }
    public bool HidePointerWhileTyping { get; set; } = true;
    public bool SnapToDefaultButton { get; set; }

    public MouseSettings Clone() => (MouseSettings)MemberwiseClone();
}

/// <summary>Windows keyboard settings.</summary>
public sealed class KeyboardSettings
{
    /// <summary>Repeat delay 0 (short) – 3 (long).</summary>
    public int RepeatDelay { get; set; } = 1;

    /// <summary>Repeat rate 0 (slow) – 31 (fast).</summary>
    public int RepeatRate { get; set; } = 31;
    public int CursorBlinkMs { get; set; } = 530;
    public bool StickyKeys { get; set; }
    public bool FilterKeys { get; set; }
    public bool ToggleKeys { get; set; }

    public KeyboardSettings Clone() => (KeyboardSettings)MemberwiseClone();
}

public sealed record InputProfile(string Id, string Name, string Description, MouseSettings Mouse, KeyboardSettings Keyboard, bool IsBuiltIn);

/// <summary>Input settings stored in <c>AppSettings.Input</c>.</summary>
public sealed class InputSettings
{
    public List<InputProfile> Profiles { get; set; } = [];
}
