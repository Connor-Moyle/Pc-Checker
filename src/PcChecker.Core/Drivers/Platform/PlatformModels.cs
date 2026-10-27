using PcChecker.Core.Drivers.Sources;

namespace PcChecker.Core.Drivers.Platform;

/// <summary>The makers whose support pages PC Checker can open for the exact board or model.</summary>
public enum PcMaker
{
    Unknown,
    Asus,
    Msi,
    Gigabyte,
    AsRock,
    Biostar,
    Nzxt,
    Dell,
    Hp,
    Lenovo,
    Acer,
    Microsoft,
    Framework,

    /// <summary>A real maker name PC Checker has no support page for.</summary>
    Other,
}

/// <summary>Whose chipset the PC uses.</summary>
public enum ChipsetPlatform
{
    Unknown,
    Amd,
    Intel,
}

/// <summary>
/// The raw SMBIOS values Windows reports (Win32_ComputerSystem, Win32_ComputerSystemProduct, Win32_BaseBoard,
/// Win32_BIOS, Win32_SystemEnclosure) plus the processor. Firmware often fills fields with placeholders such as
/// "System Product Name" or "To Be Filled By O.E.M."; <see cref="SmbiosNormalizer"/> cleans them up.
/// </summary>
public sealed record SmbiosFacts
{
    public string? SystemManufacturer { get; init; }
    public string? SystemModel { get; init; }
    public string? SystemFamily { get; init; }
    public string? SystemSku { get; init; }

    /// <summary>Win32_ComputerSystemProduct.Version: Lenovo puts the friendly name here ("ThinkPad T14 Gen 3").</summary>
    public string? ProductVersion { get; init; }

    /// <summary>Win32_ComputerSystemProduct.IdentifyingNumber: the serial number.</summary>
    public string? ProductSerial { get; init; }

    public string? BoardManufacturer { get; init; }
    public string? BoardProduct { get; init; }
    public string? BoardVersion { get; init; }
    public string? BiosManufacturer { get; init; }

    /// <summary>Win32_BIOS.SMBIOSBIOSVersion ("2.A0", "F23c", "N3BET70W (1.48 )").</summary>
    public string? BiosVersion { get; init; }

    public DateTime? BiosReleaseDate { get; init; }

    /// <summary>Win32_BIOS.SerialNumber: Dell's service tag lives here.</summary>
    public string? BiosSerial { get; init; }

    /// <summary>Win32_SystemEnclosure.ChassisTypes, comma separated.</summary>
    public string? ChassisTypes { get; init; }

    /// <summary>Win32_ComputerSystem.PCSystemType.</summary>
    public string? PcSystemType { get; init; }

    public string? CpuName { get; init; }

    /// <summary>Win32_Processor.Manufacturer ("AuthenticAMD", "GenuineIntel").</summary>
    public string? CpuManufacturer { get; init; }
}

/// <summary>The cleaned-up identity of this PC: who made it, which board and BIOS, and whose chipset.</summary>
public sealed record PcPlatformInfo
{
    /// <summary>The PC maker (Dell, Lenovo…); <see cref="PcMaker.Unknown"/> for a self-built PC.</summary>
    public PcMaker SystemMaker { get; init; }
    public string? SystemMakerName { get; init; }

    /// <summary>The model, or null when the firmware has a placeholder.</summary>
    public string? SystemModel { get; init; }

    /// <summary>A friendlier model name when the maker reports one separately (Lenovo "ThinkPad T14 Gen 3").</summary>
    public string? FriendlyModel { get; init; }

    public PcMaker BoardMaker { get; init; }
    public string? BoardMakerName { get; init; }
    public string? BoardProduct { get; init; }

    /// <summary>The board revision ("1.0"), used by GIGABYTE's support pages; null when the firmware does not say.</summary>
    public string? BoardRevision { get; init; }

    public string? BiosVendor { get; init; }
    public string? BiosVersion { get; init; }
    public DateTime? BiosDate { get; init; }

    /// <summary>Dell's service tag, when the serial number looks like one.</summary>
    public string? ServiceTag { get; init; }

    /// <summary>The serial number (HP, Acer and Lenovo support pages ask for it). Never sent anywhere by PC Checker.</summary>
    public string? SerialNumber { get; init; }

    /// <summary>Lenovo's four-character machine type ("21AH"), used for its catalog and support page.</summary>
    public string? MachineType { get; init; }

    public bool IsLaptop { get; init; }
    public ChipsetPlatform Chipset { get; init; }

    /// <summary>The board's chipset when the product name shows it ("B650", "X670E", "Z790").</summary>
    public string? ChipsetModel { get; init; }

    /// <summary>The processor socket ("AM5", "AM4", "sTRX4", "LGA1700") when known.</summary>
    public string? Socket { get; init; }

    public string? CpuName { get; init; }

    /// <summary>True for a self-built or white-box PC: the board maker is known, the PC maker or model is not.</summary>
    public bool IsSelfBuilt => (SystemMaker is PcMaker.Unknown or PcMaker.Other || SystemModel is null) && BoardMaker is not PcMaker.Unknown;

