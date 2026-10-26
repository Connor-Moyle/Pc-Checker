namespace PcChecker.Core.Software;

public enum SoftwareSource
{
    Machine64,
    Machine32,
    User,
    StoreApp,
    SystemComponent,
}

public enum InstallerKind
{
    Msi,
    InnoSetup,
    Nsis,
    InstallShield,
    Squirrel,
    StoreApp,
    Unknown,
}

public sealed record InstalledProgram(
    string Id,
    string Name,
    string? Version,
    string? Publisher,
    DateTime? InstallDate,
    string? InstallLocation,
    string? UninstallString,
    string? QuietUninstallString,
    long? SizeBytes,
    SoftwareSource Source,
    InstallerKind Installer,
    bool IsSystemComponent,
    bool IsProtected,
    string? ProtectionReason,
    string? RegistryKey,
    string? PackageFullName,
    string? IconPath,
    string? HelpLink,
    string? AboutUrl,
    string? BundledWithId,
    IReadOnlyList<string> BundledChildIds)
{
    /// <summary>Creates a minimal program entry; handy for tests and placeholders.</summary>
    public static InstalledProgram Create(string id, string name, string? publisher = null, DateTime? installDate = null,
        string? installLocation = null, string? uninstallString = null, SoftwareSource source = SoftwareSource.Machine64,
        InstallerKind installer = InstallerKind.Unknown, string? quietUninstallString = null, string? packageFullName = null,
        string? version = null, long? sizeBytes = null) =>
        new(id, name, version, publisher, installDate, installLocation, uninstallString, quietUninstallString, sizeBytes, source,
            installer, false, false, null, null, packageFullName, null, null, null, null, []);
}

public sealed record BundleGroup(InstalledProgram Parent, IReadOnlyList<InstalledProgram> Children, string Reason);

public sealed record SoftwareInventory(
    IReadOnlyList<InstalledProgram> Programs,
    IReadOnlyList<BundleGroup> Bundles,
    DateTimeOffset Captured,
    IReadOnlyList<string> Warnings)
{
    public static SoftwareInventory Empty(string? warning = null) => new([], [], DateTimeOffset.Now, warning is null ? [] : [warning]);
}
