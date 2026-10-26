namespace PcChecker.Core.Software;

/// <summary>
/// Pure final pass shared by the Windows and demo services: removes duplicates, applies protection rules, detects
/// bundles and links parents to children, then sorts by name.
/// </summary>
public static class SoftwareInventoryAssembler
{
    public static SoftwareInventory Assemble(IEnumerable<InstalledProgram> programs, IEnumerable<string>? warnings = null, DateTimeOffset? captured = null)
    {
        ArgumentNullException.ThrowIfNull(programs);
        var list = Deduplicate(programs).Select(ProtectedSoftwareRules.Apply).ToList();
        var bundles = BundleDetector.Detect(list);
        var linked = Link(list, bundles);
        var sorted = linked.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(p => p.Source).ToList();

        // Bundle groups must reference the linked (final) instances so the UI sees consistent objects.
        var byId = sorted.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        var finalBundles = bundles
            .Select(b => new BundleGroup(byId[b.Parent.Id], b.Children.Select(c => byId[c.Id]).ToList(), b.Reason))
            .ToList();

        return new SoftwareInventory(sorted, finalBundles, captured ?? DateTimeOffset.Now, (warnings ?? []).ToList());
    }

    /// <summary>
    /// Drops exact duplicates (same name, version and publisher) that appear in more than one hive, keeping the entry
    /// with the most information (uninstall string, location, size).
    /// </summary>
    public static IReadOnlyList<InstalledProgram> Deduplicate(IEnumerable<InstalledProgram> programs)
    {
        var best = new Dictionary<string, InstalledProgram>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var p in programs)
        {
            var key = p.Source == SoftwareSource.StoreApp
                ? "appx|" + p.PackageFullName
                : $"{p.Name.Trim()}|{p.Version}|{p.Publisher}|{(p.Source == SoftwareSource.User ? "user" : "machine")}";
            if (!best.TryGetValue(key, out var existing))
            {
                best[key] = p;
                order.Add(key);
            }
            else if (Score(p) > Score(existing))
            {
                best[key] = p;
            }
        }

        return order.Select(k => best[k]).ToList();
    }

    /// <summary>Fills <see cref="InstalledProgram.BundledWithId"/> and <see cref="InstalledProgram.BundledChildIds"/> from the detected groups.</summary>
    public static IReadOnlyList<InstalledProgram> Link(IReadOnlyList<InstalledProgram> programs, IReadOnlyList<BundleGroup> bundles)
    {
        var parentOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var childrenOf = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in bundles)
        {
            foreach (var c in g.Children)
            {
                parentOf.TryAdd(c.Id, g.Parent.Id);
                if (!childrenOf.TryGetValue(g.Parent.Id, out var kids))
                {
                    kids = [];
                    childrenOf[g.Parent.Id] = kids;
                }

                if (!kids.Contains(c.Id, StringComparer.OrdinalIgnoreCase))
                {
                    kids.Add(c.Id);
                }
            }
        }

        var byId = programs.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        var byKeyName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in programs)
        {
            var keyName = p.RegistryKey?[(p.RegistryKey.LastIndexOf('\\') + 1)..];
            if (!string.IsNullOrEmpty(keyName))
            {
                byKeyName.TryAdd(keyName, p.Id);
            }
        }

        return programs.Select(p =>
        {
            // Keep a registry-provided parent (ParentKeyName, a bare key name) when it resolves to a known program; otherwise use detection.
            string? bundledWith = null;
            if (p.BundledWithId is not null)
            {
                if (byId.ContainsKey(p.BundledWithId))
                {
                    bundledWith = p.BundledWithId;
                }
                else if (byKeyName.TryGetValue(p.BundledWithId, out var resolved) && !string.Equals(resolved, p.Id, StringComparison.OrdinalIgnoreCase))
                {
                    bundledWith = resolved;
                }
            }

            if (bundledWith is null && parentOf.TryGetValue(p.Id, out var detectedParent))
            {
                bundledWith = detectedParent;
            }

            var kids = childrenOf.TryGetValue(p.Id, out var list) ? (IReadOnlyList<string>)list : [];
            return p with { BundledWithId = bundledWith, BundledChildIds = kids };
        }).ToList();
    }

    private static int Score(InstalledProgram p) =>
        (p.UninstallString is null ? 0 : 2) + (p.QuietUninstallString is null ? 0 : 1) + (p.InstallLocation is null ? 0 : 2)
        + (p.SizeBytes is null ? 0 : 1) + (p.InstallDate is null ? 0 : 1) + (p.Source == SoftwareSource.Machine64 ? 1 : 0);
}
