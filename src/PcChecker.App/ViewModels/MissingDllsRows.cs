using PcChecker.App.Localization;
using PcChecker.Core.GameRuntimes;

namespace PcChecker.App.ViewModels;

/// <summary>Display state of one DLL for one architecture, used for the coloured status text.</summary>
public enum DllCellState
{
    Present,
    Missing,
    NotMade,
}

/// <summary>One row of the DLL list.</summary>
public sealed class MissingDllRow
{
    public MissingDllRow(DllStatusRow row, IReadOnlyList<RuntimePackage> installTargets, bool countsAsProblem)
    {
        Row = row;
        InstallTargets = installTargets;
        var family = RuntimeCatalog.Packages.First(p => p.FamilyId == row.Dll.FamilyId);
        PackageName = FamilyName(family);
        State32 = Cell(row.Status32);
        State64 = Cell(row.Status64);
        Status32Text = CellText(row.Status32, row.Dll.FamilyId);
        Status64Text = CellText(row.Status64, row.Dll.FamilyId);
        CountsAsProblem = countsAsProblem;
        bool missing = row.IsMissing;
        CanInstall = installTargets.Count > 0;
        ButtonText = missing ? Loc.T("Install") : Loc.T("Repair");
        InstallToolTip = CanInstall
            ? Loc.F("Installs {0} from {1}. No single DLL file is downloaded.", string.Join(", ", installTargets.Select(p => p.Name)),
                installTargets[0].Kind == RuntimeInstallKind.WindowsFeature ? Loc.T("Windows") : installTargets[0].Vendor)
            : family.UnavailableReason ?? Loc.T("Cannot be installed from here.");
    }

    public DllStatusRow Row { get; }

    public string Name => Row.Dll.Name;

    public string FamilyId => Row.Dll.FamilyId;

    public string PackageName { get; }

    public DllCellState State32 { get; }

    public DllCellState State64 { get; }

    public string Status32Text { get; }

    public string Status64Text { get; }

    public bool IsMissing => Row.IsMissing;

    /// <summary>False for DLLs whose absence is normal (Games for Windows Live, the 2003 runtime, NVIDIA-only files).</summary>
    public bool CountsAsProblem { get; }

    /// <summary>The packages the Install button installs (only the architectures that are missing, or all when repairing).</summary>
    public IReadOnlyList<RuntimePackage> InstallTargets { get; }

    public bool CanInstall { get; }

    public string ButtonText { get; }

    public string InstallToolTip { get; }

    /// <summary>A family's short name for the list ("Visual C++ 2010", "DirectX June 2010").</summary>
    public static string FamilyName(RuntimePackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var name = package.Name;
        int paren = name.IndexOf(" (", StringComparison.Ordinal);
        return package.FamilyId.StartsWith("vc", StringComparison.Ordinal) && paren > 0 ? name[..paren] : name;
    }

    private static DllCellState Cell(DllStatus status) => status switch
    {
        DllStatus.Present => DllCellState.Present,
        DllStatus.Missing => DllCellState.Missing,
        _ => DllCellState.NotMade,
    };

    private static string CellText(DllStatus status, string family) => status switch
    {
        DllStatus.Present => Loc.T("Installed"),
        DllStatus.Missing => Loc.T("Missing"),
        _ => family == "vc14" ? Loc.T("Not needed") : Loc.T("Not made"),
    };
}

