using Microsoft.Win32;

namespace PcChecker.Core.RegistryHealth;

/// <summary>A key or value one check looks at. <see cref="ValueName"/> null means the check looks at the whole key.</summary>
internal sealed record RegistryCandidate(RegistryIssueCategory Category, string Hive, string KeyPath, string? ValueName);

/// <summary>
/// The twelve read-only checks of the Windows registry service. Each category enumerates candidates and evaluates them
/// one by one, so the fixer can evaluate a single candidate again right before deleting it. Every lookup is guarded:
/// keys Windows refuses to open are counted in <see cref="RegistryAccess.AccessDenied"/> and skipped.
/// </summary>
internal sealed partial class RegistryChecks
{
    private const string CurrentVersion = @"SOFTWARE\Microsoft\Windows\CurrentVersion";
    private const string Wow = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion";
    private const string MuiCachePath = @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache";
    private const string FontsPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts";
    private const string ServicesPath = @"SYSTEM\CurrentControlSet\Services";

    private static readonly (string Hive, string Path)[] UninstallRoots =
    [
        ("HKLM", CurrentVersion + @"\Uninstall"),
        ("HKLM", Wow + @"\Uninstall"),
        ("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
    ];

    private static readonly (string Hive, string Path)[] AppPathRoots =
    [
        ("HKLM", CurrentVersion + @"\App Paths"),
        ("HKLM", Wow + @"\App Paths"),
        ("HKCU", @"Software\Microsoft\Windows\CurrentVersion\App Paths"),
    ];

    private static readonly (string Hive, string Path)[] SharedDllRoots =
    [
        ("HKLM", CurrentVersion + @"\SharedDLLs"),
        ("HKLM", Wow + @"\SharedDLLs"),
    ];

    private static readonly (string Hive, string Path)[] RunRoots =
    [
        ("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Run"),
        ("HKCU", @"Software\Microsoft\Windows\CurrentVersion\RunOnce"),
        ("HKLM", CurrentVersion + @"\Run"),
        ("HKLM", CurrentVersion + @"\RunOnce"),
        ("HKLM", Wow + @"\Run"),
        ("HKLM", Wow + @"\RunOnce"),
    ];

    private static readonly (string Hive, string Path)[] HelpRoots =
    [
        ("HKLM", @"SOFTWARE\Microsoft\Windows\Help"),
        ("HKLM", @"SOFTWARE\Microsoft\Windows\HTML Help"),
        ("HKLM", @"SOFTWARE\WOW6432Node\Microsoft\Windows\Help"),
        ("HKLM", @"SOFTWARE\WOW6432Node\Microsoft\Windows\HTML Help"),
    ];

    private readonly RegistryAccess _access;
    private readonly PathProbe _probe;
    private readonly string _windows;
    private HashSet<string>? _referencedProgIds;

    public RegistryChecks(RegistryAccess access, PathProbe probe)
    {
        _access = access;
        _probe = probe;
        _windows = probe.WindowsDirectory;
    }

    /// <summary>Every candidate of one category. Lazily enumerated; honours cancellation between keys.</summary>
    public IEnumerable<RegistryCandidate> Enumerate(RegistryIssueCategory category, CancellationToken cancellationToken) => category switch
    {
        RegistryIssueCategory.UninstallEntries => SubKeys(category, UninstallRoots, cancellationToken),
        RegistryIssueCategory.AppPaths => SubKeys(category, AppPathRoots, cancellationToken),
        RegistryIssueCategory.SharedDlls => Values(category, SharedDllRoots, cancellationToken),
        RegistryIssueCategory.FileAssociations => FileAssociationCandidates(cancellationToken),
        RegistryIssueCategory.StartupEntries => Values(category, RunRoots, cancellationToken),
        RegistryIssueCategory.ComObjects => SubKeys(category, ClsidRoots, cancellationToken),
        RegistryIssueCategory.MuiCache => Values(category, [("HKCU", MuiCachePath)], cancellationToken),
        RegistryIssueCategory.Fonts => Values(category, [("HKLM", FontsPath), ("HKCU", @"Software\Microsoft\Windows NT\CurrentVersion\Fonts")], cancellationToken),
        RegistryIssueCategory.HelpFiles => Values(category, HelpRoots, cancellationToken),
        RegistryIssueCategory.InstallerFolders => Values(category, [("HKLM", CurrentVersion + @"\Installer\Folders")], cancellationToken),
        RegistryIssueCategory.ServicesMissingImage => SubKeys(category, [("HKLM", ServicesPath)], cancellationToken),
        RegistryIssueCategory.ContextMenuHandlers => SubKeys(category, ContextMenuRoots, cancellationToken),
        _ => [],
    };

    /// <summary>Evaluates one candidate; returns an issue only when it provably points at something missing.</summary>
    public RegistryIssue? Evaluate(RegistryCandidate candidate) => candidate.Category switch
    {
        RegistryIssueCategory.UninstallEntries => EvaluateUninstall(candidate),
        RegistryIssueCategory.AppPaths => EvaluateAppPath(candidate),
        RegistryIssueCategory.SharedDlls => EvaluateSharedDll(candidate),
        RegistryIssueCategory.FileAssociations => EvaluateFileAssociation(candidate),
        RegistryIssueCategory.StartupEntries => EvaluateRun(candidate),
        RegistryIssueCategory.ComObjects => EvaluateCom(candidate),
        RegistryIssueCategory.MuiCache => EvaluateMuiCache(candidate),
        RegistryIssueCategory.Fonts => EvaluateFont(candidate),
        RegistryIssueCategory.HelpFiles => EvaluateHelp(candidate),
        RegistryIssueCategory.InstallerFolders => EvaluateInstallerFolder(candidate),
        RegistryIssueCategory.ServicesMissingImage => EvaluateService(candidate),
        RegistryIssueCategory.ContextMenuHandlers => EvaluateContextMenu(candidate),
        _ => null,
    };

    private IEnumerable<RegistryCandidate> SubKeys(RegistryIssueCategory category, IEnumerable<(string Hive, string Path)> roots, CancellationToken cancellationToken)
    {
        foreach (var (hive, path) in roots)
        {
            string[] names;
            using (var root = _access.Open(hive, path))
            {
                if (root is null)
                {
                    continue;
                }

                names = RegistryAccess.SubKeyNames(root);
            }

            foreach (var name in names)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new RegistryCandidate(category, hive, path + "\\" + name, null);
            }
        }
    }

