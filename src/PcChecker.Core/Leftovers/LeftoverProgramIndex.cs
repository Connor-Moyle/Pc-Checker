using PcChecker.Core.DiskSpace;
using PcChecker.Core.RegistryHealth;
using PcChecker.Core.Software;

namespace PcChecker.Core.Leftovers;

/// <summary>
/// What is installed, in the two forms the leftover checks need: the names that identify a program (product names,
/// publishers, Store package names) and the folders that belong to one (install locations, and the folders of its
/// uninstaller and icon). A folder is in use when it is one of those folders, inside one, or contains one. Pure.
/// </summary>
public sealed class LeftoverProgramIndex
{
    private readonly List<string> _names = [];
    private readonly List<string> _locations = [];
    private readonly List<string> _roots;

    /// <param name="programs">The installed programs and Store apps.</param>
    /// <param name="environment">Environment lookup for expanding paths.</param>
    /// <param name="roots">
    /// The scanned roots (Program Files, AppData…). A recorded location that is one of them or contains one (a program
    /// that claims all of AppData\Local) is ignored, so one bad entry cannot hide everything.
    /// </param>
    public LeftoverProgramIndex(IEnumerable<InstalledProgram> programs, Func<string, string?> environment, IEnumerable<string>? roots = null)
    {
        ArgumentNullException.ThrowIfNull(programs);
        ArgumentNullException.ThrowIfNull(environment);
        _roots = roots?.Where(r => !string.IsNullOrWhiteSpace(r)).Select(DiskSpacePaths.Normalize).ToList() ?? [];
        foreach (var program in programs)
        {
            ProgramCount++;
            AddName(program.Name);
            AddName(program.Publisher);
            AddPackageNames(program.PackageFullName);
            AddLocation(program.InstallLocation, environment);
            AddLocation(RegistryPathTools.ExtractPath(program.UninstallString, environment) is { } uninstaller ? DiskSpacePaths.ParentOf(uninstaller) : null, environment);
            AddLocation(RegistryPathTools.ExtractPath(program.IconPath, environment) is { } icon ? DiskSpacePaths.ParentOf(icon) : null, environment);
        }
    }

    /// <summary>How many programs the index was built from.</summary>
    public int ProgramCount { get; }

    /// <summary>The first installed name or publisher that <paramref name="name"/> matches (see <see cref="LeftoverNameMatcher.IsMatch"/>), or null.</summary>
    public string? MatchName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return _names.FirstOrDefault(n => LeftoverNameMatcher.IsMatch(name, n));
    }

    /// <summary>True when <paramref name="path"/> is an installed program's folder, lies inside one, or contains one.</summary>
    public bool OwnsPath(string path) =>
        _locations.Any(l => DiskSpacePaths.IsSameOrInside(path, l) || DiskSpacePaths.IsSameOrInside(l, path));

    /// <summary>True when an installed program's folder lies strictly inside <paramref name="path"/> (a vendor folder).</summary>
    public bool ContainsInstall(string path) =>
        _locations.Any(l => DiskSpacePaths.IsSameOrInside(l, path) && !DiskSpacePaths.Normalize(l).Equals(DiskSpacePaths.Normalize(path), StringComparison.OrdinalIgnoreCase));

    private void AddName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var trimmed = name.Trim();
        if (!_names.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
        {
            _names.Add(trimmed);
        }
    }

    /// <summary>"SpotifyAB.SpotifyMusic_1.249.0.0_x64__zpdnekdrzrea0" adds "SpotifyAB.SpotifyMusic", "SpotifyAB" and "SpotifyMusic".</summary>
    private void AddPackageNames(string? packageFullName)
    {
        if (string.IsNullOrWhiteSpace(packageFullName))
        {
            return;
        }

        var name = packageFullName.Split('_')[0];
        AddName(name);
        foreach (var part in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            AddName(part);
        }
    }

    private void AddLocation(string? location, Func<string, string?> environment)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return;
        }

        var expanded = RegistryPathTools.Expand(location.Trim().Trim('"'), environment);
        if (!DiskSpacePaths.IsLocalAbsolute(expanded) || DiskSpacePaths.HasRelativeSegments(expanded) || DiskSpacePaths.IsDriveRoot(expanded))
        {
            return;
        }

        var normalized = DiskSpacePaths.Normalize(expanded);
        if (_roots.Any(r => DiskSpacePaths.IsSameOrInside(r, normalized)))
        {
            return;
        }

        if (!_locations.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            _locations.Add(normalized);
        }
    }
}
