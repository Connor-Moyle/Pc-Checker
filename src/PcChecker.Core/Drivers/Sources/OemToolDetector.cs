using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using PcChecker.Core.Common;
using PcChecker.Core.Software;

namespace PcChecker.Core.Drivers.Sources;

/// <summary>An uninstall entry's name and the places it says the program lives.</summary>
public sealed record InstalledProgramHint(string? DisplayName, string? DisplayIcon, string? InstallLocation);

/// <summary>A Microsoft Store package and the app ids in its manifest.</summary>
public sealed record StorePackageHint(string Name, string FamilyName, string? InstallLocation, IReadOnlyList<string> AppIds);

/// <summary>
/// Finds which update tools are installed: uninstall entries (64-bit, 32-bit, current user), known program paths
/// under Program Files and Microsoft Store packages. Also finds winget's real program inside its protected Store
/// folder, so the elevated app never starts the per-user alias in %LOCALAPPDATA%.
/// </summary>
public sealed class OemToolDetector
{
    private const string WingetPackageName = "Microsoft.DesktopAppInstaller";
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private Task<IReadOnlyList<StorePackageHint>>? _storePackages;

    public OemToolDetector(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>Folders only administrators can change: Program Files, Program Files (x86) and the Store apps folder.</summary>
    public static IReadOnlyList<string> ProtectedRoots()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var roots = new List<string>();
        foreach (var r in new[] { pf, pf86 })
        {
            if (!string.IsNullOrWhiteSpace(r) && !roots.Contains(r, StringComparer.OrdinalIgnoreCase))
            {
                roots.Add(r);
            }
        }

        return roots;
    }

    /// <summary>Detects the given tools. Problems are logged and the tool counts as not installed.</summary>
    public async Task<IReadOnlyList<OemToolInstall>> DetectAsync(IReadOnlyList<OemTool> tools, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || tools.Count == 0)
        {
            return [];
        }

