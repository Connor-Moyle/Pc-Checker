namespace PcChecker.Core.Cleanup;

/// <summary>One concrete folder a category scans, with its file patterns and whether sub-folders are included.</summary>
public sealed record ResolvedRoot(string Path, IReadOnlyList<string> Patterns, bool Recurse);

/// <summary>Concrete folders a category resolves to, plus which extra user profiles were included.</summary>
public sealed record ResolvedRoots(IReadOnlyList<string> Roots, IReadOnlyList<string> UsersScanned)
{
    /// <summary>The same folders with their patterns and recursion (several entries may share a folder).</summary>
    public IReadOnlyList<ResolvedRoot> Entries { get; init; } = [];

    /// <summary>Concrete folders that are never scanned or deleted in this category.</summary>
    public IReadOnlyList<string> ExcludedPaths { get; init; } = [];
}

/// <summary>
/// Turns catalog templates into real folders: per-user variables are substituted for every user profile when running
/// elevated, multi-value variables (Steam libraries, fixed drives) give one folder per value, and wildcard segments
/// ('*' / '?') are matched against the directory listing supplied by the caller. Pure: every file-system question goes
/// through a delegate so the logic is testable anywhere.
/// </summary>
public static class CleanupPathResolver
{
    /// <summary>Variables that point inside a user profile (they get re-targeted when scanning other users).</summary>
    public static readonly IReadOnlyList<string> PerUserVariables = ["TEMP", "TMP", "LOCALAPPDATA", "APPDATA", "USERPROFILE"];

    /// <summary>Profile folders under C:\Users that are not real accounts.</summary>
    public static readonly IReadOnlyList<string> NonUserProfiles = ["Default", "Default User", "Public", "All Users", "WDAGUtilityAccount"];

