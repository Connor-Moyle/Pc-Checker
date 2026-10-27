using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.DiskSpace;
using PcChecker.Core.RegistryHealth;

namespace PcChecker.Core.Leftovers;

/// <summary>Folder and Start menu checks. Links are never followed; folders that cannot be read completely are not reported.</summary>
public sealed partial class WindowsLeftoverService
{
    /// <summary>A folder walk stops after this many entries; the folder is then reported with the size found so far.</summary>
    private const int MaxEntriesPerFolder = 200_000;

    private static readonly string[] ProgramExtensions = [".exe", ".dll", ".sys", ".msi", ".com", ".scr"];

    /// <summary>What a walk of one folder found.</summary>
    private sealed record FolderStats(long Files, long Bytes, bool ContainsPrograms, DateTime? LastWriteUtc, bool Complete, bool Truncated);

    private void ScanFolders(ScanContext context, List<LeftoverItem> items, IProgress<ProgressUpdate>? progress, CancellationToken ct)
    {
        for (int r = 0; r < context.Roots.Count; r++)
        {
            var root = context.Roots[r];
            progress?.Report(new ProgressUpdate("Checking program folders", 10 + 50.0 * r / context.Roots.Count, root.Path));
            foreach (var dir in ChildFolders(root.Path))
            {
                ct.ThrowIfCancellationRequested();
                var name = dir.Name;
                if (LeftoverAllowList.IsAllowedFolder(root.Root, name) || IsLinkOrSystem(dir))
                {
                    continue;
                }

                var path = DiskSpacePaths.Normalize(dir.FullName);
                bool owned = context.Index.OwnsPath(path);
                bool named = !owned && context.Index.MatchName(name) is not null;
                if (owned || named)
                {
                    // A maker's folder that still holds an installed program: look one level down for products that went.
                    if (root.Root is LeftoverRoot.ProgramFiles or LeftoverRoot.ProgramFilesX86 or LeftoverRoot.ProgramData &&
                        (context.Index.ContainsInstall(path) || named))
                    {
                        ScanVendorFolder(context, root, dir, items, ct);
                    }

                    continue;
                }

                AddFolderCandidate(context, root, dir, underVendor: false, items, ct);
            }
        }
    }

    private void ScanVendorFolder(ScanContext context, LeftoverRootFolder root, DirectoryInfo vendor, List<LeftoverItem> items, CancellationToken ct)
    {
        foreach (var child in ChildFolders(vendor.FullName))
        {
            ct.ThrowIfCancellationRequested();
            if (LeftoverAllowList.IsSharedInsideVendor(child.Name) || IsLinkOrSystem(child))
            {
                continue;
            }

            var path = DiskSpacePaths.Normalize(child.FullName);
            if (context.Index.OwnsPath(path) || context.Index.MatchName(child.Name) is not null)
            {
                continue;
            }

            AddFolderCandidate(context, root, child, underVendor: true, items, ct);
        }
    }

    private void AddFolderCandidate(ScanContext context, LeftoverRootFolder root, DirectoryInfo dir, bool underVendor, List<LeftoverItem> items, CancellationToken ct)
    {
        var path = DiskSpacePaths.Normalize(dir.FullName);
        if (LeftoverPathRules.WhyFolderRefused(path, context.Roots) is not null)
        {
            return;
        }

        var stats = Measure(dir, ct);
        if (!stats.Complete)
        {
            // Parts of it could not be read: it may belong to a service or another account. Never guess.
            _logger.LogDebug("Leftover candidate {Path} skipped: it could not be read completely.", path);
            return;
        }

        var kind = stats.Files == 0 ? LeftoverKind.EmptyFolder : LeftoverKind.Folder;
        DateTime? lastWrite = stats.LastWriteUtc ?? SafeLastWrite(dir);
        TimeSpan? age = lastWrite is { } utc ? context.Now.UtcDateTime - utc : null;
        bool focus = LeftoverRules.MatchesFocus(dir.Name, path, context.Focus);
        var evidence = new LeftoverEvidence(kind, focus, underVendor, stats.ContainsPrograms, age,
            InUserData: root.Root is LeftoverRoot.RoamingAppData or LeftoverRoot.LocalLowAppData);
        string? note = stats.Truncated ? "Very large: the size shown is what was counted before the scan stopped counting." : null;
        string name = underVendor && dir.Parent is { } parent ? $"{dir.Name} ({parent.Name})" : dir.Name;
        items.Add(Create(kind, name, path, evidence, stats.Bytes, canRemove: true, target: null, note, context.Focus?.ProgramName,
            lastWrite is { } w ? new DateTimeOffset(w, TimeSpan.Zero) : null));
    }