/// <summary>One package in the packages section.</summary>
public sealed class RuntimePackageRow
{
    public RuntimePackageRow(PackageStatus status)
    {
        Status = status;
        var p = status.Package;
        StateText = status.State switch
        {
            PackageState.Installed => Loc.T("Installed"),
            PackageState.Partial => Loc.T("Partly missing"),
            PackageState.Missing => Loc.T("Missing"),
            _ => p.Kind switch
            {
                RuntimeInstallKind.PartOfWindows => Loc.T("Part of Windows"),
                RuntimeInstallKind.GraphicsDriver => Loc.T("With the driver"),
                RuntimeInstallKind.WindowsSettings => Loc.T("Not needed"),
                _ => Loc.T("Not available"),
            },
        };
        VersionText = status.InstalledVersions.Count > 0
            ? Loc.F("Installed: {0}", string.Join(", ", status.InstalledVersions))
            : status.TotalDlls > 0 && p.Kind is RuntimeInstallKind.Installer or RuntimeInstallKind.WindowsFeature
                ? Loc.F("{0} of {1} files found", status.PresentDlls, status.TotalDlls)
                : string.Empty;
        DetailText = p.Vendor + " · " + p.ArchText + (p.SourceHost is { } host ? " · " + host : string.Empty);
        ActionText = p.Kind switch
        {
            RuntimeInstallKind.WindowsFeature => status.State == PackageState.Installed ? Loc.T("Turned on") : Loc.T("Turn on"),
            RuntimeInstallKind.Installer => status.State == PackageState.Installed ? Loc.T("Repair") : Loc.T("Install"),
            RuntimeInstallKind.WindowsSettings => Loc.T("Open Settings"),
            RuntimeInstallKind.GraphicsDriver => Loc.T("Open Drivers"),
            _ => Loc.T("Install"),
        };
        CanAct = p.Kind switch
        {
            RuntimeInstallKind.Installer => p.CanInstall,
            RuntimeInstallKind.WindowsFeature => status.State != PackageState.Installed,
            RuntimeInstallKind.WindowsSettings => true,
            RuntimeInstallKind.GraphicsDriver => true,
            _ => false,
        };
        ActionToolTip = p.Kind switch
        {
            RuntimeInstallKind.Installer when p.CanInstall => Loc.F("Downloads the official installer from {0}, checks that it is signed by {1}, then installs it.",
                p.SourceHost ?? p.Vendor, p.ExpectedSigners.FirstOrDefault() ?? p.Vendor),
            RuntimeInstallKind.WindowsFeature => Loc.T("Switches on the Windows feature; Windows downloads the files itself."),
            _ => p.UnavailableReason ?? string.Empty,
        };
    }

    public PackageStatus Status { get; }

    public RuntimePackage Package => Status.Package;

    public string Name => Package.Name;

    public string Purpose => Package.Purpose;

    public string DetailText { get; }

    public PackageState State => Status.State;

    public string StateText { get; }

    public string VersionText { get; }

    public bool HasVersionText => VersionText.Length > 0;

    public string ActionText { get; }

    public bool CanAct { get; }

    public string ActionToolTip { get; }

    public bool HasNote => !CanAct && !string.IsNullOrEmpty(Package.UnavailableReason);

    /// <summary>True when the vendor has a web page about the package (opened in the browser).</summary>
    public bool HasVendorPage => Package.InfoUrl?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) == true;
}

/// <summary>One DLL in a game scan result.</summary>
public sealed class GameImportRow
{
    public GameImportRow(GameImport import)
    {
        Import = import;
        StatusText = GameScanRules.Describe(import);
        ArchText = import.Arch == DllArch.X64 ? Loc.T("64-bit") : Loc.T("32-bit");
        Severity = import.Resolution switch
        {
            ImportResolution.RuntimeMissing or ImportResolution.WindowsPartMissing => Core.Common.Severity.High,
            ImportResolution.NotFound => Core.Common.Severity.Medium,
            _ => Core.Common.Severity.Info,
        };
    }

    public GameImport Import { get; }

    public string DllName => Import.DllName;

    public string ArchText { get; }

    public string ImportedBy => Import.ImportedBy;

    public string StatusText { get; }

    public Core.Common.Severity Severity { get; }

    public bool IsProblem => Import.IsProblem;
}

/// <summary>An entry of the package filter drop-down.</summary>
public sealed record DllFilterOption(string Key, string Label)
{
    public override string ToString() => Label;
}
