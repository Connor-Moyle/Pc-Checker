using System.Diagnostics;
using PcChecker.Core.Common;

namespace PcChecker.Core.Cleanup;

/// <summary>
/// Facts about the running machine the scanner and cleaner both need: environment variables, which other user profiles
/// may be included (elevated only), the folders PC Checker itself writes to, and a guarded directory lister.
/// </summary>
internal sealed class CleanupEnvironment
{
    private CleanupEnvironment(bool isElevated, IReadOnlyList<string> otherUserProfiles, IReadOnlyList<string> protectedDirectories)
    {
        IsElevated = isElevated;
        OtherUserProfiles = otherUserProfiles;
        ProtectedDirectories = protectedDirectories;
    }

    public bool IsElevated { get; }

    /// <summary>Profile folders of other accounts (empty unless elevated).</summary>
    public IReadOnlyList<string> OtherUserProfiles { get; }

    /// <summary>Folders never cleaned: the app's own log and settings directories.</summary>
    public IReadOnlyList<string> ProtectedDirectories { get; }

    public static CleanupEnvironment Capture(IEnumerable<string?> protectedDirectories)
    {
        bool elevated = CleanupNativeMethods.IsElevated();
        var protectedList = protectedDirectories.Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => d!).ToList();
        return new CleanupEnvironment(elevated, elevated ? FindOtherUserProfiles() : [], protectedList);
    }

    public static string? GetEnvironmentVariable(string name) => Environment.GetEnvironmentVariable(name);

    /// <summary>Child folder names of <paramref name="parent"/>; empty when it does not exist or is inaccessible.</summary>
    public static IEnumerable<string> ListDirectoryNames(string parent)
    {
        try
        {
            if (!Directory.Exists(parent))
            {
                return [];
            }

            return Directory.EnumerateDirectories(parent, "*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    public ResolvedRoots Resolve(CleanupCategory category) =>
        CleanupPathResolver.Resolve(category, GetEnvironmentVariable, OtherUserProfiles, ListDirectoryNames);

    public bool IsProtected(string path) => ProtectedDirectories.Any(d => CleanupRules.IsUnderDirectory(path, d));

    /// <summary>
    /// Names (without .exe) of the browser and installer processes currently running, including versioned vendor
    /// self-extractors (<see cref="CleanupRules.InstallerNameFragments"/>).
    /// </summary>
    public static IReadOnlyList<string> RunningBrowserProcesses()
    {
        var running = new List<string>();
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    try
                    {
                        if (CleanupRules.IsVersionedInstallerName(p.ProcessName))
                        {
                            running.Add(p.ProcessName);
                        }
                    }
                    catch
                    {
                        // A process that exited or cannot be read is not an installer we have to wait for.
                    }
                }
            }
        }
        catch
        {
            // Process enumeration failed; the exact-name checks below and the fresh-file rule still apply.
        }

        foreach (var name in CleanupRules.AllBrowserProcessNames)
        {
            try
            {
                var processes = Process.GetProcessesByName(name);
                if (processes.Length > 0)
                {
                    running.Add(name);
                }

                foreach (var p in processes)
                {
                    p.Dispose();
                }
            }
            catch
            {
                // Process enumeration can fail for protected processes; treat as not running.
            }
        }

        return running;
    }

    private static IReadOnlyList<string> FindOtherUserProfiles()
    {
        try
        {
            var current = Environment.GetEnvironmentVariable("USERPROFILE");
            var usersRoot = current is null ? null : Path.GetDirectoryName(current.TrimEnd('\\'));
            if (string.IsNullOrEmpty(usersRoot) || !Directory.Exists(usersRoot))
            {
                return [];
            }

            return Directory.EnumerateDirectories(usersRoot, "*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .Where(d => !CleanupRules.IsUnderDirectory(d, current) && CleanupPathResolver.IsRealUserProfile(Path.GetFileName(d)))
                .ToList();
        }
        catch
        {
            return [];
        }
    }
}
