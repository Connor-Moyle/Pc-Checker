using System.Text.RegularExpressions;

namespace PcChecker.Core.GameRuntimes;

/// <summary>
/// Turns what was read from the PC (which DLL files exist, installed versions, feature states) into the rows and
/// package states the page shows. Pure, so every rule is tested.
/// </summary>
public static partial class RuntimeStatusRules
{
    /// <summary>Families whose packages cannot be installed from here and whose missing DLLs are not counted as problems.</summary>
    private static readonly HashSet<string> InformationalFamilies = new(StringComparer.Ordinal) { "gfwl", "vc2003", "graphics-driver" };

    /// <summary>The status of one DLL for one architecture.</summary>
    public static DllStatus StatusOf(KnownDll dll, DllArch arch, bool is64BitWindows, Func<KnownDll, DllArch, bool> isPresent)
    {
        ArgumentNullException.ThrowIfNull(dll);
        ArgumentNullException.ThrowIfNull(isPresent);
        if (!dll.Has(arch) || (arch == DllArch.X64 && !is64BitWindows))
        {
            return DllStatus.NotApplicable;
        }

        return isPresent(dll, arch) ? DllStatus.Present : DllStatus.Missing;
    }

    /// <summary>
    /// Builds the full scan result. Media Feature Pack DLLs only count as missing on N editions; everywhere else they
    /// are part of Windows and their package is not applicable.
    /// </summary>
    public static RuntimeScanResult Build(RuntimeProbe probe, bool is64BitWindows, DateTimeOffset scannedAt, IReadOnlyList<string>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(probe);
        var rows = RuntimeCatalog.Dlls
            .Select(d => new DllStatusRow(d,
                StatusOf(d, DllArch.X86, is64BitWindows, probe.IsPresent),
                StatusOf(d, DllArch.X64, is64BitWindows, probe.IsPresent)))
            .ToList();

        var packages = RuntimeCatalog.Packages.Select(p => PackageStatusOf(p, rows, probe, is64BitWindows)).ToList();
        return new RuntimeScanResult(rows, packages, probe.IsNEdition, scannedAt, warnings ?? []);
    }

    /// <summary>The installed state of one package from its DLL rows, versions and feature state.</summary>
    public static PackageStatus PackageStatusOf(RuntimePackage package, IReadOnlyList<DllStatusRow> rows, RuntimeProbe probe, bool is64BitWindows)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(probe);
        var versions = probe.InstalledVersions.TryGetValue(package.Id, out var v) ? v : [];

        int present = 0;
        int total = 0;
        foreach (var row in rows.Where(r => r.Dll.FamilyId == package.FamilyId))
        {
            foreach (var arch in new[] { DllArch.X86, DllArch.X64 })
            {
                if (package.Arch is { } a && a != arch)
                {
                    continue;
                }

                var status = arch == DllArch.X86 ? row.Status32 : row.Status64;
                if (status == DllStatus.NotApplicable)
                {
                    continue;
                }

                total++;
                if (status == DllStatus.Present)
                {
                    present++;
                }
            }
        }

        if (package.Arch == DllArch.X64 && !is64BitWindows)
        {
            return new PackageStatus(package, PackageState.NotApplicable, present, total, versions);
        }

        var state = package.Kind switch
        {
            RuntimeInstallKind.Installer => FromFiles(present, total, versions.Count > 0),
            RuntimeInstallKind.WindowsFeature => package.FeatureName is { } f && probe.FeatureEnabled.TryGetValue(f, out var on) && on is { } enabled
                ? (enabled ? PackageState.Installed : PackageState.Missing)
                : FromFiles(present, total, false),
            RuntimeInstallKind.WindowsSettings => probe.IsNEdition ? FromFiles(present, total, false) : PackageState.NotApplicable,
            _ => PackageState.NotApplicable,
        };

