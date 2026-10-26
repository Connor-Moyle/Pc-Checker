using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PcChecker.Core.Common;
using PcChecker.Core.Software;

namespace PcChecker.App.ViewModels;

/// <summary>Which programs the Software page lists.</summary>
public enum SoftwareSourceFilter
{
    [Description("Everything")]
    All,

    [Description("Programs")]
    Programs,

    [Description("Store apps")]
    StoreApps,

    [Description("Installed for this user only")]
    UserOnly,

    [Description("System components")]
    SystemComponents,
}

/// <summary>Sort order for the Software page list.</summary>
public enum SoftwareSortOrder
{
    [Description("Name")]
    Name,

    [Description("Size (largest first)")]
    Size,

    [Description("Date installed (newest first)")]
    Date,

    [Description("Publisher")]
    Publisher,
}

/// <summary>One program as shown in the list, with display-ready text and the uninstall verdict.</summary>
public sealed partial class ProgramRow : ObservableObject
{
    [ObservableProperty]
    private bool _isExpanded;

    public ProgramRow(InstalledProgram program, (bool CanUninstall, string Reason) verdict, string? bundledWithName, IReadOnlyList<BundleChildRow> children)
    {
        Program = program;
        CanUninstall = verdict.CanUninstall;
        UninstallReason = verdict.Reason;
        BundledWithName = bundledWithName;
        BundleChildren = children;
        Details = BuildDetails(program);
    }

    public InstalledProgram Program { get; }

    public string Id => Program.Id;
    public string Name => Program.Name;
    public string Version => Program.Version ?? "—";
    public string Publisher => Program.Publisher ?? "—";
    public string? Location => Program.InstallLocation;
    public bool HasLocation => !string.IsNullOrWhiteSpace(Program.InstallLocation);
    public bool HasAboutUrl => !string.IsNullOrWhiteSpace(Program.AboutUrl) || !string.IsNullOrWhiteSpace(Program.HelpLink);
    public string InstallDateText => Program.InstallDate?.ToString("d MMM yyyy", CultureInfo.CurrentCulture) ?? "—";
    public DateTime SortDate => Program.InstallDate ?? DateTime.MinValue;
    public long SortSize => Program.SizeBytes ?? -1;
    public string SizeText => Program.SizeBytes is { } b and > 0 ? ByteSize.Format(b) : "—";
    public bool IsProtected => Program.IsProtected;
    public string ProtectionReason => Program.ProtectionReason ?? "This program is protected.";
    public bool CanUninstall { get; }
    public string UninstallReason { get; }
    public bool IsStoreApp => Program.Source == SoftwareSource.StoreApp;
    public bool IsSystemComponent => Program.IsSystemComponent || Program.Source == SoftwareSource.SystemComponent;
    public bool IsUserInstall => Program.Source == SoftwareSource.User;
    public string SourceLabel => Program.Source switch
    {
        SoftwareSource.StoreApp => "Store",
        SoftwareSource.User => "User",
        SoftwareSource.Machine32 => "32-bit",
        SoftwareSource.SystemComponent => "System",
        _ => "64-bit",
    };

    public string SourceTooltip => Program.Source switch
    {
        SoftwareSource.StoreApp => "Installed from the Microsoft Store (or as a packaged app).",
        SoftwareSource.User => "Installed for the current Windows user only.",
        SoftwareSource.Machine32 => "32-bit program installed for all users.",
        SoftwareSource.SystemComponent => "Marked as a system component by its installer; normally hidden in Windows Settings.",
        _ => "64-bit program installed for all users.",
    };

    public string InstallerLabel => Program.Installer switch
    {
        InstallerKind.Msi => "Windows Installer (MSI)",
        InstallerKind.InnoSetup => "Inno Setup",
        InstallerKind.Nsis => "NSIS",
        InstallerKind.InstallShield => "InstallShield",
        InstallerKind.Squirrel => "Squirrel (self-updating app)",
        InstallerKind.StoreApp => "Store package",
        _ => "Unknown installer",
    };

