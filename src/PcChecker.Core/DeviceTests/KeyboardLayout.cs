namespace PcChecker.Core.DeviceTests;

/// <summary>
/// One key on the on-screen keyboard. <see cref="Code"/> is the physical key (scan code, plus 0xE000 for the
/// extended keys), so the layout is the same whatever keyboard language Windows uses. Positions and sizes are in key
/// units (one normal key is 1 x 1).
/// </summary>
public sealed record KeyCap(int Code, string Label, double X, double Y, double Width = 1, double Height = 1);

/// <summary>A full-size (104-key, ANSI) keyboard with function row, navigation block and number pad.</summary>
public static class KeyboardLayout
{
    /// <summary>Width of the whole layout in key units.</summary>
    public const double TotalWidth = 22.5;

    /// <summary>Height of the whole layout in key units.</summary>
    public const double TotalHeight = 6.5;

    public static IReadOnlyList<KeyCap> FullSize { get; } = Build();

    /// <summary>The key with <paramref name="code"/>, or null when it is not on the layout (media keys, the extra ISO key).</summary>
    public static KeyCap? Find(int code) => FullSize.FirstOrDefault(k => k.Code == code);

    private static List<KeyCap> Build()
    {
        var keys = new List<KeyCap>();
        void Row(double y, double x, params (int Code, string Label, double Width)[] items)
        {
            foreach (var (code, label, width) in items)
            {
                keys.Add(new KeyCap(code, label, x, y, width));
                x += width;
            }
        }

        const int E = KeyCodes.Extended;

        // Function row.
        Row(0, 0, (0x01, "Esc", 1));
        Row(0, 2, (0x3B, "F1", 1), (0x3C, "F2", 1), (0x3D, "F3", 1), (0x3E, "F4", 1));
        Row(0, 6.5, (0x3F, "F5", 1), (0x40, "F6", 1), (0x41, "F7", 1), (0x42, "F8", 1));
        Row(0, 11, (0x43, "F9", 1), (0x44, "F10", 1), (0x57, "F11", 1), (0x58, "F12", 1));
        Row(0, 15.25, (E | 0x37, "PrtSc", 1), (0x46, "ScrLk", 1), (0x45, "Pause", 1));

        // Number row.
        Row(1.5, 0, (0x29, "`", 1), (0x02, "1", 1), (0x03, "2", 1), (0x04, "3", 1), (0x05, "4", 1), (0x06, "5", 1),
            (0x07, "6", 1), (0x08, "7", 1), (0x09, "8", 1), (0x0A, "9", 1), (0x0B, "0", 1), (0x0C, "-", 1), (0x0D, "=", 1),
            (0x0E, "Backspace", 2));
        Row(1.5, 15.25, (E | 0x52, "Ins", 1), (E | 0x47, "Home", 1), (E | 0x49, "PgUp", 1));
        Row(1.5, 18.5, (E | 0x45, "Num", 1), (E | 0x35, "/", 1), (0x37, "*", 1), (0x4A, "-", 1));

        // Top letter row.
        Row(2.5, 0, (0x0F, "Tab", 1.5), (0x10, "Q", 1), (0x11, "W", 1), (0x12, "E", 1), (0x13, "R", 1), (0x14, "T", 1),
            (0x15, "Y", 1), (0x16, "U", 1), (0x17, "I", 1), (0x18, "O", 1), (0x19, "P", 1), (0x1A, "[", 1), (0x1B, "]", 1),
            (0x2B, "\\", 1.5));
        Row(2.5, 15.25, (E | 0x53, "Del", 1), (E | 0x4F, "End", 1), (E | 0x51, "PgDn", 1));
        Row(2.5, 18.5, (0x47, "7", 1), (0x48, "8", 1), (0x49, "9", 1));
        keys.Add(new KeyCap(0x4E, "+", 21.5, 2.5, 1, 2));

        // Home row.
        Row(3.5, 0, (0x3A, "Caps", 1.75), (0x1E, "A", 1), (0x1F, "S", 1), (0x20, "D", 1), (0x21, "F", 1), (0x22, "G", 1),
            (0x23, "H", 1), (0x24, "J", 1), (0x25, "K", 1), (0x26, "L", 1), (0x27, ";", 1), (0x28, "'", 1), (0x1C, "Enter", 2.25));
        Row(3.5, 18.5, (0x4B, "4", 1), (0x4C, "5", 1), (0x4D, "6", 1));

        // Bottom letter row.
        Row(4.5, 0, (0x2A, "Shift", 2.25), (0x2C, "Z", 1), (0x2D, "X", 1), (0x2E, "C", 1), (0x2F, "V", 1), (0x30, "B", 1),
            (0x31, "N", 1), (0x32, "M", 1), (0x33, ",", 1), (0x34, ".", 1), (0x35, "/", 1), (0x36, "Shift", 2.75));
        Row(4.5, 16.25, (E | 0x48, "↑", 1));
        Row(4.5, 18.5, (0x4F, "1", 1), (0x50, "2", 1), (0x51, "3", 1));
        keys.Add(new KeyCap(E | 0x1C, "Enter", 21.5, 4.5, 1, 2));

        // Space row.
        Row(5.5, 0, (0x1D, "Ctrl", 1.25), (E | 0x5B, "Win", 1.25), (0x38, "Alt", 1.25), (0x39, "Space", 6.25),
            (E | 0x38, "Alt", 1.25), (E | 0x5C, "Win", 1.25), (E | 0x5D, "Menu", 1.25), (E | 0x1D, "Ctrl", 1.25));
        Row(5.5, 15.25, (E | 0x4B, "←", 1), (E | 0x50, "↓", 1), (E | 0x4D, "→", 1));
        Row(5.5, 18.5, (0x52, "0", 2), (0x53, ".", 1));
        return keys;
    }
}