        var programs = ReadUninstallHints(cancellationToken);
        var packages = tools.Any(t => t.StorePackageNames.Count > 0) ? await ReadStorePackagesAsync(cancellationToken).ConfigureAwait(false) : [];
        return Resolve(tools, programs, packages, ProtectedRoots(), File.Exists);
    }

    /// <summary>
    /// Pure matching: a tool is installed when a Store package with its name is present (started through its app id),
    /// or when one of its program paths exists under a protected root, or when an uninstall entry has its name (the
    /// program from DisplayIcon or InstallLocation is only used when it sits in a protected folder).
    /// </summary>
    public static IReadOnlyList<OemToolInstall> Resolve(IReadOnlyList<OemTool> tools, IReadOnlyList<InstalledProgramHint> programs,
        IReadOnlyList<StorePackageHint> packages, IReadOnlyList<string> protectedRoots, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(programs);
        ArgumentNullException.ThrowIfNull(packages);
        ArgumentNullException.ThrowIfNull(protectedRoots);
        ArgumentNullException.ThrowIfNull(fileExists);
        var found = new List<OemToolInstall>();
        foreach (var tool in tools)
        {
            var package = packages.FirstOrDefault(p => tool.StorePackageNames.Any(n => string.Equals(n, p.Name, StringComparison.OrdinalIgnoreCase)));
            if (package is not null)
            {
                var appId = package.AppIds.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));
                found.Add(new OemToolInstall(tool, null, appId is null ? null : package.FamilyName + "!" + appId));
                continue;
            }

            string? exe = null;
            foreach (var root in protectedRoots)
            {
                foreach (var relative in tool.ExecutablePaths)
                {
                    var candidate = OemToolCatalog.JoinWindowsPath(root, relative);
                    if (exe is null && fileExists(candidate))
                    {
                        exe = candidate;
                    }
                }
            }

            var entry = programs.FirstOrDefault(p => OemToolCatalog.MatchesDisplayName(tool, p.DisplayName));
            if (exe is null && entry is not null)
            {
                var fromIcon = OemToolCatalog.ExecutableFromDisplayIcon(entry.DisplayIcon);
                if (fromIcon is not null && OemToolCatalog.IsInProtectedFolder(fromIcon, protectedRoots) && fileExists(fromIcon))
                {
                    exe = fromIcon;
                }
            }

            if (exe is not null || entry is not null)
            {
                found.Add(new OemToolInstall(tool, exe, null));
            }
        }

        return found;
    }

    /// <summary>
    /// winget.exe inside the App Installer package folder (C:\Program Files\WindowsApps\…), or null. The alias in
    /// %LOCALAPPDATA%\Microsoft\WindowsApps is a per-user file and is never used.
    /// </summary>
    public async Task<string?> FindWingetAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var packages = await ReadStorePackagesAsync(cancellationToken).ConfigureAwait(false);
        var installer = packages.FirstOrDefault(p => string.Equals(p.Name, WingetPackageName, StringComparison.OrdinalIgnoreCase));
        if (installer?.InstallLocation is not { } location)
        {
            return null;
        }

        var exe = Path.Combine(location, "winget.exe");
        var windowsApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
        return OemToolCatalog.IsInProtectedFolder(exe, [windowsApps]) && File.Exists(exe) ? exe : null;
    }

    [SupportedOSPlatform("windows")]
    private IReadOnlyList<InstalledProgramHint> ReadUninstallHints(CancellationToken cancellationToken)
    {
        var hints = new List<InstalledProgramHint>();
        var warnings = new List<string>();
        foreach (var (hive, view, subKey, source, label) in RegistryUninstallReader.Hives)
        {
            foreach (var entry in RegistryUninstallReader.ReadHive(hive, view, subKey, source, label, readUninstallerHeaders: false, warnings, cancellationToken))
            {
                hints.Add(new InstalledProgramHint(Text(entry, "DisplayName"), Text(entry, "DisplayIcon"), Text(entry, "InstallLocation")));
            }
        }

        foreach (var warning in warnings.Take(3))
        {
            _logger.LogDebug("Uninstall entries: {Warning}", warning);
        }

        return hints;
    }

    private static string? Text(UninstallEntry entry, string name) =>
        entry.Values.TryGetValue(name, out var value) ? value?.ToString() : null;

    /// <summary>The current user's Store packages that matter here (the tools and App Installer), read once per detector.</summary>
    private Task<IReadOnlyList<StorePackageHint>> ReadStorePackagesAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return _storePackages ??= ReadStorePackagesCoreAsync(cancellationToken);
        }
    }

    private async Task<IReadOnlyList<StorePackageHint>> ReadStorePackagesCoreAsync(CancellationToken cancellationToken)
    {
        var names = OemToolCatalog.Tools.SelectMany(t => t.StorePackageNames).Append(WingetPackageName).Distinct(StringComparer.OrdinalIgnoreCase);
        var list = string.Join(",", names.Select(PowerShellRunner.Quote));
        var script = $"$names = @({list}); Get-AppxPackage | Where-Object {{ $names -contains $_.Name }} | ForEach-Object {{ "
            + "$ids = @(); try { $ids = @(($_ | Get-AppxPackageManifest).Package.Applications.Application | ForEach-Object { [string]$_.Id }) } catch { }; "
            + "[pscustomobject]@{ Name = $_.Name; FamilyName = $_.PackageFamilyName; InstallLocation = $_.InstallLocation; AppIds = $ids } } "
            + "| ConvertTo-Json -Depth 3 -Compress";
        try
        {
            var result = await PowerShellRunner.RunAsync(script, TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false);
            if (!result.Success || string.IsNullOrWhiteSpace(result.StdOut))
            {
                return [];
            }

            var parsed = PowerShellRunner.ParseJson<List<StorePackageDto>>(result.StdOut)
                ?? (PowerShellRunner.ParseJson<StorePackageDto>(result.StdOut) is { } single ? [single] : []);
            return parsed
                .Where(p => !string.IsNullOrWhiteSpace(p.Name) && !string.IsNullOrWhiteSpace(p.FamilyName))
                .Select(p => new StorePackageHint(p.Name!, p.FamilyName!, p.InstallLocation, p.AppIds ?? []))
                .ToList();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Store packages could not be listed; Store-installed tools will show as missing.");
            return [];
        }
    }

    private sealed class StorePackageDto
    {
        public string? Name { get; set; }
        public string? FamilyName { get; set; }
        public string? InstallLocation { get; set; }
        public List<string>? AppIds { get; set; }
    }
}
