using System.Globalization;
using PcChecker.Core.Common;

namespace PcChecker.Core.Drivers;

/// <summary>
/// Pure rules for the "Old and unused drivers" scan: groups driver-store packages into versions of the same driver,
/// finds the superseded ones, decides which are in use, classifies every finding, adds up sizes and turns a selection
/// into an ordered removal plan. Nothing here touches Windows.
/// </summary>
public static class DriverCleanupRules
{
    /// <summary>Folder under <c>%LOCALAPPDATA%\PcChecker</c> that holds the copies made before a package is deleted.</summary>
    public const string BackupFolderName = "driver-backups";

    /// <summary>
    /// Device setup classes Windows needs to start and to reach disks, keyboard and mouse. Their packages are kept even
    /// when unused, because the device can come back (a disk controller after a BIOS change, a docking-station keyboard).
    /// Both the class key names WMI reports and the English names pnputil prints are listed; the class GUID decides when
    /// pnputil prints a translated name. Network adapters are not listed: the active adapter's package is in use, so the
    /// in-use rule keeps it, and an unused network package is as safe to remove as any other.
    /// </summary>
    public static IReadOnlySet<string> ProtectedClassNames { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "System", "HDC", "SCSIAdapter", "DiskDrive", "Volume", "Keyboard", "Mouse", "Processor", "Firmware",
        "System devices", "IDE ATA/ATAPI controllers", "Storage controllers", "Disk drives", "Storage volumes",
        "Keyboards", "Mice and other pointing devices", "Processors",
    };

    /// <summary>Class GUIDs of <see cref="ProtectedClassNames"/> (lower case, with braces).</summary>
    public static IReadOnlySet<string> ProtectedClassGuids { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "{4d36e97d-e325-11ce-bfc1-08002be10318}", // System
        "{4d36e96a-e325-11ce-bfc1-08002be10318}", // HDC
        "{4d36e97b-e325-11ce-bfc1-08002be10318}", // SCSIAdapter
        "{4d36e967-e325-11ce-bfc1-08002be10318}", // DiskDrive
        "{71a27cdd-812a-11d0-bec7-08002be2092f}", // Volume
        "{4d36e96b-e325-11ce-bfc1-08002be10318}", // Keyboard
        "{4d36e96f-e325-11ce-bfc1-08002be10318}", // Mouse
        "{50127dc3-0f36-415e-a6cc-4cb3be910b65}", // Processor
        "{f2e7dd72-6468-4e36-b6f1-6488f42c1b52}", // Firmware
    };

    /// <summary>
    /// Class GUID of extension INFs (class "Extension"). Windows applies them on top of a device's base driver, and
    /// <c>Win32_PnPSignedDriver</c> only reports the base INF, so whether an extension is in use cannot be read.
    /// </summary>
    public const string ExtensionClassGuid = "{e2f84ce7-8efa-411c-aa69-97454ca4cb57}";

    /// <summary>Display adapters setup class.</summary>
    public const string DisplayClassGuid = "{4d36e968-e325-11ce-bfc1-08002be10318}";

    /// <summary>Default backup root: <c>%LOCALAPPDATA%\PcChecker\driver-backups</c>.</summary>
    public static string DefaultBackupRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PcChecker", BackupFolderName);