/// <summary>Turns a Windows key message into the physical key code used by <see cref="KeyboardLayout"/>.</summary>
public static class KeyCodes
{
    /// <summary>Added to the scan code of extended keys (right Ctrl and Alt, arrows, number pad Enter and /).</summary>
    public const int Extended = 0xE000;

    /// <summary>Added to the virtual-key code of keys that report no scan code (some virtual and media keys).</summary>
    public const int VirtualOnly = 0x10000;

    private const int VkPause = 0x13;
    private const int VkSnapshot = 0x2C;
    private const int VkNumLock = 0x90;

    /// <summary>
    /// The physical key behind a WM_KEYDOWN/WM_KEYUP message, from its virtual-key code and the scan code and extended
    /// bit in lParam. Returns null for messages that are not a real key: the "fake Shift" Windows adds around number pad
    /// keys while Num Lock is on, and the reserved code 0xFF.
    /// </summary>
    public static int? FromMessage(int virtualKey, int scanCode, bool extended)
    {
        if (virtualKey == 0xFF)
        {
            return null;
        }

        // Pause, Num Lock and Print Screen report confusing scan codes (Pause and Num Lock share 0x45).
        switch (virtualKey)
        {
            case VkPause:
                return 0x45;
            case VkNumLock:
                return Extended | 0x45;
            case VkSnapshot:
                return Extended | 0x37;
        }

        scanCode &= 0xFF;
        if (scanCode == 0)
        {
            return virtualKey <= 0 ? null : VirtualOnly | virtualKey;
        }

        if (extended && scanCode is 0x2A or 0x36)
        {
            return null;
        }

        return extended ? Extended | scanCode : scanCode;
    }

    /// <summary>Splits a WM_KEYDOWN lParam into its scan code, extended bit and "was already down" (auto-repeat) bit.</summary>
    public static (int ScanCode, bool Extended, bool IsRepeat) SplitLParam(long lParam) =>
        ((int)((lParam >> 16) & 0xFF), ((lParam >> 24) & 1) != 0, ((lParam >> 30) & 1) != 0);

    /// <summary>A short technical description such as "scan code 1C" or "scan code E0 1C".</summary>
    public static string Describe(int code) =>
        (code & VirtualOnly) != 0 ? $"virtual key {code & 0xFFFF:X2}"
        : (code & Extended) != 0 ? $"scan code E0 {code & 0xFF:X2}"
        : $"scan code {code:X2}";
}