        return new PackageStatus(package, state, present, total, versions);
    }

    /// <summary>Installed when every DLL is there, Partial when some are (or it is registered but files are gone), else Missing.</summary>
    public static PackageState FromFiles(int present, int total, bool registered)
    {
        if (total == 0)
        {
            return registered ? PackageState.Installed : PackageState.Missing;
        }

        if (present >= total)
        {
            return PackageState.Installed;
        }

        return present > 0 || registered ? PackageState.Partial : PackageState.Missing;
    }

    /// <summary>True when a missing DLL of this row should count in the "missing" tiles.</summary>
    public static bool CountsAsProblem(DllStatusRow row, bool isNEdition)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (InformationalFamilies.Contains(row.Dll.FamilyId))
        {
            return false;
        }

        return row.Dll.FamilyId != "mediafeaturepack" || isNEdition;
    }

    /// <summary>Missing DLL counts for the tiles: (32-bit, 64-bit).</summary>
    public static (int Missing32, int Missing64) MissingCounts(RuntimeScanResult scan)
    {
        ArgumentNullException.ThrowIfNull(scan);
        int m32 = 0, m64 = 0;
        foreach (var row in scan.Dlls.Where(r => CountsAsProblem(r, scan.IsNEdition)))
        {
            if (row.Status32 == DllStatus.Missing)
            {
                m32++;
            }

            if (row.Status64 == DllStatus.Missing)
            {
                m64++;
            }
        }

        return (m32, m64);
    }

    /// <summary>The packages "Install everything missing" would install: installable, and missing or partly missing.</summary>
    public static IReadOnlyList<RuntimePackage> MissingInstallable(RuntimeScanResult scan)
    {
        ArgumentNullException.ThrowIfNull(scan);
        return scan.Packages.Where(p => p.Package.CanInstall && p.State is PackageState.Missing or PackageState.Partial).Select(p => p.Package).ToList();
    }

    /// <summary>
    /// The packages a DLL row's Install button installs: the package of each architecture that is missing, or of
    /// every architecture the DLL exists for when nothing is missing (a repair). Only installable packages; each once.
    /// </summary>
    public static IReadOnlyList<RuntimePackage> InstallTargets(DllStatusRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var missing = new List<DllArch>();
        if (row.Status32 == DllStatus.Missing)
        {
            missing.Add(DllArch.X86);
        }

        if (row.Status64 == DllStatus.Missing)
        {
            missing.Add(DllArch.X64);
        }

        if (missing.Count == 0)
        {
            if (row.Status32 == DllStatus.Present)
            {
                missing.Add(DllArch.X86);
            }

            if (row.Status64 == DllStatus.Present)
            {
                missing.Add(DllArch.X64);
            }
        }

        return missing.Select(a => RuntimeCatalog.PackageFor(row.Dll.FamilyId, a))
            .OfType<RuntimePackage>()
            .Where(p => p.CanInstall)
            .DistinctBy(p => p.Id)
            .ToList();
    }

    /// <summary>True for Windows N editions ("ProfessionalN", "CoreN", "EnterpriseN", "EducationN"…).</summary>
    public static bool IsNEdition(string? editionId)
    {
        if (string.IsNullOrWhiteSpace(editionId))
        {
            return false;
        }

        var e = editionId.Trim();
        return (e.Length > 1 && e.EndsWith('N')) || e.EndsWith("NEval", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads an uninstall entry's display name and tells which runtime package it is, or null. Covers the names
    /// Microsoft used over the years ("Microsoft Visual C++ 2010  x64 Redistributable - 10.0.40219", "Microsoft Visual
    /// C++ 2015-2022 Redistributable (x86) - 14.40.33810", "Microsoft Visual C++ v14 Redistributable (x64) - 14.50…")
    /// plus XNA, OpenAL and PhysX.
    /// </summary>
    public static string? PackageIdFromDisplayName(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return null;
        }

        var name = displayName.Trim();
        var vc = VcName().Match(name);
        if (vc.Success)
        {
            if (name.Contains("Additional Runtime", StringComparison.OrdinalIgnoreCase) || name.Contains("Debug", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string year = vc.Groups["year"].Value;
            string family = year switch
            {
                "2005" => "vc2005",
                "2008" => "vc2008",
                "2010" => "vc2010",
                "2012" => "vc2012",
                "2013" => "vc2013",
                _ => "vc14", // 2015, 2015-2017, 2015-2019, 2015-2022, 2017, 2019, 2022, v14
            };
            bool x64 = X64Marker().IsMatch(name);
            bool arm = name.Contains("arm64", StringComparison.OrdinalIgnoreCase) || name.Contains("ia64", StringComparison.OrdinalIgnoreCase);
            if (arm)
            {
                return null;
            }

            return family + (x64 ? "-x64" : "-x86");
        }

        if (name.StartsWith("Microsoft XNA Framework Redistributable 4.0", StringComparison.OrdinalIgnoreCase))
        {
            return "xna4";
        }

        if (name.Equals("OpenAL", StringComparison.OrdinalIgnoreCase))
        {
            return "openal";
        }

        if (name.StartsWith("NVIDIA PhysX", StringComparison.OrdinalIgnoreCase))
        {
            return name.Contains("Legacy", StringComparison.OrdinalIgnoreCase) ? "physx-legacy" : "physx";
        }

        return null;
    }

    /// <summary>"v14.40.33810.00" or "14.40.33810" → "14.40.33810"; null for anything that is not a version.</summary>
    public static string? CleanVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var m = VersionPattern().Match(text);
        return m.Success ? m.Groups[1].Value.TrimEnd('.') : null;
    }

    [GeneratedRegex(@"^Microsoft Visual C\+\+ (?<year>2005|2008|2010|2012|2013|2015|2017|2019|2022|v14)(-\d{4})?\b.*(Redistributable|Runtime)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VcName();

    [GeneratedRegex(@"\b(x64|amd64)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex X64Marker();

    [GeneratedRegex(@"v?(\d+\.\d+(\.\d+){0,2})", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}