    /// <summary>The per-run backup folder: <c>&lt;root&gt;\yyyyMMdd-HHmmss</c>.</summary>
    public static string BackupFolderFor(string root, DateTime now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        return Path.Combine(root, now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
    }

    /// <summary>True for a protected class, by GUID first (language independent) and then by name.</summary>
    public static bool IsProtectedClass(string? className, string? classGuid)
    {
        var guid = NormalizeGuid(classGuid);
        if (guid is not null && ProtectedClassGuids.Contains(guid))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(className) && ProtectedClassNames.Contains(className.Trim());
    }

    /// <summary>
    /// True for an extension INF package (by class GUID, or by the English class name when the GUID is missing). Its use
    /// cannot be told from the installed-driver list, so it is never classed as unused.
    /// </summary>
    public static bool IsExtensionPackage(DriverPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var guid = NormalizeGuid(package.ClassGuid);
        if (guid is not null)
        {
            return guid == ExtensionClassGuid;
        }

        var name = package.ClassName?.Trim();
        return string.Equals(name, "Extension", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "Extensions", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Only third-party packages (<c>oemN.inf</c>) can ever be removed; inbox packages belong to Windows.</summary>
    public static bool IsRemovablePackageName(string? publishedName) => DriverInfoBuilder.IsOemInf(publishedName);

    /// <summary>
    /// Key that puts versions of the same driver together: original INF name, provider and class (GUID when known,
    /// otherwise the class name). A package without an original name cannot be matched and stays on its own.
    /// </summary>
    public static string GroupKey(DriverPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (string.IsNullOrWhiteSpace(package.OriginalName))
        {
            return "#" + package.PublishedName.Trim().ToLowerInvariant();
        }

        var cls = NormalizeGuid(package.ClassGuid) ?? Normalize(package.ClassName);
        if (IsAmdVersionedDisplayPackage(package))
        {
            // AMD Adrenalin uses a release-specific INF name (u0xxxxxx.inf) for every display driver version, so the
            // original name never repeats: group AMD display packages by provider and class instead.
            return $"amd-display|{Normalize(package.Provider)}|{cls}";
        }

        return $"{Normalize(package.OriginalName)}|{Normalize(package.Provider)}|{cls}";
    }

    /// <summary>
    /// True for an AMD/ATI display driver package (Display class, published by Advanced Micro Devices or ATI
    /// Technologies). Older versions of these are superseded by the newest one even though their INF names differ.
    /// </summary>
    public static bool IsAmdVersionedDisplayPackage(DriverPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        bool amd = Hardware.AmdPlatformRules.IsAmdProvider(package.Provider);
        bool display = NormalizeGuid(package.ClassGuid) is { } g
            ? g == DisplayClassGuid
            : string.Equals(package.ClassName?.Trim(), "Display", StringComparison.OrdinalIgnoreCase);
        return amd && display;
    }

    /// <summary>Groups third-party packages into versions of the same driver (see <see cref="GroupKey"/>).</summary>
    public static IReadOnlyList<IReadOnlyList<DriverPackage>> GroupPackages(IEnumerable<DriverPackage> packages)
    {
        ArgumentNullException.ThrowIfNull(packages);
        return packages
            .Where(p => IsRemovablePackageName(p.PublishedName))
            .GroupBy(GroupKey, StringComparer.Ordinal)
            .Select(g => (IReadOnlyList<DriverPackage>)g.ToList())
            .ToList();
    }

    /// <summary>
    /// Orders two packages the way Windows ranks otherwise equal drivers: the later driver date wins, and on the same
    /// date the higher version wins. A missing date or version counts as older. Zero means a tie.
    /// </summary>
    public static int CompareRelease(DriverPackage a, DriverPackage b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        int byDate = Nullable.Compare(a.Date?.Date, b.Date?.Date);
        return byDate != 0 ? byDate : Math.Sign(VersionText.Compare(a.Version, b.Version));
    }

    /// <summary>
    /// Maps every superseded package (published name) to the newest package of its group. A package is superseded only
    /// when another version is strictly newer; two packages with the same date and version supersede neither.
    /// </summary>
    public static IReadOnlyDictionary<string, DriverPackage> FindSuperseded(IEnumerable<DriverPackage> packages)
    {
        var result = new Dictionary<string, DriverPackage>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in GroupPackages(packages))
        {
            var newest = group.Aggregate((x, y) => CompareRelease(y, x) > 0 ? y : x);
            foreach (var p in group.Where(p => CompareRelease(newest, p) > 0))
            {
                result[p.PublishedName] = newest;
            }
        }

        return result;
    }

    /// <summary>Connected devices per published package name (only connected devices count as "in use").</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> PackagesInUse(IEnumerable<DriverInfo> drivers)
    {
        ArgumentNullException.ThrowIfNull(drivers);
        return drivers
            .Where(d => d.IsPresent && !string.IsNullOrWhiteSpace(d.InfName))
            .GroupBy(d => d.InfName!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)g.Select(d => d.DeviceName).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The classification: anything in use, inbox or of a protected class is Keep; otherwise superseded packages are
    /// Safe to remove and the rest Probably safe.
    /// </summary>
    public static DriverCleanupStatus Classify(bool isInbox, bool inUse, bool protectedClass, bool superseded)
    {
        if (isInbox || inUse || protectedClass)
        {
            return DriverCleanupStatus.Keep;
        }

        return superseded ? DriverCleanupStatus.SafeToRemove : DriverCleanupStatus.ProbablySafe;
    }

    /// <summary>
    /// Merges the disconnected devices pnputil listed with the ones WMI marks as not present (older Windows builds have
    /// no <c>/enum-devices /disconnected</c>). Duplicates by instance id keep the pnputil entry.
    /// </summary>
    public static IReadOnlyList<DisconnectedDevice> MergeDisconnected(IEnumerable<DisconnectedDevice> fromPnpUtil, IEnumerable<DriverInfo> drivers)
    {
        ArgumentNullException.ThrowIfNull(fromPnpUtil);
        ArgumentNullException.ThrowIfNull(drivers);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<DisconnectedDevice>();
        foreach (var d in fromPnpUtil.Where(d => !string.IsNullOrWhiteSpace(d.InstanceId) && seen.Add(d.InstanceId.Trim())))
        {
            result.Add(d);
        }

        foreach (var d in drivers.Where(d => !d.IsPresent && !string.IsNullOrWhiteSpace(d.DeviceId) && seen.Add(d.DeviceId.Trim())))
        {
            result.Add(new DisconnectedDevice(d.DeviceId, d.DeviceName, d.DeviceClass, null, d.Provider, null, d.InfName));
        }

        return result;
    }

    /// <summary>
    /// Builds the findings. Packages are listed when they are superseded or unused (a current package in use is just a
    /// normal driver); inbox packages are never listed. Disconnected devices are listed when they use a third-party
    /// package. When <paramref name="usageKnown"/> is false (the device list could not be read) every package is Keep,
    /// because "not in use" cannot be told apart from "not checked". Extension INFs are always Keep for the same reason:
    /// the device list names only each device's base driver, never the extensions applied to it.
    /// </summary>
    public static IReadOnlyList<DriverCleanupItem> Analyze(
        IEnumerable<DriverPackage> packages,
        IEnumerable<DriverInfo> drivers,
        IEnumerable<DisconnectedDevice> disconnected,
        IReadOnlyDictionary<string, long>? sizes = null,
        bool usageKnown = true)
    {
        ArgumentNullException.ThrowIfNull(packages);
        ArgumentNullException.ThrowIfNull(drivers);
        ArgumentNullException.ThrowIfNull(disconnected);

        var driverList = drivers.ToList();
        var oem = packages
            .Where(p => IsRemovablePackageName(p.PublishedName))
            .GroupBy(p => p.PublishedName.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
        var byName = oem.ToDictionary(p => p.PublishedName.Trim(), StringComparer.OrdinalIgnoreCase);
        var inUse = PackagesInUse(driverList);
        var superseded = FindSuperseded(oem);
        var ghosts = MergeDisconnected(disconnected, driverList)
            .Where(d => IsRemovablePackageName(d.DriverName))
            .ToList();
        var ghostsByPackage = ghosts
            .GroupBy(d => d.DriverName!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<DisconnectedDevice>)g.ToList(), StringComparer.OrdinalIgnoreCase);

        var items = new List<DriverCleanupItem>();
        foreach (var p in oem)
        {
            var name = p.PublishedName.Trim();
            var users = inUse.GetValueOrDefault(name) ?? [];
            bool used = users.Count > 0;
            superseded.TryGetValue(name, out var newer);
            if (usageKnown && used && newer is null)
            {
                continue;
            }

            bool prot = IsProtectedClass(p.ClassName, p.ClassGuid);
            bool extension = IsExtensionPackage(p);
            var myGhosts = ghostsByPackage.GetValueOrDefault(name) ?? [];
            var status = usageKnown && !extension ? Classify(false, used, prot, newer is not null) : DriverCleanupStatus.Keep;
            items.Add(new DriverCleanupItem(DriverCleanupKind.Package, name, PackageTitle(p, users, myGhosts), status,
                PackageReason(p, users, newer, prot, myGhosts, usageKnown, extension))
            {
                Package = p,
                InUse = used,
                UsedBy = users,
                IsSuperseded = newer is not null,
                NewerPackage = newer,
                IsProtectedClass = prot,
                SizeBytes = sizes is not null && sizes.TryGetValue(name, out var size) ? size : null,
                DisconnectedDevices = myGhosts,
            });
        }

        foreach (var g in ghosts)
        {
            bool prot = IsProtectedClass(g.ClassName, g.ClassGuid);
            byName.TryGetValue(g.DriverName!.Trim(), out var package);
            bool packageUsed = inUse.ContainsKey(g.DriverName.Trim());
            bool packageGoesToo = usageKnown && package is not null && !packageUsed && !IsProtectedClass(package.ClassName, package.ClassGuid)
                && !IsExtensionPackage(package);
            items.Add(new DriverCleanupItem(DriverCleanupKind.DisconnectedDevice, g.InstanceId.Trim(), g.DisplayName,
                prot ? DriverCleanupStatus.Keep : DriverCleanupStatus.ProbablySafe,
                DeviceReason(g, prot, packageUsed, packageGoesToo))
            {
                Package = package,
                Device = g,
                IsProtectedClass = prot,
            });
        }

        return Sort(items);
    }

    /// <summary>Returns the findings with package sizes filled in from <paramref name="sizes"/> (published name → bytes).</summary>
    public static IReadOnlyList<DriverCleanupItem> ApplySizes(IEnumerable<DriverCleanupItem> items, IReadOnlyDictionary<string, long> sizes)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(sizes);
        return items
            .Select(i => i.Kind == DriverCleanupKind.Package && sizes.TryGetValue(i.Id, out var s) ? i with { SizeBytes = s } : i)
            .ToList();
    }

    /// <summary>Safe first, then Probably safe, then Keep; packages before devices; largest first; then by title.</summary>
    public static IReadOnlyList<DriverCleanupItem> Sort(IEnumerable<DriverCleanupItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return items
            .OrderBy(i => i.Status)
            .ThenBy(i => i.Kind)
            .ThenByDescending(i => i.SizeBytes ?? -1)
            .ThenBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(i => i.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Headline numbers. "Space you can free" counts the packages that are not Keep and whose size is known.</summary>
    public static DriverCleanupSummary Summarize(IEnumerable<DriverCleanupItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var list = items.ToList();
        var removable = list.Where(i => i.Kind == DriverCleanupKind.Package && i.Status != DriverCleanupStatus.Keep).ToList();
        return new DriverCleanupSummary(
            list.Count(i => i.Kind == DriverCleanupKind.Package),
            list.Count(i => i.Status == DriverCleanupStatus.SafeToRemove),
            list.Count(i => i.Status == DriverCleanupStatus.ProbablySafe),
            list.Count(i => i.Status == DriverCleanupStatus.Keep),
            TotalSize(removable),
            removable.Any(i => i.SizeBytes is null),
            list.Count(i => i.Kind == DriverCleanupKind.DisconnectedDevice));
    }

    /// <summary>Sum of the known package sizes; devices and unknown sizes add nothing, and a package counts once.</summary>
    public static long TotalSize(IEnumerable<DriverCleanupItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return items
            .Where(i => i.Kind == DriverCleanupKind.Package)
            .GroupBy(i => i.Id, StringComparer.OrdinalIgnoreCase)
            .Sum(g => Math.Max(0, g.First().SizeBytes ?? 0));
    }

    /// <summary>
    /// Whether the user may tick a finding. Inbox packages never; Keep items only in Advanced mode (and even then the
    /// removal never forces a package off a device that uses it).
    /// </summary>
    public static bool CanSelect(DriverCleanupItem item, bool advancedMode)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.IsInbox || (item.Kind == DriverCleanupKind.Package && !IsRemovablePackageName(item.Id)))
        {
            return false;
        }

        return item.Status != DriverCleanupStatus.Keep || advancedMode;
    }

    /// <summary>Only "Safe to remove" findings are ticked after a scan.</summary>
    public static bool IsSelectedByDefault(DriverCleanupItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.Status == DriverCleanupStatus.SafeToRemove;
    }

    /// <summary>
    /// Turns the ticked findings into ordered steps: first forget the disconnected devices, then delete the packages
    /// (a package still referenced by a remembered device would otherwise be refused). Selecting a disconnected device
    /// also deletes its package when nothing connected uses it; selecting a package also forgets the disconnected devices
    /// that point at it. Anything <see cref="CanSelect"/> rejects is left out, and inbox packages never appear.
    /// </summary>
    public static IReadOnlyList<DriverCleanupStep> BuildRemovalPlan(IEnumerable<DriverCleanupItem> selected, IEnumerable<DriverCleanupItem> all, bool advancedMode)
    {
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(all);
        var allList = all.ToList();
        var chosen = selected.Where(i => CanSelect(i, advancedMode)).ToList();
        var chosenIds = new HashSet<string>(chosen.Select(i => i.Kind + "|" + i.Id), StringComparer.OrdinalIgnoreCase);
        var packages = allList.Where(i => i.Kind == DriverCleanupKind.Package)
            .GroupBy(i => i.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var devices = allList.Where(i => i.Kind == DriverCleanupKind.DisconnectedDevice)
            .GroupBy(i => i.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var deviceSteps = new List<DriverCleanupStep>();
        var packageSteps = new List<DriverCleanupStep>();
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in chosen)
        {
            if (item.Kind == DriverCleanupKind.DisconnectedDevice)
            {
                AddDevice(item);
                if (item.PackageName is { } pkgName && packages.TryGetValue(pkgName, out var pkg) && !pkg.InUse && CanSelect(pkg, advancedMode))
                {
                    AddPackage(pkg);
                }
            }
            else
            {
                AddPackage(item);
                foreach (var ghost in item.DisconnectedDevices)
                {
                    if (devices.TryGetValue(ghost.InstanceId, out var dev) && CanSelect(dev, advancedMode))
                    {
                        AddDevice(dev);
                    }
                }
            }
        }

        return [.. deviceSteps, .. packageSteps];

        void AddDevice(DriverCleanupItem dev)
        {
            if (added.Add("D|" + dev.Id))
            {
                deviceSteps.Add(new DriverCleanupStep(DriverCleanupStepKind.RemoveDevice, dev.Id, dev.Title, null,
                    !chosenIds.Contains(dev.Kind + "|" + dev.Id)));
            }
        }

        void AddPackage(DriverCleanupItem pkg)
        {
            if (IsRemovablePackageName(pkg.Id) && added.Add("P|" + pkg.Id))
            {
                packageSteps.Add(new DriverCleanupStep(DriverCleanupStepKind.DeletePackage, pkg.Id, pkg.Title, pkg.SizeBytes,
                    !chosenIds.Contains(pkg.Kind + "|" + pkg.Id)));
            }
        }
    }

    /// <summary>"Version 31.0.15.3623 · 21 Jun 2023" style text for a package (either part may be missing).</summary>
    public static string DescribeRelease(DriverPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(package.Version))
        {
            parts.Add("version " + package.Version);
        }

        if (package.Date is { } date)
        {
            parts.Add("from " + date.ToString("d MMM yyyy", CultureInfo.CurrentCulture));
        }

        return parts.Count == 0 ? "unknown version" : string.Join(" ", parts);
    }

    private static string PackageTitle(DriverPackage p, IReadOnlyList<string> users, IReadOnlyList<DisconnectedDevice> ghosts)
    {
        if (users.Count > 0)
        {
            return users[0];
        }

        if (ghosts.Count > 0)
        {
            return ghosts[0].DisplayName;
        }

        var provider = string.IsNullOrWhiteSpace(p.Provider) ? "Unknown maker" : p.Provider.Trim();
        return string.IsNullOrWhiteSpace(p.ClassName) ? provider + " driver" : $"{provider} · {p.ClassName.Trim()}";
    }

    private static string PackageReason(DriverPackage p, IReadOnlyList<string> users, DriverPackage? newer, bool prot,
        IReadOnlyList<DisconnectedDevice> ghosts, bool usageKnown, bool extension)
    {
        if (!usageKnown)
        {
            return "PC Checker could not read which devices use this driver, so it is kept.";
        }

        if (extension && users.Count == 0)
        {
            return "An extension driver: Windows adds it on top of a device's main driver, and PC Checker cannot tell which devices use it, so it is kept.";
        }

        if (users.Count > 0)
        {
            var text = $"In use by {string.Join(", ", users)}.";
            return newer is null ? text : text + $" A newer {DescribeRelease(newer)} is also stored, but Windows still uses this one.";
        }

        if (prot)
        {
            var cls = string.IsNullOrWhiteSpace(p.ClassName) ? "this kind" : p.ClassName.Trim();
            return $"Not in use, but Windows needs drivers of this kind ({cls}) to start or to reach your disks, keyboard and mouse, so it is kept.";
        }

        var ghostText = ghosts.Count == 0 ? string.Empty
            : $" It belongs to {string.Join(", ", ghosts.Select(g => g.DisplayName))}, which is not connected.";
        return newer is not null
            ? $"Replaced by the newer {DescribeRelease(newer)}. Nothing connected uses this one.{ghostText}"
            : $"Nothing connected uses this driver and there is no newer version of it.{ghostText}";
    }

    private static string DeviceReason(DisconnectedDevice device, bool prot, bool packageUsed, bool packageGoesToo)
    {
        if (prot)
        {
            return "Not connected. Windows keeps devices of this kind to start and to reach your disks, so it is left alone.";
        }

        var text = "Not connected. Removing it only makes Windows forget the device; it is set up again when you plug it back in.";
        if (packageUsed)
        {
            return text + $" Its driver ({device.DriverName}) is still used by another device and stays.";
        }

        return packageGoesToo ? text + $" Its driver package ({device.DriverName}) is removed with it." : text;
    }

    private static string Normalize(string? text) => string.IsNullOrWhiteSpace(text) ? string.Empty : text.Trim().ToLowerInvariant();

    private static string? NormalizeGuid(string? guid)
    {
        if (string.IsNullOrWhiteSpace(guid))
        {
            return null;
        }

        var g = guid.Trim().ToLowerInvariant();
        if (!g.StartsWith('{'))
        {
            g = "{" + g;
        }

        if (!g.EndsWith('}'))
        {
            g += "}";
        }

        return g;
    }
}
