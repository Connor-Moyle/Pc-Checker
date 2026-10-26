namespace PcChecker.Core.Displays;

/// <summary>
/// Pure text helpers for the display module: connection types from DisplayConfig output technology codes,
/// <c>ChangeDisplaySettingsEx</c> result codes, orientation labels, EDID manufacturer decoding and screen size maths.
/// </summary>
public static class DisplayText
{
    /// <summary><c>DISPLAYCONFIG_OUTPUT_TECHNOLOGY</c> code → plain connection name ("HDMI", "DisplayPort", "Built-in"…).</summary>
    public static string OutputTechnologyText(uint outputTechnology) => outputTechnology switch
    {
        0 => "VGA",
        1 => "S-Video",
        2 => "Composite",
        3 => "Component",
        4 => "DVI",
        5 => "HDMI",
        6 => "LVDS (built-in)",
        8 => "D-Terminal",
        9 => "SDI",
        10 => "DisplayPort",
        11 => "Embedded DisplayPort (built-in)",
        12 => "UDI",
        13 => "Embedded UDI (built-in)",
        14 => "SDTV dongle",
        15 => "Miracast (wireless)",
        16 => "Indirect display (wired)",
        17 => "Virtual display",
        18 => "DisplayPort over USB-C",
        0x80000000 => "Built-in panel",
        0xFFFFFFFF => "Other",
        _ => "Unknown",
    };

    /// <summary>True for connection codes that describe a laptop/tablet panel rather than an external monitor.</summary>
    public static bool IsInternalOutput(uint outputTechnology) =>
        outputTechnology is 6 or 11 or 13 or 0x80000000;

    /// <summary><c>DISP_CHANGE_*</c> result of <c>ChangeDisplaySettingsEx</c> → friendly message.</summary>
    public static string DispChangeMessage(int code) => code switch
    {
        0 => "The display settings were changed.",
        1 => "The display settings were saved but Windows needs a restart before they take effect.",
        -1 => "The display driver refused the change.",
        -2 => "This display does not support that resolution and refresh rate combination.",
        -3 => "Windows could not write the new settings to the registry.",
        -4 => "The change was requested with an invalid set of options.",
        -5 => "One of the requested values was invalid.",
        -6 => "The change is not allowed while the system is in DualView mode.",
        _ => $"The display driver returned an unexpected result ({code}).",
    };

    /// <summary>True when the result code means the change was accepted (now or after a restart).</summary>
    public static bool IsDispChangeSuccess(int code) => code is 0 or 1;

    /// <summary>0/90/180/270 → "Landscape", "Portrait", "Landscape (flipped)", "Portrait (flipped)".</summary>
    public static string OrientationText(int degrees) => Normalize(degrees) switch
    {
        90 => "Portrait",
        180 => "Landscape (flipped)",
        270 => "Portrait (flipped)",
        _ => "Landscape",
    };

    /// <summary>The <c>DMDO_*</c> value for an orientation in degrees (0 → 0, 90 → 1, 180 → 2, 270 → 3).</summary>
    public static int OrientationToDevMode(int degrees) => Normalize(degrees) / 90;

    /// <summary>Degrees from a <c>DMDO_*</c> value.</summary>
    public static int OrientationFromDevMode(uint devModeOrientation) => (int)(devModeOrientation % 4) * 90;

    /// <summary>Snaps any angle to 0, 90, 180 or 270.</summary>
    public static int Normalize(int degrees)
    {
        int d = ((degrees % 360) + 360) % 360;
        return (int)Math.Round(d / 90.0) % 4 * 90;
    }

    /// <summary>True when rotating from one orientation to another swaps the width and height.</summary>
    public static bool SwapsDimensions(int fromDegrees, int toDegrees) =>
        (Normalize(fromDegrees) / 90 % 2) != (Normalize(toDegrees) / 90 % 2);

