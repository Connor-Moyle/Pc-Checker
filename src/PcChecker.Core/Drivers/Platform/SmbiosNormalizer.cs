using System.Text.RegularExpressions;
using PcChecker.Core.Drivers.Sources;
using PcChecker.Core.Hardware;

namespace PcChecker.Core.Drivers.Platform;

/// <summary>
/// Turns raw SMBIOS values into a clean <see cref="PcPlatformInfo"/>: maker names in their many spellings
/// ("ASUSTeK COMPUTER INC.", "Micro-Star International Co., Ltd.", "Gigabyte Technology Co., Ltd."), firmware
/// placeholders dropped, Dell service tag, Lenovo machine type, board revision, chipset and socket. Pure.
/// </summary>
public static partial class SmbiosNormalizer
{
    /// <summary>The maker behind a manufacturer string; <see cref="PcMaker.Unknown"/> for empty or placeholder values.</summary>
    public static PcMaker MakerOf(string? manufacturer)
    {
        if (OemToolCatalog.IsPlaceholder(manufacturer))
        {
            return PcMaker.Unknown;
        }

        var m = Regex.Replace(manufacturer!.Trim().ToLowerInvariant(), @"\s+", " ");
        if (m.StartsWith("asus", StringComparison.Ordinal)) { return PcMaker.Asus; }
        if (m.StartsWith("micro-star", StringComparison.Ordinal) || m.StartsWith("micro star", StringComparison.Ordinal) || m == "msi" || m.StartsWith("msi ", StringComparison.Ordinal)) { return PcMaker.Msi; }
        if (m.StartsWith("gigabyte", StringComparison.Ordinal) || m.StartsWith("aorus", StringComparison.Ordinal)) { return PcMaker.Gigabyte; }
        if (m.StartsWith("asrock", StringComparison.Ordinal)) { return PcMaker.AsRock; }
        if (m.StartsWith("biostar", StringComparison.Ordinal)) { return PcMaker.Biostar; }
        if (m.StartsWith("nzxt", StringComparison.Ordinal)) { return PcMaker.Nzxt; }
        if (m.StartsWith("dell", StringComparison.Ordinal) || m.StartsWith("alienware", StringComparison.Ordinal)) { return PcMaker.Dell; }
        if (m == "hp" || m.StartsWith("hp ", StringComparison.Ordinal) || m.StartsWith("hp-", StringComparison.Ordinal) || m.StartsWith("hewlett", StringComparison.Ordinal)) { return PcMaker.Hp; }
        if (m.StartsWith("lenovo", StringComparison.Ordinal)) { return PcMaker.Lenovo; }
        if (m.StartsWith("acer", StringComparison.Ordinal)) { return PcMaker.Acer; }
        if (m.StartsWith("microsoft", StringComparison.Ordinal)) { return PcMaker.Microsoft; }
        if (m.StartsWith("framework", StringComparison.Ordinal)) { return PcMaker.Framework; }
        return PcMaker.Other;
    }

    /// <summary>How the maker writes its own name.</summary>
    public static string MakerName(PcMaker maker, string? raw = null) => maker switch
    {
        PcMaker.Asus => "ASUS",
        PcMaker.Msi => "MSI",
        PcMaker.Gigabyte => "GIGABYTE",
        PcMaker.AsRock => "ASRock",
        PcMaker.Biostar => "BIOSTAR",
        PcMaker.Nzxt => "NZXT",
        PcMaker.Dell => "Dell",
        PcMaker.Hp => "HP",
        PcMaker.Lenovo => "Lenovo",
        PcMaker.Acer => "Acer",
        PcMaker.Microsoft => "Microsoft",
        PcMaker.Framework => "Framework",
        _ => CleanCompanyName(raw) ?? "your PC maker",
    };

    /// <summary>"Contoso Computers Co., Ltd." → "Contoso Computers"; null for placeholders.</summary>
    public static string? CleanCompanyName(string? raw)
    {
        if (OemToolCatalog.IsPlaceholder(raw))
        {
            return null;
        }

        var t = CompanySuffix().Replace(raw!.Trim(), string.Empty).Trim().TrimEnd(',', '.').Trim();
        return t.Length == 0 ? raw.Trim() : t;
    }

