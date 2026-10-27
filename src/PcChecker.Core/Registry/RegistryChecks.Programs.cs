namespace PcChecker.Core.RegistryHealth;

/// <summary>Checks that look at program registrations: uninstall entries, file types, COM components and menu handlers.</summary>
internal sealed partial class RegistryChecks
{
    private const string FileExtsPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts";

    private static readonly (string Hive, string Path)[] ClassesRoots =
    [
        ("HKCU", @"Software\Classes"),
        ("HKLM", @"SOFTWARE\Classes"),
    ];

    private static readonly (string Hive, string Path)[] ClsidRoots =
    [
        ("HKCU", @"Software\Classes\CLSID"),
        ("HKLM", @"SOFTWARE\Classes\CLSID"),
        ("HKLM", @"SOFTWARE\WOW6432Node\Classes\CLSID"),
    ];

    private static readonly string[] ContextMenuParents = ["*", "Directory", @"Directory\Background", "Folder", "Drive", "AllFilesystemObjects"];

    private static readonly (string Hive, string Path)[] ContextMenuRoots =
        ClassesRoots.SelectMany(r => ContextMenuParents.Select(p => (r.Hive, $@"{r.Path}\{p}\shellex\ContextMenuHandlers"))).ToArray();

    /// <summary>Classes sub-keys that are not file types (ProgIDs) and are never examined as such.</summary>
    private static readonly HashSet<string> NonProgIdKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "*", "AllFilesystemObjects", "AppID", "Applications", "AppX", "CLSID", "Component Categories", "Directory", "Drive",
        "Folder", "Installer", "Interface", "Local Settings", "MIME", "Media Type", "PackagedCom", "Protocols",
        "SystemFileAssociations", "TypeLib", "Unknown", "WOW6432Node", "DesktopBackground", "Network", "Printers",
    };

    private RegistryIssue? EvaluateUninstall(RegistryCandidate c)
    {
        using var key = _access.Open(c.Hive, c.KeyPath);
        if (key is null)
        {
            return null;
        }

        // Entries without a name are not shown in Installed apps, and system components are Windows' own business.
        string? name = RegistryAccess.Text(key, "DisplayName");
        if (string.IsNullOrWhiteSpace(name) || RegistryAccess.DWord(key, "SystemComponent") == 1)
        {
            return null;
        }

        string? publisher = RegistryAccess.Text(key, "Publisher");
        string? location = RegistryAccess.Text(key, "InstallLocation") is { } l ? Expand(l.Trim('"')).TrimEnd('\\') : null;
        if (string.IsNullOrWhiteSpace(location))
        {
            location = null;
        }

        if (RegistryAccess.DWord(key, "WindowsInstaller") == 1)
        {
            // Windows Installer entries: only when the install folder AND the cached installer package are both gone.
            if (location is null || !IsReportableMissing(c.Category, location, publisher) || MsiPackageMissing(Leaf(c.KeyPath)) != true)
            {
                return null;
            }

            return RegistryIssueRules.Create(c.Category, c.Hive, c.KeyPath, null,
                $"{Quote(name)} is listed as installed, but its install folder and its Windows Installer package are gone.", location);
        }

        var uninstaller = PathFrom(RegistryAccess.Text(key, "UninstallString"));
        if (!IsReportableMissing(c.Category, uninstaller, publisher))
        {
            return null;
        }

        string uninstallerPath = Expand(uninstaller!);
        if (location is not null)
        {
            if (location.Length <= 3 || !IsReportableMissing(c.Category, location, publisher))
            {
                return null;
            }

            return RegistryIssueRules.Create(c.Category, c.Hive, c.KeyPath, null,
                $"{Quote(name)} is still listed in Installed apps, but its uninstaller and install folder are gone.", uninstallerPath);
        }

        // No InstallLocation. Many setups keep the uninstaller in its own cache folder (WiX Package Cache\{guid},
        // InstallShield Installation Information\{guid}), which disk cleaners remove while the program stays installed,
        // so that folder proves nothing. An icon elsewhere that is gone too does; without one the entry is only reported.
        var icon = RegistryPathTools.IconOutsideUninstallerFolder(PathFrom(RegistryAccess.Text(key, "DisplayIcon")), uninstallerPath);
        if (icon is not null)
        {
            if (!IsReportableMissing(c.Category, icon, publisher))
            {
                return null;
            }

            return RegistryIssueRules.Create(c.Category, c.Hive, c.KeyPath, null,
                $"{Quote(name)} is still listed in Installed apps, but its uninstaller and program files are gone.", uninstallerPath);
        }

        string? folder = RegistryPathTools.DirectoryName(uninstallerPath);
        if (folder is null || folder.Length <= 3 || !IsReportableMissing(c.Category, folder, publisher))
        {
            return null;
        }

        return RegistryIssueRules.Create(c.Category, c.Hive, c.KeyPath, null,
            $"{Quote(name)} is still listed in Installed apps, but its uninstaller is gone. The entry does not say where the program " +
            "was installed, so PC Checker cannot tell whether it is still there and only reports it. If the program is gone, " +
            "remove the entry from Settings > Apps > Installed apps.", uninstallerPath) with { SafeToFix = false };
    }

    /// <summary>True when Windows Installer has no usable cached package for the product; null when that cannot be told.</summary>
    private bool? MsiPackageMissing(string productCode)
    {
        var packed = RegistryPathTools.PackMsiProductCode(productCode);
        if (packed is null)
        {
            return null;
        }

        const string userData = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData";
        bool registered = false;
        using (var root = _access.Open("HKLM", userData))
        {
            if (root is not null)
            {
                foreach (var sid in RegistryAccess.SubKeyNames(root))
                {
                    using var props = _access.Open("HKLM", $@"{userData}\{sid}\Products\{packed}\InstallProperties");
                    if (props is null)
                    {
                        continue;
                    }

                    registered = true;
                    var package = RegistryAccess.Text(props, "LocalPackage");
                    if (string.IsNullOrWhiteSpace(package))
                    {
                        return null;
                    }

                    var state = RegistryPathTools.Evaluate(package, _probe);
                    return state switch
                    {
                        TargetState.Missing => true,
                        TargetState.Present => false,
                        _ => null,
                    };
                }
            }
            else
            {
                return null;
            }
        }

        // Not in UserData: missing only if the product is not advertised either.
        return !registered && !_access.KeyExists("HKLM", $@"SOFTWARE\Classes\Installer\Products\{packed}") &&
               !_access.KeyExists("HKCU", $@"Software\Microsoft\Installer\Products\{packed}")
            ? true
            : null;
    }

    private IEnumerable<RegistryCandidate> FileAssociationCandidates(CancellationToken cancellationToken)
    {
        foreach (var (hive, path) in ClassesRoots)
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
                if (!name.StartsWith('.') && !NonProgIdKeys.Contains(name) && !name.StartsWith('{'))
                {
                    yield return new RegistryCandidate(RegistryIssueCategory.FileAssociations, hive, path + "\\" + name, null);
                }
            }

            using var applications = _access.Open(hive, path + @"\Applications");
            if (applications is not null)
            {
                foreach (var app in RegistryAccess.SubKeyNames(applications))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return new RegistryCandidate(RegistryIssueCategory.FileAssociations, hive, $@"{path}\Applications\{app}", null);
                }
            }
        }

        string[] extensions;
        using (var exts = _access.Open("HKCU", FileExtsPath))
        {
            extensions = exts is null ? [] : RegistryAccess.SubKeyNames(exts);
        }

        foreach (var ext in extensions)
        {
            string listPath = $@"{FileExtsPath}\{ext}\OpenWithList";
            using var list = _access.Open("HKCU", listPath);
            if (list is null)
            {
                continue;
            }

            foreach (var value in RegistryAccess.ValueNames(list).Where(v => v.Length > 0 && !v.Equals("MRUList", StringComparison.OrdinalIgnoreCase)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new RegistryCandidate(RegistryIssueCategory.FileAssociations, "HKCU", listPath, value);
            }
        }
    }

    private RegistryIssue? EvaluateFileAssociation(RegistryCandidate c) =>
        c.ValueName is null ? EvaluateProgId(c) : EvaluateOpenWithEntry(c);

    private RegistryIssue? EvaluateProgId(RegistryCandidate c)
    {
        var program = OpenCommandTarget(c.Hive, c.KeyPath);
        if (!IsReportableMissing(c.Category, program))
        {
            return null;
        }

        string name = Leaf(c.KeyPath);
        bool isApplication = c.KeyPath.Contains(@"\Applications\", StringComparison.OrdinalIgnoreCase);
        if (!isApplication && ReferencedProgIds().Contains(name))
        {
            // Still the default for some extension: leave it, removing it could change what opens those files.
            return null;
        }

        string file = RegistryPathTools.FileName(program!);
        string problem = isApplication
            ? $"{Quote(name)} is still offered in Open with, but {file} is no longer installed."
            : $"The file type {Quote(name)} opens {file}, which is no longer installed, and no file extension uses it any more.";
        return RegistryIssueRules.Create(c.Category, c.Hive, c.KeyPath, null, problem, Expand(program!));
    }

    private RegistryIssue? EvaluateOpenWithEntry(RegistryCandidate c)
    {
        using var list = _access.Open(c.Hive, c.KeyPath);
        var app = list is null ? null : RegistryAccess.Text(list, c.ValueName);
        if (string.IsNullOrWhiteSpace(app) || app.Contains('\\'))
        {
            return null;
        }

        // The list stores bare names such as "editor.exe"; only flag them when the program's own registration points
        // at a missing file. Names Windows resolves some other way are left alone.
        string? program = null;
        foreach (var (hive, path) in ClassesRoots)
        {
            program = OpenCommandTarget(hive, $@"{path}\Applications\{app}");
            if (program is not null)
            {
                break;
            }
        }

        if (program is null)
        {
            foreach (var (hive, path) in AppPathRoots)
            {
                using var appPath = _access.Open(hive, $@"{path}\{app}");
                program = PathFrom(DefaultOf(appPath));
                if (program is not null)
                {
                    break;
                }
            }
        }

        if (!IsReportableMissing(c.Category, program))
        {
            return null;
        }

        string ext = RegistryPathTools.FileName(RegistryPathTools.DirectoryName(c.KeyPath) ?? c.KeyPath);
        return RegistryIssueRules.Create(c.Category, c.Hive, c.KeyPath, c.ValueName,
            $"The Open with list for {ext} files still offers {app}, which is no longer installed.", Expand(program!));
    }

    private string? OpenCommandTarget(string hive, string classKey)
    {
        using var command = _access.Open(hive, classKey + @"\shell\open\command");
        return PathFrom(DefaultOf(command));
    }

    /// <summary>ProgIDs that some extension still uses (default value, OpenWithProgids or the user's choice).</summary>
    private HashSet<string> ReferencedProgIds()
    {
        if (_referencedProgIds is not null)
        {
            return _referencedProgIds;
        }

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (hive, path) in ClassesRoots)
        {
            using var root = _access.Open(hive, path);
            if (root is null)
            {
                continue;
            }

            foreach (var ext in RegistryAccess.SubKeyNames(root).Where(n => n.StartsWith('.')))
            {
                using var extKey = _access.OpenChild(root, ext);
                if (extKey is null)
                {
                    continue;
                }

                AddIfPresent(set, DefaultOf(extKey));
                using var progIds = _access.OpenChild(extKey, "OpenWithProgids");
                if (progIds is not null)
                {
                    set.UnionWith(RegistryAccess.ValueNames(progIds).Where(v => v.Length > 0));
                }
            }
        }

        using (var exts = _access.Open("HKCU", FileExtsPath))
        {
            foreach (var ext in exts is null ? [] : RegistryAccess.SubKeyNames(exts))
            {
                using var choice = _access.Open("HKCU", $@"{FileExtsPath}\{ext}\UserChoice");
                AddIfPresent(set, choice is null ? null : RegistryAccess.Text(choice, "ProgId"));
                using var progIds = _access.Open("HKCU", $@"{FileExtsPath}\{ext}\OpenWithProgids");
                if (progIds is not null)
                {
                    set.UnionWith(RegistryAccess.ValueNames(progIds).Where(v => v.Length > 0));
                }
            }
        }

        return _referencedProgIds = set;
    }

    private static void AddIfPresent(HashSet<string> set, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            set.Add(value.Trim());
        }
    }

    private RegistryIssue? EvaluateCom(RegistryCandidate c)
    {
        string clsid = Leaf(c.KeyPath);
        if (!Guid.TryParse(clsid, out _))
        {
            return null;
        }

        using var key = _access.Open(c.Hive, c.KeyPath);
        if (key is null)
        {
            return null;
        }

        var server = MissingServer(key);
        if (server is null)
        {
            return null;
        }

        string? name = DefaultOf(key);
        string label = string.IsNullOrWhiteSpace(name) ? clsid : $"{Quote(name)} {clsid}";
        return RegistryIssueRules.Create(c.Category, c.Hive, c.KeyPath, null,
            $"The component {label} is registered, but its file {RegistryPathTools.FileName(server)} is gone.", server);
    }

    /// <summary>
    /// The server file of a CLSID when every server it lists (InprocServer32, LocalServer32) is provably missing and
    /// none of them is a Microsoft component; otherwise null.
    /// </summary>
    private string? MissingServer(Microsoft.Win32.RegistryKey clsidKey)
    {
        string? missing = null;
        foreach (var serverKey in new[] { "InprocServer32", "LocalServer32" })
        {
            using var server = _access.OpenChild(clsidKey, serverKey);
            var path = PathFrom(DefaultOf(server));
            if (path is null)
            {
                continue;
            }

            if (!IsReportableMissing(RegistryIssueCategory.ComObjects, path))
            {
                return null;
            }

            missing ??= Expand(path);
        }

        return missing;
    }

    private RegistryIssue? EvaluateContextMenu(RegistryCandidate c)
    {
        using var handler = _access.Open(c.Hive, c.KeyPath);
        if (handler is null)
        {
            return null;
        }

        string name = Leaf(c.KeyPath);
        string? clsid = DefaultOf(handler);
        if (clsid is null || !Guid.TryParse(clsid, out _))
        {
            clsid = Guid.TryParse(name, out _) ? name : null;
        }

        if (clsid is null)
        {
            return null;
        }

        // Look the CLSID up where Explorer would: per-user first, then machine-wide 64-bit and 32-bit.
        foreach (var (hive, root) in ClsidRoots)
        {
            using var clsidKey = _access.Open(hive, $@"{root}\{clsid}");
            if (clsidKey is null)
            {
                continue;
            }

            var server = MissingServer(clsidKey);
            if (server is null)
            {
                return null;
            }

            string where = c.KeyPath.Contains(@"\Directory\Background\", StringComparison.OrdinalIgnoreCase) ? "folder background"
                : c.KeyPath.Contains(@"\Directory\", StringComparison.OrdinalIgnoreCase) || c.KeyPath.Contains(@"\Folder\", StringComparison.OrdinalIgnoreCase) ? "folder"
                : c.KeyPath.Contains(@"\Drive\", StringComparison.OrdinalIgnoreCase) ? "drive"
                : "file";
            return RegistryIssueRules.Create(c.Category, c.Hive, c.KeyPath, null,
                $"The {where} right-click menu entry {Quote(name)} belongs to a program whose file {RegistryPathTools.FileName(server)} is gone.", server);
        }

        return null;
    }
}