    /// <summary>True when the template references a variable that lives inside a user profile.</summary>
    public static bool IsPerUserTemplate(string template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return PerUserVariables.Any(v => template.Contains("%" + v + "%", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True when the path has a '*' or '?' in any segment.</summary>
    public static bool HasWildcard(string path) => path.IndexOfAny(['*', '?']) >= 0;

    /// <summary>True when a C:\Users sub-folder name is a real account rather than a template or shared profile.</summary>
    public static bool IsRealUserProfile(string profileFolderName) =>
        !string.IsNullOrWhiteSpace(profileFolderName)
        && !NonUserProfiles.Contains(profileFolderName.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when <paramref name="path"/> may be a cleanup root: a local, absolute path (X:\…) that is not a whole drive.
    /// Network (UNC) paths, device paths and relative paths are refused.
    /// </summary>
    public static bool IsAcceptableRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var p = path.Trim().Replace('/', '\\').TrimEnd('\\');
        if (p.StartsWith(@"\\", StringComparison.Ordinal) || p.Length < 3)
        {
            return false;
        }

        return char.IsAsciiLetter(p[0]) && p[1] == ':' && p[2] == '\\';
    }

    /// <summary>
    /// Builds an environment lookup that answers per-user variables for <paramref name="profileDirectory"/>
    /// and falls back to <paramref name="getEnvironmentVariable"/> for everything else.
    /// </summary>
    public static Func<string, string?> EnvironmentForProfile(string profileDirectory, Func<string, string?> getEnvironmentVariable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileDirectory);
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        var profile = profileDirectory.TrimEnd('\\');
        return name => name.ToUpperInvariant() switch
        {
            "USERPROFILE" => profile,
            "LOCALAPPDATA" => profile + @"\AppData\Local",
            "APPDATA" => profile + @"\AppData\Roaming",
            "TEMP" or "TMP" => profile + @"\AppData\Local\Temp",
            _ => getEnvironmentVariable(name),
        };
    }

    /// <summary>
    /// Replaces each multi-value variable in <paramref name="template"/> by every value <paramref name="multiValues"/>
    /// returns for it, giving one template per combination. A variable with no values removes the template.
    /// </summary>
    public static IReadOnlyList<string> ExpandMultiValue(string template, Func<string, IReadOnlyList<string>>? multiValues)
    {
        ArgumentNullException.ThrowIfNull(template);
        IReadOnlyList<string> current = [template];
        foreach (var variable in CleanupCatalog.MultiValueVariables)
        {
            var token = "%" + variable + "%";
            if (!template.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var values = (multiValues?.Invoke(variable) ?? [])
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim().TrimEnd('\\', '/'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            current = current
                .SelectMany(t => values.Select(v => t.Replace(token, v, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        return current;
    }

    /// <summary>
    /// Expands wildcard segments in <paramref name="path"/>. <paramref name="listDirectoryNames"/> returns the child
    /// folder names of a parent folder (empty when it does not exist). Paths without wildcards are returned as-is.
    /// </summary>
    public static IReadOnlyList<string> ExpandWildcards(string path, Func<string, IEnumerable<string>> listDirectoryNames)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(listDirectoryNames);
        if (!HasWildcard(path))
        {
            return [path];
        }

        var segments = path.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || HasWildcard(segments[0]))
        {
            return [];
        }

        var current = new List<string> { segments[0] };
        for (int i = 1; i < segments.Length; i++)
        {
            var segment = segments[i];
            var next = new List<string>();
            foreach (var parent in current)
            {
                if (!HasWildcard(segment))
                {
                    next.Add(parent + "\\" + segment);
                    continue;
                }

                IEnumerable<string> children;
                try
                {
                    // A drive letter alone ("D:") lists the current folder on that drive; ask for its root instead.
                    children = listDirectoryNames(parent.Length == 2 && parent[1] == ':' ? parent + "\\" : parent) ?? [];
                }
                catch
                {
                    continue;
                }

                foreach (var child in children)
                {
                    var name = CleanupSafety.FileNameOf(child.TrimEnd('\\'));
                    if (name.Length > 0 && CleanupSafety.MatchesPattern(name, segment))
                    {
                        next.Add(parent + "\\" + name);
                    }
                }
            }

            current = next;
            if (current.Count == 0)
            {
                break;
            }
        }

        return current.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Resolves every concrete root for <paramref name="category"/>: the current user's folders, the same folders for
    /// each profile in <paramref name="otherUserProfiles"/> (pass an empty list when not elevated), with multi-value
    /// variables and wildcards expanded. Folders marked elevated-only are left out unless <paramref name="isElevated"/>.
    /// Roots inside an excluded folder are dropped. A <see cref="CleanupCategory.CurrentUserOnly"/> category never
    /// reaches other profiles.
    /// </summary>
    public static ResolvedRoots Resolve(
        CleanupCategory category,
        Func<string, string?> getEnvironmentVariable,
        IReadOnlyList<string> otherUserProfiles,
        Func<string, IEnumerable<string>> listDirectoryNames,
        Func<string, IReadOnlyList<string>>? multiValues = null,
        bool isElevated = true)
    {
        ArgumentNullException.ThrowIfNull(category);
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        ArgumentNullException.ThrowIfNull(otherUserProfiles);
        ArgumentNullException.ThrowIfNull(listDirectoryNames);

        if (category.CurrentUserOnly)
        {
            otherUserProfiles = [];
        }

        var users = new List<string>();
        var excluded = category.ExcludePathTemplates
            .SelectMany(t => ExpandForAllUsers(t, getEnvironmentVariable, otherUserProfiles, listDirectoryNames, multiValues, null))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var entries = new List<ResolvedRoot>();
        foreach (var path in category.Paths)
        {
            if (path.ElevatedOnly && !isElevated)
            {
                continue;
            }

            foreach (var root in ExpandForAllUsers(path.Template, getEnvironmentVariable, otherUserProfiles, listDirectoryNames, multiValues, users))
            {
                if (excluded.Any(x => CleanupRules.IsUnderDirectory(root, x)))
                {
                    continue;
                }

                var entry = new ResolvedRoot(root, path.Patterns ?? category.IncludePatterns, path.Recurse ?? category.Recurse);
                if (!entries.Any(e => e.Path.Equals(entry.Path, StringComparison.OrdinalIgnoreCase) && e.Recurse == entry.Recurse && e.Patterns.SequenceEqual(entry.Patterns)))
                {
                    entries.Add(entry);
                }
            }
        }

        var roots = entries.Select(e => e.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new ResolvedRoots(roots, users) { Entries = entries, ExcludedPaths = excluded };
    }

    private static List<string> ExpandForAllUsers(string template, Func<string, string?> getEnvironmentVariable, IReadOnlyList<string> otherUserProfiles,
        Func<string, IEnumerable<string>> listDirectoryNames, Func<string, IReadOnlyList<string>>? multiValues, List<string>? users)
    {
        var result = new List<string>();
        foreach (var expandedTemplate in ExpandMultiValue(template, multiValues))
        {
            AddExpanded(result, CleanupCatalog.ExpandTemplate(expandedTemplate, getEnvironmentVariable), listDirectoryNames);
            if (!IsPerUserTemplate(expandedTemplate))
            {
                continue;
            }

            foreach (var profile in otherUserProfiles)
            {
                var name = CleanupSafety.FileNameOf(profile.TrimEnd('\\'));
                if (!IsRealUserProfile(name))
                {
                    continue;
                }

                var env = EnvironmentForProfile(profile, getEnvironmentVariable);
                int before = result.Count;
                AddExpanded(result, CleanupCatalog.ExpandTemplate(expandedTemplate, env), listDirectoryNames);
                if (users is not null && result.Count > before && !users.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    users.Add(name);
                }
            }
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void AddExpanded(List<string> roots, string expanded, Func<string, IEnumerable<string>> listDirectoryNames)
    {
        if (expanded.Contains('%'))
        {
            // An unknown variable stayed in the text; such a root can never be valid.
            return;
        }

        // Network shares, relative paths and whole drives never become roots.
        roots.AddRange(ExpandWildcards(expanded, listDirectoryNames).Where(IsAcceptableRoot));
    }
}