    /// <summary>Whose support page has this PC's drivers and BIOS: the PC maker for a brand PC, else the board maker.</summary>
    public PcMaker SupportMaker => IsSelfBuilt || SystemMaker == PcMaker.Unknown ? BoardMaker : SystemMaker;

    /// <summary>"MSI PRO B650-P WIFI" or "Lenovo ThinkPad T14 Gen 3 (21AH)"; "This PC" when nothing is known.</summary>
    public string DisplayName
    {
        get
        {
            if (!IsSelfBuilt && SystemModel is not null)
            {
                var model = FriendlyModel ?? SystemModel;
                var name = model.StartsWith(SystemMakerName ?? "\u0000", StringComparison.OrdinalIgnoreCase) ? model : $"{SystemMakerName} {model}".Trim();
                return MachineType is not null && FriendlyModel is not null ? $"{name} ({MachineType})" : name;
            }

            if (BoardProduct is not null)
            {
                return BoardProduct.StartsWith(BoardMakerName ?? "\u0000", StringComparison.OrdinalIgnoreCase) ? BoardProduct : $"{BoardMakerName} {BoardProduct}".Trim();
            }

            return SystemMakerName ?? BoardMakerName ?? "This PC";
        }
    }
}

/// <summary>A link shown on the "Your PC" card or offered for a device.</summary>
/// <param name="Exact">True when the page is for this exact board or model; false for a general page where the user picks the model.</param>
public sealed record PlatformLink(string Title, string Url, string Note, bool Exact);

/// <summary>What is known about the BIOS: the installed version, the maker's newest when it could be read, and its age.</summary>
public sealed record BiosStatus(
    string? InstalledVersion,
    DateTime? InstalledDate,
    string? LatestVersion,
    DateTime? LatestDate,
    string? LatestSource,
    DriverVersionComparison Comparison,
    PlatformLink? Page)
{
    /// <summary>Whole years since the installed BIOS was released, or null when the date is unknown.</summary>
    public int? AgeYears { get; init; }

    /// <summary>True when the installed BIOS is more than <see cref="BiosAgeRule.OldAfterYears"/> years old.</summary>
    public bool IsOld { get; init; }
}

/// <summary>The chipset driver: installed version, the maker's newest and where it comes from.</summary>
public sealed record ChipsetStatus(
    ChipsetPlatform Platform,
    string Name,
    string? InstalledVersion,
    string? LatestVersion,
    DateTime? LatestDate,
    DriverVersionComparison Comparison,
    PlatformLink Page)
{
    /// <summary>The maker's installer, when it can be downloaded from the maker's own server.</summary>
    public string? DownloadUrl { get; init; }

    /// <summary>The page the download link was read from (sent as the referrer).</summary>
    public string? ReferrerUrl { get; init; }

    /// <summary>True when the newest version was read from the maker (not just a link).</summary>
    public bool CheckedOnline { get; init; }

    /// <summary>One plain line: what was found or why it could not be checked.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Extra links (Intel Management Engine from the PC maker, Intel's assistant).</summary>
    public IReadOnlyList<PlatformLink> MoreLinks { get; init; } = [];
}

/// <summary>One package from a PC maker's catalog (Lenovo): a driver or the BIOS with its version and date.</summary>
public sealed record MakerCatalogEntry(
    string Category,
    string Title,
    string Version,
    DateTime? ReleaseDate,
    string? ReadmeUrl)
{
    /// <summary>The hardware ids the package's driver installs on (matched against the installed devices).</summary>
    public IReadOnlyList<string> HardwareIds { get; init; } = [];

    /// <summary>The Windows driver version the package installs ("24.30.1.1"), when it says.</summary>
    public string? DriverVersion { get; init; }

    /// <summary>True for a BIOS (UEFI) update.</summary>
    public bool IsBios { get; init; }

    /// <summary>The BIOS ids the package is for ("N3BET"), from its detection rules.</summary>
    public IReadOnlyList<string> BiosLevels { get; init; } = [];
}

/// <summary>Everything the "Your PC" card shows and the driver search uses for chipset, BIOS and maker catalogs.</summary>
public sealed record PcPlatformReport(
    PcPlatformInfo Info,
    BiosStatus Bios,
    ChipsetStatus? Chipset,
    PlatformLink? SupportPage,
    IReadOnlyList<MakerCatalogEntry> CatalogEntries,
    IReadOnlyList<string> Notes,
    DateTimeOffset Captured)
{
    /// <summary>True when the maker sites were asked (or a saved answer under 12 hours old was used).</summary>
    public bool CheckedOnline { get; init; }

    /// <summary>Where the maker's catalog came from ("Lenovo catalog"), or null when there is none.</summary>
    public string? CatalogSource { get; init; }
}

/// <summary>A saved maker answer and how old it is.</summary>
public sealed record CachedText(string Text, DateTimeOffset FetchedAt, bool FromCache, bool Stale);