    /// <summary>Walks a folder without following links, counting files and bytes and noting program files and the last change.</summary>
    private static FolderStats Measure(DirectoryInfo dir, CancellationToken ct)
    {
        long files = 0, bytes = 0, entries = 0;
        bool programs = false, complete = true, truncated = false;
        DateTime? last = null;
        var pending = new Stack<DirectoryInfo>();
        pending.Push(dir);
        var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0, ReturnSpecialDirectories = false };

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var current = pending.Pop();
            try
            {
                foreach (var entry in current.EnumerateFileSystemInfos("*", options))
                {
                    if (++entries > MaxEntriesPerFolder)
                    {
                        truncated = true;
                        pending.Clear();
                        break;
                    }

                    var written = entry.LastWriteTimeUtc;
                    if (last is null || written > last)
                    {
                        last = written;
                    }

                    if (entry is DirectoryInfo sub)
                    {
                        // A link inside is neither followed nor counted; the folder is then not "empty" either.
                        if ((sub.Attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            files++;
                            continue;
                        }

                        pending.Push(sub);
                    }
                    else if (entry is FileInfo file)
                    {
                        files++;
                        bytes += SafeLength(file);
                        programs |= ProgramExtensions.Contains(file.Extension, StringComparer.OrdinalIgnoreCase);
                    }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                complete = false;
                break;
            }
        }

        return new FolderStats(files, bytes, programs, last, complete, truncated);
    }

    private void ScanShortcuts(ScanContext context, List<LeftoverItem> items, CancellationToken ct)
    {
        var folders = StartMenuFolders();
        var resolver = ShortcutResolver.TryCreate();
        if (resolver is null)
        {
            _logger.LogInformation("Shortcuts cannot be read on this PC (Windows Script Host is unavailable); the Start menu is skipped.");
            return;
        }

        foreach (var folder in folders.Where(Directory.Exists))
        {
            foreach (var file in ShortcutFiles(folder, ct))
            {
                ct.ThrowIfCancellationRequested();
                var path = DiskSpacePaths.Normalize(file.FullName);
                if (LeftoverPathRules.WhyShortcutRefused(path, folders) is not null || IsLinkOrSystem(file))
                {
                    continue;
                }

                var target = resolver.Target(path);
                if (string.IsNullOrWhiteSpace(target) || LeftoverRules.IsWindowsImage(target, WindowsDirectory) ||
                    RegistryPathTools.Evaluate(target, context.Probe) != TargetState.Missing)
                {
                    continue;
                }

                var parent = DiskSpacePaths.ParentOf(RegistryPathTools.Expand(target, _environment));
                bool folderMissing = parent is not null && !context.Probe.DirectoryExists(parent);
                string name = Path.GetFileNameWithoutExtension(file.Name);
                bool focus = LeftoverRules.MatchesFocus(name, target, context.Focus);
                var evidence = new LeftoverEvidence(LeftoverKind.Shortcut, focus, TargetFolderMissing: folderMissing);
                items.Add(Create(LeftoverKind.Shortcut, name, path, evidence, SafeLength(file), canRemove: true, target, null,
                    context.Focus?.ProgramName, new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero)));
            }
        }
    }

    private static IEnumerable<FileInfo> ShortcutFiles(string folder, CancellationToken ct)
    {
        var pending = new Stack<(DirectoryInfo Dir, int Depth)>();
        pending.Push((new DirectoryInfo(folder), 1));
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, depth) = pending.Pop();
            FileSystemInfo[] entries;
            try
            {
                entries = dir.GetFileSystemInfos();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (entry is DirectoryInfo sub)
                {
                    if (depth < LeftoverPathRules.MaxShortcutDepth && (sub.Attributes & FileAttributes.ReparsePoint) == 0)
                    {
                        pending.Push((sub, depth + 1));
                    }
                }
                else if (entry is FileInfo file && file.Extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    yield return file;
                }
            }
        }
    }

    private IEnumerable<DirectoryInfo> ChildFolders(string path)
    {
        try
        {
            return new DirectoryInfo(path).GetDirectories();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            _logger.LogDebug(ex, "Could not list {Path}.", path);
            return [];
        }
    }

    private static bool IsLinkOrSystem(FileSystemInfo info)
    {
        try
        {
            return (info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.System)) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static long SafeLength(FileInfo file)
    {
        try
        {
            return file.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static DateTime? SafeLastWrite(FileSystemInfo info)
    {
        try
        {
            return info.LastWriteTimeUtc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Reads .lnk targets through the Windows Script Host shell object (late bound, one instance per scan).</summary>
    private sealed class ShortcutResolver
    {
        private readonly dynamic _shell;

        private ShortcutResolver(object shell) => _shell = shell;

        public static ShortcutResolver? TryCreate()
        {
            if (!PlatformGuard.IsWindows)
            {
                return null;
            }

            try
            {
                var type = Type.GetTypeFromProgID("WScript.Shell");
                return type is null || Activator.CreateInstance(type) is not { } shell ? null : new ShortcutResolver(shell);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>The target file of a shortcut; null for advertised (installer) shortcuts, URLs and unreadable files.</summary>
        public string? Target(string path)
        {
            try
            {
                dynamic link = _shell.CreateShortcut(path);
                string target = (string?)link.TargetPath ?? string.Empty;
                return string.IsNullOrWhiteSpace(target) || target.Contains("://", StringComparison.Ordinal) ? null : target.Trim();
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
