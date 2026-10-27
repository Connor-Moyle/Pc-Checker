namespace PcChecker.Core.GameRuntimes;

/// <summary>The two kinds of Windows program a DLL can belong to.</summary>
public enum DllArch
{
    /// <summary>32-bit (PE32). On 64-bit Windows these DLLs live in SysWOW64.</summary>
    X86,

    /// <summary>64-bit (PE32+). On 64-bit Windows these DLLs live in System32.</summary>
    X64,
}

/// <summary>Which architectures a DLL exists for.</summary>
[Flags]
public enum ArchSet
{
    None = 0,
    X86 = 1,
    X64 = 2,
    Both = X86 | X64,
}

/// <summary>How a package is put on the PC, which decides what its Install button does.</summary>
public enum RuntimeInstallKind
{
    /// <summary>An official installer downloaded from the vendor (or installed with winget).</summary>
    Installer,

    /// <summary>A Windows optional feature that is switched on (.NET Framework 3.5, DirectPlay).</summary>
    WindowsFeature,

    /// <summary>Installed from Windows Settings (the Media Feature Pack on N editions).</summary>
    WindowsSettings,

    /// <summary>Comes with the graphics driver (Vulkan); updated on the Drivers page.</summary>
    GraphicsDriver,

    /// <summary>Part of Windows itself; nothing to install.</summary>
    PartOfWindows,

    /// <summary>No official installer exists any more.</summary>
    NotAvailable,
}

/// <summary>What the downloaded file is and how it is started.</summary>
public enum InstallerFormat
{
    /// <summary>A signed .exe that is run with <see cref="RuntimeDownload.Arguments"/>.</summary>
    Exe,

    /// <summary>A signed .msi that msiexec installs quietly.</summary>
    Msi,

    /// <summary>A .zip that holds one signed .exe (<see cref="RuntimeDownload.InnerFileName"/>).</summary>
    ZipWithExe,

    /// <summary>The DirectX redistributable: a signed self-extractor whose signed DXSETUP.exe is then run.</summary>
    DirectXRedist,
}

/// <summary>Where a DLL is looked for.</summary>
public enum DllLocation
{
    /// <summary>System32 (64-bit) or SysWOW64 (32-bit).</summary>
    System,

    /// <summary>A side-by-side assembly in the WinSxS folder (Visual C++ 2005 and 2008).</summary>
    WinSxS,

    /// <summary>The .NET Framework 2.0 folder (Framework or Framework64\v2.0.50727).</summary>
    NetFx2,

    /// <summary>The XNA Framework 4.0 assemblies in the 32-bit global assembly cache.</summary>
    XnaGac,

    /// <summary>The system folders plus NVIDIA's PhysX folders under Program Files.</summary>
    PhysX,
}

/// <summary>One DLL that games ask for, and the package family that provides it.</summary>
/// <param name="Name">File name, lower case, with ".dll".</param>
/// <param name="FamilyId">The <see cref="RuntimePackage.FamilyId"/> that provides it.</param>
/// <param name="Archs">The architectures the DLL exists for.</param>
/// <param name="Location">Where it is installed.</param>
/// <param name="SxsAssembly">For <see cref="DllLocation.WinSxS"/>: the assembly name ("microsoft.vc80.crt").</param>
public sealed record KnownDll(string Name, string FamilyId, ArchSet Archs, DllLocation Location = DllLocation.System, string? SxsAssembly = null)
{
    public bool Has(DllArch arch) => Archs.HasFlag(arch == DllArch.X86 ? ArchSet.X86 : ArchSet.X64);
}

/// <summary>The download of an official installer.</summary>
/// <param name="Url">HTTPS link on the vendor's own server (redirects are checked against the package's hosts too).</param>
/// <param name="FileName">Name the download is saved under.</param>
/// <param name="Format">What the file is.</param>
/// <param name="Arguments">Documented silent switches for the downloaded file.</param>
/// <param name="InnerFileName">For a .zip: the .exe inside it. For DirectX: the setup program after extraction.</param>
/// <param name="InnerArguments">Switches for <see cref="InnerFileName"/>.</param>
/// <param name="MaxBytes">Largest download accepted.</param>
public sealed record RuntimeDownload(
    string Url,
    string FileName,
    InstallerFormat Format,
    IReadOnlyList<string> Arguments,
    string? InnerFileName = null,
    IReadOnlyList<string>? InnerArguments = null,
    long MaxBytes = 256L * 1024 * 1024);