    private IEnumerable<RegistryCandidate> Values(RegistryIssueCategory category, IEnumerable<(string Hive, string Path)> roots, CancellationToken cancellationToken)
    {
        foreach (var (hive, path) in roots)
        {
            string[] names;
            using (var root = _access.Open(hive, path))
            {
                if (root is null)
                {
                    continue;
                }

                names = RegistryAccess.ValueNames(root);
            }

            foreach (var name in names.Where(n => n.Length > 0))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new RegistryCandidate(category, hive, path, name);
            }
        }
    }

    private string Leaf(string keyPath) => RegistryPathTools.FileName(keyPath);

    /// <summary>Missing and not a Microsoft/Windows component (unless the category allows those).</summary>
    private bool IsReportableMissing(RegistryIssueCategory category, string? path, string? publisher = null) =>
        path is not null &&
        RegistryPathTools.Evaluate(path, _probe) == TargetState.Missing &&
        (RegistryIssueRules.AllowsMicrosoftOwnedTargets(category) || !RegistryIssueRules.LooksMicrosoftOwned(Expand(path), publisher, _windows));

    private string Expand(string path) => RegistryPathTools.Expand(path, _probe.Environment);

    /// <summary>The file a command line refers to, with unquoted paths containing spaces resolved the way Windows reads them.</summary>
    private string? PathFrom(string? value) => RegistryPathTools.ResolveCommandPath(value, _probe);

    private static string Quote(string text) => "“" + text + "”";

    private RegistryIssue? EvaluateAppPath(RegistryCandidate c)
    {
        using var key = _access.Open(c.Hive, c.KeyPath);
        if (key is null)
        {
            return null;
        }

        var target = PathFrom(RegistryAccess.Text(key, null));
        if (!IsReportableMissing(c.Category, target))
        {
            return null;
        }

        string name = Leaf(c.KeyPath);
        return RegistryIssueRules.Create(c.Category, c.Hive, c.KeyPath, null,
            $"{Quote(name)} can still be started from Run (Win+R), but it points at a program that is no longer installed.", Expand(target!));
    }

    private RegistryIssue? EvaluateSharedDll(RegistryCandidate c)
    {
        if (!_access.KeyExists(c.Hive, c.KeyPath) || !ValueExists(c))
        {
            return null;
        }

        // The value name is a complete path (files and sometimes folders, often with spaces): never a command line.
        var target = RegistryPathTools.PlainPath(c.ValueName, _probe.Environment);
        if (!IsReportableMissing(c.Category, target))
        {
            return null;
        }

        return RegistryIssueRules.Create(c.Category, c.Hive, c.KeyPath, c.ValueName,
            $"Installers still keep a usage counter for {RegistryPathTools.FileName(target!)}, which was removed.", target);
    }

    private RegistryIssue? EvaluateRun(RegistryCandidate c)
    {
        using var key = _access.Open(c.Hive, c.KeyPath);
        if (key is null)
        {
            return null;
        }

        var command = RegistryAccess.Text(key, c.ValueName);
        var target = PathFrom(command);
        if (!IsReportableMissing(c.Category, target))
        {
            return null;
        }

        string when = c.KeyPath.EndsWith("RunOnce", StringComparison.OrdinalIgnoreCase) ? "the next time you sign in" : "every time you sign in";
        return RegistryIssueRules.Create(c.Category, c.Hive, c.KeyPath, c.ValueName,
            $"{Quote(c.ValueName!)} is set to start {when}, but {RegistryPathTools.FileName(target!)} no longer exists.", Expand(target!));
    }

    private RegistryIssue? EvaluateMuiCache(RegistryCandidate c)
    {
        if (!ValueExists(c))
        {
            return null;
        }

        var target = RegistryPathTools.MuiCacheTarget(c.ValueName);
        if (!IsReportableMissing(c.Category, target))
        {
            return null;
        }

        return RegistryIssueRules.Create(c.Category, c.Hive, c.KeyPath, c.ValueName,
            $"Windows still remembers the name of {RegistryPathTools.FileName(target!)}, which is no longer on this PC.", target);
    }

    private RegistryIssue? EvaluateFont(RegistryCandidate c)
    {
        using var key = _access.Open(c.Hive, c.KeyPath);
        var file = key is null ? null : RegistryAccess.Text(key, c.ValueName);
        if (string.IsNullOrWhiteSpace(file))
        {
            return null;
        }

        file = Expand(file);
        string? target;
        if (RegistryPathTools.HasDriveRoot(file) || RegistryPathTools.IsUnc(file))
        {
            target = file;
        }
        else if (!file.Contains('\\'))
        {
            // A bare file name lives in the Windows Fonts folder or, for per-user fonts, in the user's own Fonts folder.
            target = _windows + @"\Fonts\" + file;
            var local = _probe.Environment("LOCALAPPDATA");
            if (!string.IsNullOrWhiteSpace(local) && RegistryPathTools.Evaluate(local.TrimEnd('\\') + @"\Microsoft\Windows\Fonts\" + file, _probe) != TargetState.Missing)
            {
                return null;
            }
        }
        else
        {
            return null;
        }

        if (!IsReportableMissing(c.Category, target))
        {
            return null;
        }

        return RegistryIssueRules.Create(c.Category, c.Hive, c.KeyPath, c.ValueName,
            $"The font {Quote(c.ValueName!)} is listed as installed, but its file {RegistryPathTools.FileName(target)} is missing.", target);
    }

    private RegistryIssue? EvaluateHelp(RegistryCandidate c)
    {
        using var key = _access.Open(c.Hive, c.KeyPath);
        var folder = key is null ? null : RegistryAccess.Text(key, c.ValueName);
        if (string.IsNullOrWhiteSpace(folder) || c.ValueName is null || c.ValueName.Contains('\\'))
        {
            return null;
        }

        string target = Expand(folder).TrimEnd('\\') + "\\" + c.ValueName;
        if (!IsReportableMissing(c.Category, target))
        {
            return null;
        }

        return RegistryIssueRules.Create(c.Category, c.Hive, c.KeyPath, c.ValueName,
            $"The help file {c.ValueName} is registered, but it is no longer in {Expand(folder).TrimEnd('\\')}.", target);
    }

    private RegistryIssue? EvaluateInstallerFolder(RegistryCandidate c)
    {
        if (!ValueExists(c) || c.ValueName is null || !RegistryPathTools.HasDriveRoot(c.ValueName))
        {
            return null;
        }

        if (!IsReportableMissing(c.Category, c.ValueName))
        {
            return null;
        }

        return RegistryIssueRules.Create(c.Category, c.Hive, c.KeyPath, c.ValueName,
            $"Windows Installer still records the folder {c.ValueName.TrimEnd('\\')}, which no longer exists.", c.ValueName);
    }

    private RegistryIssue? EvaluateService(RegistryCandidate c)
    {
        using var key = _access.Open(c.Hive, c.KeyPath);
        if (key is null)
        {
            return null;
        }

        // Kernel and file-system drivers (type 1, 2, 4, 8) are left to Windows; only Win32 services (0x10/0x20) are checked.
        int type = RegistryAccess.DWord(key, "Type") ?? 0;
        if ((type & 0x30) == 0)
        {
            return null;
        }

        var image = RegistryPathTools.ResolveServiceImagePath(RegistryAccess.Text(key, "ImagePath"), _probe.Environment);
        if (!IsReportableMissing(c.Category, image))
        {
            return null;
        }

        string name = Leaf(c.KeyPath);
        string? display = RegistryAccess.Text(key, "DisplayName");
        if (string.IsNullOrWhiteSpace(display) || display.StartsWith('@'))
        {
            display = name;
        }

        return RegistryIssueRules.Create(c.Category, c.Hive, c.KeyPath, null,
            $"The service {Quote(display)} ({name}) points at {RegistryPathTools.FileName(image!)}, which no longer exists. " +
            "Uninstall the program it belonged to, or use that vendor's removal tool; PC Checker does not edit services.", image);
    }

    private bool ValueExists(RegistryCandidate c)
    {
        using var key = _access.Open(c.Hive, c.KeyPath);
        return key is not null && c.ValueName is not null &&
               RegistryAccess.ValueNames(key).Contains(c.ValueName, StringComparer.OrdinalIgnoreCase);
    }

    private static string? DefaultOf(RegistryKey? key) => key is null ? null : RegistryAccess.Text(key, null);
}