    /// <summary>
    /// Decodes the EDID manufacturer id as DisplayConfig hands it over (the two EDID bytes read as a little-endian
    /// <c>ushort</c>) into the three-letter PNP code, e.g. 0xAC10 → "DEL".
    /// </summary>
    public static string DecodeEdidManufacturer(ushort raw)
    {
        int swapped = ((raw & 0xFF) << 8) | (raw >> 8);
        char a = (char)('@' + ((swapped >> 10) & 0x1F));
        char b = (char)('@' + ((swapped >> 5) & 0x1F));
        char c = (char)('@' + (swapped & 0x1F));
        var text = new string([a, b, c]);
        return text.All(ch => ch is >= 'A' and <= 'Z') ? text : string.Empty;
    }

    /// <summary>Three-letter PNP code → well-known brand ("AUS" → "ASUS"); unknown codes are returned unchanged.</summary>
    public static string ManufacturerName(string? pnpCode)
    {
        if (string.IsNullOrWhiteSpace(pnpCode))
        {
            return string.Empty;
        }

        return Brands.TryGetValue(pnpCode.Trim(), out var name) ? name : pnpCode.Trim();
    }

    /// <summary>Diagonal in inches from the EDID image size in centimetres; null when either side is unknown.</summary>
    public static double? DiagonalInches(int widthCm, int heightCm)
    {
        if (widthCm <= 0 || heightCm <= 0)
        {
            return null;
        }

        double cm = Math.Sqrt((double)widthCm * widthCm + (double)heightCm * heightCm);
        return Math.Round(cm / 2.54, 1);
    }

    /// <summary>"27-inch" style label, or null.</summary>
    public static string? DiagonalText(double? inches) =>
        inches is null or <= 0 ? null : $"{Math.Round(inches.Value)}-inch";

    /// <summary>Short friendly summary of a mode including the aspect ratio: "2560 × 1440 (16:9) at 240 Hz".</summary>
    public static string ModeText(DisplayMode mode) =>
        $"{mode.Width} × {mode.Height} ({DisplayModeMath.AspectRatioText(mode.Width, mode.Height)}) at {mode.RefreshHz} Hz";

    /// <summary>Converts the WMI <c>ushort[]</c> text fields of <c>WmiMonitorID</c> to a trimmed string.</summary>
    public static string WmiText(IEnumerable<ushort>? codes)
    {
        if (codes is null)
        {
            return string.Empty;
        }

        var chars = codes.TakeWhile(c => c != 0).Select(c => (char)c).ToArray();
        return new string(chars).Trim();
    }

    private static readonly Dictionary<string, string> Brands = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ACI"] = "ASUS", ["AUS"] = "ASUS", ["ACR"] = "Acer", ["AOC"] = "AOC", ["APP"] = "Apple", ["BNQ"] = "BenQ",
        ["BOE"] = "BOE", ["AUO"] = "AU Optronics", ["CMN"] = "Chi Mei (Innolux)", ["DEL"] = "Dell", ["ENC"] = "Eizo",
        ["GSM"] = "LG", ["LGD"] = "LG Display", ["GBT"] = "Gigabyte", ["HPN"] = "HP", ["HWP"] = "HP", ["IVM"] = "iiyama",
        ["LEN"] = "Lenovo", ["MSI"] = "MSI", ["MSH"] = "MSI", ["NEC"] = "NEC", ["PHL"] = "Philips", ["SAM"] = "Samsung",
        ["SEC"] = "Samsung", ["SHP"] = "Sharp", ["SNY"] = "Sony", ["VSC"] = "ViewSonic", ["XMI"] = "Xiaomi",
        ["HSD"] = "HannStar", ["CPQ"] = "Compaq", ["EIZ"] = "Eizo", ["KTC"] = "KTC", ["GIG"] = "Gigabyte", ["COR"] = "Corsair",
        ["ALI"] = "Alienware", ["DWE"] = "Dell", ["MED"] = "Medion", ["PNR"] = "Planar", ["TSB"] = "Toshiba", ["HKC"] = "HKC",
    };
}