    [GeneratedRegex(@"[,.]?\s+(?:co\.?,?\s*ltd\.?|corporation|corp\.?|inc\.?|incorporated|ltd\.?|limited|gmbh|technology|international|computer)\b\.?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CompanySuffix();

    /// <summary>A model or product name with spaces tidied; null for empty values and firmware placeholders.</summary>
    public static string? CleanModel(string? value)
    {
        if (OemToolCatalog.IsPlaceholder(value))
        {
            return null;
        }

        var t = Regex.Replace(value!.Trim(), @"\s+", " ");
        return t.Length == 0 ? null : t;
    }

    /// <summary>A board product without MSI's "(MS-7D78)" code: "PRO B650-P WIFI (MS-7D78)" → "PRO B650-P WIFI".</summary>
    public static string? BoardNameWithoutCode(string? product)
    {
        var clean = CleanModel(product);
        if (clean is null)
        {
            return null;
        }

        var t = MsiCode().Replace(clean, string.Empty).Trim();
        return t.Length == 0 ? clean : t;
    }

    [GeneratedRegex(@"\s*\((?:MS-)?[0-9A-F]{4}\)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MsiCode();

    /// <summary>The board revision from Win32_BaseBoard.Version ("1.0", "Rev 1.0", "1.x"); null when it is a placeholder.</summary>
    public static string? BoardRevision(string? version)
    {
        if (OemToolCatalog.IsPlaceholder(version))
        {
            return null;
        }

        var m = Revision().Match(version!);
        return m.Success ? m.Groups["rev"].Value : null;
    }

    [GeneratedRegex(@"^(?:rev(?:ision)?\.?\s*)?(?<rev>\d{1,2}\.\d{1,2})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Revision();

    /// <summary>The Dell service tag when the BIOS serial number looks like one (7 letters and digits).</summary>
    public static string? DellServiceTag(string? serial)
    {
        if (string.IsNullOrWhiteSpace(serial))
        {
            return null;
        }

        var t = serial.Trim().ToUpperInvariant();
        return t.Length == 7 && t.All(char.IsAsciiLetterOrDigit) && !OemToolCatalog.IsPlaceholder(t) ? t : null;
    }

    /// <summary>Lenovo's machine type: the first four characters of a model such as "21AHCTO1WW" or "20XW0026US".</summary>
    public static string? LenovoMachineType(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        var t = model.Trim().ToUpperInvariant();
        return MachineTypeModel().IsMatch(t) ? t[..4] : null;
    }

    [GeneratedRegex(@"^[0-9A-Z]{4}(?:[0-9A-Z]{2,8})?$", RegexOptions.CultureInvariant)]
    private static partial Regex MachineTypeModel();

    /// <summary>A plain serial number (letters, digits and dashes only); null for placeholders and odd values.</summary>
    public static string? CleanSerial(string? serial)
    {
        if (OemToolCatalog.IsPlaceholder(serial))
        {
            return null;
        }

        var t = serial!.Trim();
        return t.Length is >= 4 and <= 32 && t.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') ? t : null;
    }

    /// <summary>The chipset named in a board product: "MAG B650 TOMAHAWK WIFI" → "B650"; "X670E AORUS MASTER" → "X670E".</summary>
    public static string? ChipsetModel(string? boardProduct)
    {
        if (string.IsNullOrWhiteSpace(boardProduct))
        {
            return null;
        }

        var m = ChipsetWord().Match(boardProduct);
        return m.Success ? m.Groups["chip"].Value.ToUpperInvariant() : null;
    }

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?<chip>(?:[ABXZHQW]\d{3}E?)|TRX40|TRX50|WRX80|WRX90)(?:M|I|-|\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChipsetWord();

    /// <summary>The AMD socket of a chipset: A620–X870E are AM5, A320–X570 are AM4, TRX40 sTRX4, TRX50 sTR5.</summary>
    public static string? AmdSocketOf(string? chipset) => chipset?.ToUpperInvariant() switch
    {
        "A620" or "B650" or "B650E" or "X670" or "X670E" or "B840" or "B850" or "X870" or "X870E" => "AM5",
        "A320" or "B350" or "X370" or "B450" or "X470" or "A520" or "B550" or "X570" => "AM4",
        "TRX40" => "sTRX4",
        "TRX50" => "sTR5",
        "WRX80" => "sWRX8",
        "WRX90" => "sWRX9",
        _ => null,
    };

    /// <summary>Whose chipset: from the processor maker, else the processor name, else the board's chipset.</summary>
    public static ChipsetPlatform PlatformOf(string? cpuManufacturer, string? cpuName, string? chipsetModel)
    {
        var maker = cpuManufacturer?.Trim() ?? string.Empty;
        if (maker.Contains("AMD", StringComparison.OrdinalIgnoreCase) || maker.Contains("Advanced Micro", StringComparison.OrdinalIgnoreCase))
        {
            return ChipsetPlatform.Amd;
        }

        if (maker.Contains("Intel", StringComparison.OrdinalIgnoreCase))
        {
            return ChipsetPlatform.Intel;
        }

        var traits = CpuTraits.Parse(cpuName);
        if (traits.IsAmd)
        {
            return ChipsetPlatform.Amd;
        }

        if (traits.IsIntel)
        {
            return ChipsetPlatform.Intel;
        }

        if (AmdSocketOf(chipsetModel) is not null)
        {
            return ChipsetPlatform.Amd;
        }

        return chipsetModel is { Length: > 0 } c && c[0] is 'Z' or 'H' or 'Q' or 'W' or 'B' ? ChipsetPlatform.Intel : ChipsetPlatform.Unknown;
    }

    /// <summary>Cleans every field and works out the maker, model, board, BIOS, chipset and socket.</summary>
    public static PcPlatformInfo Normalize(SmbiosFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var systemMaker = MakerOf(facts.SystemManufacturer);
        var boardMaker = MakerOf(facts.BoardManufacturer);
        var model = CleanModel(facts.SystemModel);
        var board = BoardNameWithoutCode(facts.BoardProduct);
        if (IsBoardAsModel(systemMaker, boardMaker, model, facts.BoardProduct, board))
        {
            // A self-built PC whose firmware repeats the board as the model ("MS-7D78", "B650 AORUS ELITE AX").
            model = null;
        }

        var chipset = ChipsetModel(board);
        var platform = PlatformOf(facts.CpuManufacturer, facts.CpuName, chipset);
        var socket = platform == ChipsetPlatform.Amd ? AmdSocketOf(chipset) ?? CpuTraits.Parse(facts.CpuName).Socket : CpuTraits.Parse(facts.CpuName).Socket;
        string? friendly = null;
        string? machineType = null;
        if (systemMaker == PcMaker.Lenovo)
        {
            machineType = LenovoMachineType(model);
            friendly = CleanModel(facts.ProductVersion) is { } v && v.Any(char.IsAsciiLetter) && v.Contains(' ', StringComparison.Ordinal) ? v : null;
        }

        var serial = CleanSerial(facts.ProductSerial) ?? CleanSerial(facts.BiosSerial);
        return new PcPlatformInfo
        {
            SystemMaker = systemMaker,
            SystemMakerName = systemMaker == PcMaker.Unknown ? null : MakerName(systemMaker, facts.SystemManufacturer),
            SystemModel = model,
            FriendlyModel = friendly,
            BoardMaker = boardMaker,
            BoardMakerName = boardMaker == PcMaker.Unknown ? null : MakerName(boardMaker, facts.BoardManufacturer),
            BoardProduct = board,
            BoardRevision = BoardRevision(facts.BoardVersion),
            BiosVendor = CleanCompanyName(facts.BiosManufacturer),
            BiosVersion = CleanModel(facts.BiosVersion),
            BiosDate = facts.BiosReleaseDate,
            ServiceTag = systemMaker == PcMaker.Dell ? DellServiceTag(facts.BiosSerial) ?? DellServiceTag(facts.ProductSerial) : null,
            SerialNumber = serial,
            MachineType = machineType,
            IsLaptop = DriverDeviceMapper.IsLaptop(facts.PcSystemType, facts.ChassisTypes),
            Chipset = platform,
            ChipsetModel = chipset,
            Socket = socket,
            CpuName = CleanModel(facts.CpuName),
        };
    }

    /// <summary>
    /// True when the "model" is really the motherboard: same maker as the board and either MSI's board code
    /// ("MS-7D78") or the board's own name.
    /// </summary>
    public static bool IsBoardAsModel(PcMaker systemMaker, PcMaker boardMaker, string? model, string? rawBoard, string? board)
    {
        // Only motherboard makers do this; Lenovo, Dell and HP put the machine type in both fields of a real model.
        if (model is null || systemMaker != boardMaker || boardMaker is not (PcMaker.Asus or PcMaker.Msi or PcMaker.Gigabyte or PcMaker.AsRock or PcMaker.Biostar or PcMaker.Nzxt))
        {
            return false;
        }

        return MsiBoardCode().IsMatch(model)
            || string.Equals(model, board, StringComparison.OrdinalIgnoreCase)
            || string.Equals(model, CleanModel(rawBoard), StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"^MS-[0-9A-F]{4}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MsiBoardCode();

    /// <summary>The facts the driver providers use, from a normalized platform.</summary>
    public static DriverSystemFacts ToDriverFacts(PcPlatformInfo info, SmbiosFacts raw)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(raw);
        return info.IsSelfBuilt
            ? new DriverSystemFacts(null, null, raw.BoardManufacturer, info.BoardProduct ?? raw.BoardProduct, info.IsLaptop)
            : new DriverSystemFacts(raw.SystemManufacturer, info.SystemModel ?? raw.SystemModel, raw.BoardManufacturer, info.BoardProduct ?? raw.BoardProduct, info.IsLaptop);
    }
}
