namespace PcChecker.Core.GameRuntimes;

/// <summary>
/// Decides, for every DLL a game's programs import, whether it is there: in the game's own folder, a Windows API set,
/// a Windows DLL, an installed runtime, or missing (with the package that fixes it). Pure, so the rules are tested.
/// </summary>
public static class GameScanRules
{
    /// <summary>Most program files read in one scan.</summary>
    public const int MaxFiles = 300;

    /// <summary>
    /// True for API set names (api-ms-win-*, ext-ms-*). Windows maps them to real DLLs itself; they never exist as
    /// files on their own, so they are never "missing".
    /// </summary>
    public static bool IsApiSet(string? dllName)
    {
        if (string.IsNullOrWhiteSpace(dllName))
        {
            return false;
        }

        var n = dllName.Trim();
        return n.StartsWith("api-ms-win-", StringComparison.OrdinalIgnoreCase) || n.StartsWith("ext-ms-", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Adds ".dll" to an import name without an extension (the loader does the same).</summary>
    public static string NormalizeName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var n = name.Trim();
        return Path.HasExtension(n) ? n : n + ".dll";
    }

    /// <summary>
    /// Resolves one import. <paramref name="gameFiles"/> holds the file names in the game folder and its subfolders;
    /// <paramref name="existsInSystem"/> says whether the name exists in System32 (64-bit) or SysWOW64 (32-bit);
    /// <paramref name="isInstalled"/> whether a catalog DLL is installed for the architecture.
    /// </summary>
    public static (ImportResolution Resolution, string? PackageId) Resolve(string dllName, DllArch arch, ISet<string> gameFiles,
        Func<string, DllArch, bool> existsInSystem, Func<KnownDll, DllArch, bool> isInstalled)
    {
        ArgumentNullException.ThrowIfNull(dllName);
        ArgumentNullException.ThrowIfNull(gameFiles);
        ArgumentNullException.ThrowIfNull(existsInSystem);
        ArgumentNullException.ThrowIfNull(isInstalled);
        var name = NormalizeName(dllName);

        if (IsApiSet(name))
        {
            return (ImportResolution.WindowsApiSet, null);
        }

        if (gameFiles.Contains(name))
        {
            return (ImportResolution.InGameFolder, null);
        }

        if (RuntimeCatalog.Find(name) is { } known)
        {
            var package = RuntimeCatalog.PackageFor(known.FamilyId, arch);
            bool installed = known.Has(arch) ? isInstalled(known, arch) : existsInSystem(name, arch);
            if (installed)
            {
                return (known.FamilyId == "windows" ? ImportResolution.Windows : ImportResolution.RuntimeInstalled, null);
            }

            if (known.FamilyId is "windows" or "mediafeaturepack")
            {
                return (ImportResolution.WindowsPartMissing, package?.Id);
            }

            return (ImportResolution.RuntimeMissing, package?.Id);
        }

        return existsInSystem(name, arch) ? (ImportResolution.Windows, null) : (ImportResolution.NotFound, null);
    }

    /// <summary>
    /// Resolves every import of every file. One entry per DLL name and architecture (the first file that needs it is
    /// named); problems come first, then by name.
    /// </summary>
    public static IReadOnlyList<GameImport> ResolveAll(IEnumerable<GameBinary> binaries, ISet<string> gameFiles,
        Func<string, DllArch, bool> existsInSystem, Func<KnownDll, DllArch, bool> isInstalled)
    {
        ArgumentNullException.ThrowIfNull(binaries);
        var seen = new Dictionary<(string, DllArch), GameImport>();
        foreach (var binary in binaries)
        {
            foreach (var (import, delayed) in binary.Info.Imports.Select(i => (i, false)).Concat(binary.Info.DelayImports.Select(i => (i, true))))
            {
                var name = NormalizeName(import);
                var key = (name.ToLowerInvariant(), binary.Info.Arch);
                if (seen.TryGetValue(key, out var existing))
                {
                    // A normal import outranks a delay-loaded one (the program cannot even start without it).
                    if (existing.DelayLoaded && !delayed)
                    {
                        seen[key] = existing with { DelayLoaded = false, ImportedBy = binary.RelativePath };
                    }

                    continue;
                }

                var (resolution, packageId) = Resolve(name, binary.Info.Arch, gameFiles, existsInSystem, isInstalled);
                seen[key] = new GameImport(name, binary.Info.Arch, binary.RelativePath, delayed, resolution, packageId);
            }
        }

        return seen.Values
            .OrderBy(i => Rank(i.Resolution))
            .ThenBy(i => i.DllName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.Arch)
            .ToList();
    }

    /// <summary>The packages to install, each once, in catalog order.</summary>
    public static IReadOnlyList<string> NeededPackages(IEnumerable<GameImport> imports)
    {
        ArgumentNullException.ThrowIfNull(imports);
        var ids = imports.Where(i => i.Resolution == ImportResolution.RuntimeMissing && i.PackageId is not null)
            .Select(i => i.PackageId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return RuntimeCatalog.Packages.Where(p => ids.Contains(p.Id) && p.CanInstall).Select(p => p.Id).ToList();
    }

    /// <summary>Plain words for a resolution, shown in the scan result.</summary>
    public static string Describe(GameImport import)
    {
        ArgumentNullException.ThrowIfNull(import);
        var package = import.PackageId is { } id ? RuntimeCatalog.Package(id) : null;
        return import.Resolution switch
        {
            ImportResolution.InGameFolder => "Included with the game.",
            ImportResolution.WindowsApiSet => "Part of Windows (API set).",
            ImportResolution.Windows => "Part of Windows.",
            ImportResolution.RuntimeInstalled => "Installed.",
            ImportResolution.RuntimeMissing when package is { CanInstall: true } => $"Missing. Install {package.Name}.",
            ImportResolution.RuntimeMissing when package is not null => $"Missing. {package.UnavailableReason}",
            ImportResolution.WindowsPartMissing when package is not null => $"Missing. {package.UnavailableReason}",
            ImportResolution.WindowsPartMissing => "Missing Windows part.",
            _ => import.DelayLoaded
                ? "Not found, but only loaded when needed. Usually an optional part of the game."
                : "Not found. Usually part of the game itself; verify the game files in its launcher.",
        };
    }

    /// <summary>The game's display name from the picked folder or .exe.</summary>
    public static string GameName(string target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var trimmed = target.TrimEnd('\\', '/');
        if (trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFileNameWithoutExtension(trimmed.Replace('\\', '/').Split('/')[^1]);
        }

        var last = trimmed.Replace('\\', '/').Split('/')[^1];
        return string.IsNullOrWhiteSpace(last) ? trimmed : last;
    }

    private static int Rank(ImportResolution r) => r switch
    {
        ImportResolution.RuntimeMissing => 0,
        ImportResolution.WindowsPartMissing => 1,
        ImportResolution.NotFound => 2,
        ImportResolution.RuntimeInstalled => 3,
        ImportResolution.InGameFolder => 4,
        ImportResolution.Windows => 5,
        _ => 6,
    };
}
