namespace PcChecker.Core.Cleanup;

/// <summary>Concrete folders a category resolves to, plus which extra user profiles were included.</summary>
public sealed record ResolvedRoots(IReadOnlyList<string> Roots, IReadOnlyList<string> UsersScanned);

/// <summary>
/// Turns catalog templates into real folders: per-user variables are substituted for every user profile when running
/// elevated, and wildcard segments ('*' / '?') are matched against the directory listing supplied by the caller.
/// Pure: every file-system question goes through a delegate so the logic is testable anywhere.
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
                    children = listDirectoryNames(parent) ?? [];
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
    /// each profile in <paramref name="otherUserProfiles"/> (pass an empty list when not elevated), with wildcards expanded.
    /// </summary>
    public static ResolvedRoots Resolve(
        CleanupCategory category,
        Func<string, string?> getEnvironmentVariable,
        IReadOnlyList<string> otherUserProfiles,
        Func<string, IEnumerable<string>> listDirectoryNames)
    {
        ArgumentNullException.ThrowIfNull(category);
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        ArgumentNullException.ThrowIfNull(otherUserProfiles);
        ArgumentNullException.ThrowIfNull(listDirectoryNames);

        var roots = new List<string>();
        var users = new List<string>();

        foreach (var template in category.PathTemplates)
        {
            AddExpanded(roots, CleanupCatalog.ExpandTemplate(template, getEnvironmentVariable), listDirectoryNames);
            if (!IsPerUserTemplate(template))
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
                int before = roots.Count;
                AddExpanded(roots, CleanupCatalog.ExpandTemplate(template, env), listDirectoryNames);
                if (roots.Count > before && !users.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    users.Add(name);
                }
            }
        }

        return new ResolvedRoots(roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), users);
    }

    private static void AddExpanded(List<string> roots, string expanded, Func<string, IEnumerable<string>> listDirectoryNames)
    {
        if (expanded.Contains('%'))
        {
            // An unknown variable stayed in the text; such a root can never be valid.
            return;
        }

        roots.AddRange(ExpandWildcards(expanded, listDirectoryNames));
    }
}