    public bool SupportsQuietUninstall => Program.Installer is not InstallerKind.Unknown || !string.IsNullOrWhiteSpace(Program.QuietUninstallString);

    /// <summary>The parent this program was installed alongside, if any.</summary>
    public string? BundledWithName { get; }
    public bool IsBundledChild => BundledWithName is not null;
    public IReadOnlyList<BundleChildRow> BundleChildren { get; }
    public int BundledCount => BundleChildren.Count;
    public bool HasBundledChildren => BundleChildren.Count > 0;
    public string BundledBadgeText => BundledCount == 1 ? "1 installed alongside" : $"{BundledCount} installed alongside";

    public IReadOnlyList<KeyValuePair<string, string>> Details { get; }

    /// <summary>Text searched by the filter box.</summary>
    public string SearchText => string.Join(' ', Program.Name, Program.Publisher, Program.Version, Program.InstallLocation, Program.PackageFullName).ToLowerInvariant();

    private static IReadOnlyList<KeyValuePair<string, string>> BuildDetails(InstalledProgram p)
    {
        var list = new List<KeyValuePair<string, string>>();
        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                list.Add(new KeyValuePair<string, string>(key, value));
            }
        }

        Add("Installer", p.Installer switch
        {
            InstallerKind.Msi => "Windows Installer (MSI)",
            InstallerKind.InnoSetup => "Inno Setup",
            InstallerKind.Nsis => "NSIS",
            InstallerKind.InstallShield => "InstallShield",
            InstallerKind.Squirrel => "Squirrel",
            InstallerKind.StoreApp => "Store package",
            _ => "Unknown",
        });
        Add("Install folder", p.InstallLocation);
        Add("Uninstall command", p.UninstallString);
        Add("Quiet uninstall command", p.QuietUninstallString);
        Add("Registry key", p.RegistryKey);
        Add("Package", p.PackageFullName);
        Add("Icon", p.IconPath);
        Add("Help", p.HelpLink);
        Add("Website", p.AboutUrl);
        if (p.SizeBytes is { } bytes)
        {
            Add("Size on disk", ByteSize.Format(bytes) + (p.Source == SoftwareSource.StoreApp ? string.Empty : " (reported by the installer or measured)"));
        }

        return list;
    }
}

/// <summary>A program that was installed alongside another one.</summary>
public sealed record BundleChildRow(InstalledProgram Program, string Reason)
{
    public string Name => Program.Name;
    public string Publisher => Program.Publisher ?? "—";
    public string SizeText => Program.SizeBytes is { } b and > 0 ? ByteSize.Format(b) : "—";
    public bool IsRuntimeDependency => BundleDetector.IsRuntimeDependency(Program);
    public bool IsKnownExtra => BundleDetector.IsKnownBundledExtra(Program);
    public string Kind => IsKnownExtra ? "Known bundled extra" : IsRuntimeDependency ? "Runtime dependency (keep)" : "Installed together";
}

/// <summary>A bundle card: one parent and the extras that arrived with it.</summary>
public sealed class BundleRow
{
    public BundleRow(BundleGroup group, IReadOnlyList<BundleChildRow> children)
    {
        Group = group;
        Children = children;
        Removable = children.Where(c => !c.IsRuntimeDependency && !c.Program.IsProtected).ToList();
    }

    public BundleGroup Group { get; }
    public string ParentName => Group.Parent.Name;
    public string Reason => Group.Reason;
    public IReadOnlyList<BundleChildRow> Children { get; }
    public IReadOnlyList<BundleChildRow> Removable { get; }
    public bool HasRemovable => Removable.Count > 0;
    public string ChildrenText => string.Join(", ", Children.Select(c => c.Name));
    public string Title => $"{Group.Parent.Name} installed alongside {ChildrenText}";
    public string RemoveButtonText => Removable.Count == 1 ? "Uninstall extra" : $"Uninstall {Removable.Count} extras";
}