/// <summary>
/// One official package that provides DLLs. Packages with the same <see cref="FamilyId"/> are the 32-bit and 64-bit
/// builds of the same runtime (Visual C++); a package with <see cref="Arch"/> null covers both.
/// </summary>
public sealed record RuntimePackage(
    string Id,
    string FamilyId,
    string Name,
    string Vendor,
    string Purpose,
    DllArch? Arch,
    RuntimeInstallKind Kind)
{
    /// <summary>winget package id, tried first when winget is available.</summary>
    public string? WingetId { get; init; }

    /// <summary>The official download, used when winget is not available or not set.</summary>
    public RuntimeDownload? Download { get; init; }

    /// <summary>For <see cref="RuntimeInstallKind.WindowsFeature"/>: the DISM feature name.</summary>
    public string? FeatureName { get; init; }

    /// <summary>The vendor's own page about the package (opened when nothing can be installed from here).</summary>
    public string? InfoUrl { get; init; }

    /// <summary>Accepted Authenticode signer names (certificate CN or O). The first is shown to the user.</summary>
    public IReadOnlyList<string> ExpectedSigners { get; init; } = [];

    /// <summary>HTTPS hosts the download (and every redirect) may come from.</summary>
    public IReadOnlyList<string> AllowedHosts { get; init; } = [];

    /// <summary>Why the Install button is off, for packages that cannot be installed from here.</summary>
    public string? UnavailableReason { get; init; }

    /// <summary>Where the link and switches came from (shown in the docs, kept for reviewers).</summary>
    public string? SourceNote { get; init; }

    /// <summary>The newest version the official download installs, for display.</summary>
    public string? VersionText { get; init; }

    public bool CanInstall => Kind is RuntimeInstallKind.Installer or RuntimeInstallKind.WindowsFeature
                              && (Kind != RuntimeInstallKind.Installer || Download is not null || WingetId is not null);

    /// <summary>"32-bit", "64-bit" or "32 and 64-bit".</summary>
    public string ArchText => Arch switch
    {
        DllArch.X86 => "32-bit",
        DllArch.X64 => "64-bit",
        _ => "32 and 64-bit",
    };

    /// <summary>The download host shown in the confirmation, or null.</summary>
    public string? SourceHost => Download is { } d && Uri.TryCreate(d.Url, UriKind.Absolute, out var u) ? u.Host : null;
}

/// <summary>Whether one DLL is on the PC for one architecture.</summary>
public enum DllStatus
{
    Present,
    Missing,

    /// <summary>The DLL is never made for this architecture.</summary>
    NotApplicable,
}

/// <summary>One row of the DLL list: a DLL with its 32-bit and 64-bit state.</summary>
public sealed record DllStatusRow(KnownDll Dll, DllStatus Status32, DllStatus Status64)
{
    public bool IsMissing => Status32 == DllStatus.Missing || Status64 == DllStatus.Missing;
}

/// <summary>Installed state of a package.</summary>
public enum PackageState
{
    Installed,

    /// <summary>Installed, but some of its DLLs are gone (Install repairs it).</summary>
    Partial,

    Missing,

    /// <summary>Nothing to install here (part of Windows, comes with a driver, not available, or not needed on this edition).</summary>
    NotApplicable,
}

/// <summary>A package with what the scan found.</summary>
public sealed record PackageStatus(RuntimePackage Package, PackageState State, int PresentDlls, int TotalDlls, IReadOnlyList<string> InstalledVersions);

/// <summary>Everything the DLL page shows after a scan.</summary>
public sealed record RuntimeScanResult(
    IReadOnlyList<DllStatusRow> Dlls,
    IReadOnlyList<PackageStatus> Packages,
    bool IsNEdition,
    DateTimeOffset ScannedAt,
    IReadOnlyList<string> Warnings)
{
    public static RuntimeScanResult Empty { get; } = new([], [], false, DateTimeOffset.MinValue, []);
}

/// <summary>What the scan read from the PC, as plain data for <see cref="RuntimeStatusRules"/>.</summary>
/// <param name="IsPresent">Whether a DLL is installed for an architecture.</param>
/// <param name="InstalledVersions">Versions from the uninstall entries and runtime keys, per package id.</param>
/// <param name="FeatureEnabled">Optional feature states by feature name; missing or null when unknown.</param>
/// <param name="IsNEdition">True on Windows N editions (no media components until the Media Feature Pack is added).</param>
public sealed record RuntimeProbe(
    Func<KnownDll, DllArch, bool> IsPresent,
    IReadOnlyDictionary<string, IReadOnlyList<string>> InstalledVersions,
    IReadOnlyDictionary<string, bool?> FeatureEnabled,
    bool IsNEdition);

/// <summary>How one DLL a game imports was resolved.</summary>
public enum ImportResolution
{
    /// <summary>The game ships the DLL in its own folder.</summary>
    InGameFolder,

    /// <summary>An api-ms-win-* or ext-ms-* API set that Windows resolves itself.</summary>
    WindowsApiSet,

    /// <summary>A Windows DLL that is present.</summary>
    Windows,

    /// <summary>A runtime DLL that is installed.</summary>
    RuntimeInstalled,

    /// <summary>A runtime DLL that is missing; installing its package fixes it.</summary>
    RuntimeMissing,

    /// <summary>A Windows part that is missing (needs a Windows repair or optional component).</summary>
    WindowsPartMissing,

    /// <summary>Unknown and not found; usually part of the game itself.</summary>
    NotFound,
}

/// <summary>One DLL a game needs, for one architecture.</summary>
public sealed record GameImport(string DllName, DllArch Arch, string ImportedBy, bool DelayLoaded, ImportResolution Resolution, string? PackageId)
{
    public bool IsProblem => Resolution is ImportResolution.RuntimeMissing or ImportResolution.WindowsPartMissing or ImportResolution.NotFound;
}

/// <summary>The result of scanning a game folder.</summary>
public sealed record GameScanResult(
    string Target,
    string GameName,
    int FilesRead,
    int FilesSkipped,
    bool HitFileLimit,
    bool Has32Bit,
    bool Has64Bit,
    IReadOnlyList<GameImport> Imports,
    IReadOnlyList<string> NeededPackageIds)
{
    public int ProblemCount => Imports.Count(i => i.IsProblem);
}

/// <summary>One PE file's imports.</summary>
public sealed record PeImportInfo(ushort Machine, bool IsPe32Plus, bool IsDll, IReadOnlyList<string> Imports, IReadOnlyList<string> DelayImports)
{
    public DllArch Arch => IsPe32Plus ? DllArch.X64 : DllArch.X86;
}

/// <summary>A parsed file of a game, with its path relative to the game folder.</summary>
public sealed record GameBinary(string RelativePath, PeImportInfo Info);
