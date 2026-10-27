using PcChecker.Core.Optimization;

namespace PcChecker.Core.RegistryHealth;

/// <summary>The kinds of leftover registry entries the Registry page checks.</summary>
public enum RegistryIssueCategory
{
    UninstallEntries,
    AppPaths,
    SharedDlls,
    FileAssociations,
    StartupEntries,
    ComObjects,
    MuiCache,
    Fonts,
    HelpFiles,
    InstallerFolders,
    ServicesMissingImage,
    ContextMenuHandlers,
}

/// <summary>
/// One registry entry that points at something that no longer exists. <paramref name="FixDeletesKey"/> true means the
/// fix removes the whole key at <paramref name="KeyPath"/>; false means it removes only <paramref name="ValueName"/>
/// (an empty value name is the key's default value). <paramref name="Hive"/> is HKLM, HKCU or HKCR. Key paths are
/// 64-bit view paths: entries of 32-bit programs carry <c>WOW6432Node</c> in the path.
/// </summary>
public sealed record RegistryIssue(
    string Id,
    RegistryIssueCategory Category,
    string Hive,
    string KeyPath,
    string? ValueName,
    string Problem,
    string? MissingTarget,
    RiskLevel Risk,
    bool SafeToFix,
    bool FixDeletesKey,
    bool AdvancedOnly)
{
    /// <summary>The key in the familiar regedit form, e.g. <c>HKEY_CURRENT_USER\Software\…</c>.</summary>
    public string FullKeyPath => RegistryPathTools.FullKeyName(Hive, KeyPath);
}

/// <summary>Outcome of a read-only registry scan.</summary>
public sealed record RegistryScanResult(
    IReadOnlyList<RegistryIssue> Issues,
    IReadOnlyDictionary<RegistryIssueCategory, int> CountsByCategory,
    int KeysScanned,
    TimeSpan Elapsed,
    IReadOnlyList<string> Warnings,
    DateTimeOffset Captured);

/// <summary>A .reg backup written before a fix. <paramref name="Id"/> is the file name without extension.</summary>
public sealed record RegistryBackup(string Id, DateTimeOffset Created, string FilePath, int IssueCount, string Summary);

/// <summary>Outcome of fixing a batch of issues. <paramref name="BackupId"/> is null when nothing was backed up.</summary>
public sealed record RegistryFixResult(int Fixed, int Skipped, int Failed, string? BackupId, IReadOnlyList<string> Messages);

/// <summary>Plain-English description and fix rules of one category (see <see cref="RegistryIssueRules"/>).</summary>
public sealed record RegistryCategoryInfo(
    RegistryIssueCategory Category,
    string Name,
    string Description,
    string WhereItLooks,
    RiskLevel Risk,
    bool SafeToFix,
    bool AdvancedOnly,
    bool FixDeletesKey);
