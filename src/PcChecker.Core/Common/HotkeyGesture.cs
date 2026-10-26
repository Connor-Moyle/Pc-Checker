using System.Diagnostics.CodeAnalysis;

namespace PcChecker.Core.Common;

/// <summary>Modifier flags for a global hotkey. Values match the Win32 <c>MOD_*</c> constants.</summary>
[Flags]
public enum HotkeyModifierKeys
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Win = 0x0008,
}

/// <summary>
/// A parsed hotkey such as "Ctrl+Shift+O": modifier flags, the canonical key name and the Win32 virtual-key code.
/// Pure .NET so the overlay page can validate what the user types without touching the OS.
/// </summary>
public sealed record HotkeyGesture(HotkeyModifierKeys Modifiers, string Key, uint VirtualKey)
{
    private static readonly Dictionary<string, HotkeyModifierKeys> ModifierNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = HotkeyModifierKeys.Control,
        ["control"] = HotkeyModifierKeys.Control,
        ["ctl"] = HotkeyModifierKeys.Control,
        ["shift"] = HotkeyModifierKeys.Shift,
        ["alt"] = HotkeyModifierKeys.Alt,
        ["win"] = HotkeyModifierKeys.Win,
        ["windows"] = HotkeyModifierKeys.Win,
        ["super"] = HotkeyModifierKeys.Win,
        ["meta"] = HotkeyModifierKeys.Win,
    };

    private static readonly Dictionary<string, (string Canonical, uint Vk)> NamedKeys = BuildNamedKeys();

    /// <summary>Canonical text, e.g. "Ctrl+Shift+O".</summary>
    public override string ToString() => Format(Modifiers, Key);

    /// <summary>True when the gesture has at least one modifier or is a function key (the only keys safe to grab globally on their own).</summary>
    public bool IsSafeGlobalHotkey => Modifiers != HotkeyModifierKeys.None || (Key.Length >= 2 && Key[0] == 'F' && char.IsDigit(Key[1]));

    /// <summary>Parses text such as "Ctrl + Shift + O", "ctrl+alt+F9" or "Win+Space".</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out HotkeyGesture? gesture) => TryParse(text, out gesture, out _);

    /// <summary>Parses the text and explains why it was rejected when it fails.</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out HotkeyGesture? gesture, out string? error)
    {
        gesture = null;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Type a key combination such as Ctrl+Shift+O.";
            return false;
        }

        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            error = "Type a key combination such as Ctrl+Shift+O.";
            return false;
        }

        var modifiers = HotkeyModifierKeys.None;
        string? key = null;
        uint vk = 0;
        foreach (var part in parts)
        {
            if (ModifierNames.TryGetValue(part, out var modifier))
            {
                if (modifiers.HasFlag(modifier))
                {
                    error = $"\"{part}\" is listed twice.";
                    return false;
                }

                modifiers |= modifier;
                continue;
            }

            if (key is not null)
            {
                error = "Only one normal key is allowed (plus any modifiers).";
                return false;
            }

            if (!TryMapKey(part, out key, out vk))
            {
                error = $"\"{part}\" is not a key this app can use.";
                return false;
            }
        }

        if (key is null)
        {
            error = "Add a key after the modifiers, for example Ctrl+Shift+O.";
            return false;
        }

        var result = new HotkeyGesture(modifiers, key, vk);
        if (!result.IsSafeGlobalHotkey)
        {
            error = "Add a modifier (Ctrl, Alt, Shift or Win) so the key still works in other apps.";
            return false;
        }

        gesture = result;
        return true;
    }

    /// <summary>Parses or throws a <see cref="FormatException"/>.</summary>
    public static HotkeyGesture Parse(string text) =>
        TryParse(text, out var gesture, out var error) ? gesture : throw new FormatException(error);

    /// <summary>Builds the canonical text for the given parts (modifiers in Ctrl, Alt, Shift, Win order).</summary>
    public static string Format(HotkeyModifierKeys modifiers, string key)
    {
        var parts = new List<string>(5);
        if (modifiers.HasFlag(HotkeyModifierKeys.Control))
        {
            parts.Add("Ctrl");
        }

        if (modifiers.HasFlag(HotkeyModifierKeys.Alt))
        {
            parts.Add("Alt");
        }

        if (modifiers.HasFlag(HotkeyModifierKeys.Shift))
        {
            parts.Add("Shift");
        }

        if (modifiers.HasFlag(HotkeyModifierKeys.Win))
        {
            parts.Add("Win");
        }

        parts.Add(key);
        return string.Join("+", parts);
    }

    private static bool TryMapKey(string part, [NotNullWhen(true)] out string? canonical, out uint vk)
    {
        canonical = null;
        vk = 0;
        if (part.Length == 1)
        {
            char c = char.ToUpperInvariant(part[0]);
            if (c is (>= 'A' and <= 'Z') or (>= '0' and <= '9'))
            {
                canonical = c.ToString();
                vk = c;
                return true;
            }
        }

        if (NamedKeys.TryGetValue(part, out var named))
        {
            canonical = named.Canonical;
            vk = named.Vk;
            return true;
        }

        return false;
    }

    private static Dictionary<string, (string, uint)> BuildNamedKeys()
    {
        var map = new Dictionary<string, (string, uint)>(StringComparer.OrdinalIgnoreCase);
        void Add(uint vk, string canonical, params string[] aliases)
        {
            map[canonical] = (canonical, vk);
            foreach (var alias in aliases)
            {
                map[alias] = (canonical, vk);
            }
        }

        for (int i = 1; i <= 24; i++)
        {
            Add(0x6F + (uint)i, "F" + i);
        }

        for (int i = 0; i <= 9; i++)
        {
            Add(0x60 + (uint)i, "NumPad" + i, "Num" + i);
        }

        Add(0x20, "Space", "Spacebar");
        Add(0x09, "Tab");
        Add(0x0D, "Enter", "Return");
        Add(0x1B, "Esc", "Escape");
        Add(0x08, "Backspace", "Back");
        Add(0x2D, "Insert", "Ins");
        Add(0x2E, "Delete", "Del");
        Add(0x24, "Home");
        Add(0x23, "End");
        Add(0x21, "PageUp", "PgUp", "Prior");
        Add(0x22, "PageDown", "PgDn", "Next");
        Add(0x25, "Left");
        Add(0x26, "Up");
        Add(0x27, "Right");
        Add(0x28, "Down");
        Add(0x13, "Pause", "Break");
        Add(0x91, "ScrollLock", "Scroll");
        Add(0x2C, "PrintScreen", "PrtSc", "Snapshot");
        Add(0x6A, "Multiply", "NumPad*");
        Add(0x6B, "Add", "NumPad+");
        Add(0x6D, "Subtract", "NumPad-");
        Add(0x6E, "Decimal", "NumPad.");
        Add(0x6F, "Divide", "NumPad/");
        Add(0xBA, "Semicolon", ";");
        Add(0xBB, "Plus", "=", "Equals");
        Add(0xBC, "Comma", ",");
        Add(0xBD, "Minus", "-", "Dash");
        Add(0xBE, "Period", ".");
        Add(0xBF, "Slash", "/", "Question");
        Add(0xC0, "Tilde", "`", "~", "Backquote");
        Add(0xDB, "OpenBracket", "[");
        Add(0xDC, "Backslash", "\\", "Pipe");
        Add(0xDD, "CloseBracket", "]");
        Add(0xDE, "Quote", "'", "Apostrophe");
        return map;
    }
}
