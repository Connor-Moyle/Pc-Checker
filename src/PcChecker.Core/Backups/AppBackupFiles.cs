namespace PcChecker.Core.Backups;

/// <summary>
/// Reads what one backup location holds: one item per backup file (its sidecar files counted with it) or per backup
/// folder (its files added up). Plain file I/O that works on any OS; the caller catches I/O errors. Only the location's
/// own folder is read, never anything above it, and links are not followed.
/// </summary>
public static class AppBackupFiles
{
    /// <summary>Folders deeper than this are not added up (a driver backup is two or three levels deep).</summary>
    private const int MaxDepth = 8;

    public static IReadOnlyList<AppBackupItem> List(AppBackupLocation location, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(location);
        switch (location.Kind)
        {
            case BackupItemKind.SingleFile:
            {
                var file = new FileInfo(location.Folder);
                return file.Exists ? [new AppBackupItem(file.FullName, new DateTimeOffset(file.LastWriteTime), file.Length)] : [];
            }

            case BackupItemKind.Folders:
            {
                var root = new DirectoryInfo(location.Folder);
                if (!root.Exists)
                {
                    return [];
                }

                var items = new List<AppBackupItem>();
                foreach (var folder in root.EnumerateDirectories(location.SearchPattern))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (folder.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        continue;
                    }

                    items.Add(new AppBackupItem(folder.FullName, new DateTimeOffset(folder.CreationTime), FolderSize(folder, 0, cancellationToken)));
                }

                return items.OrderByDescending(i => i.Created).ToList();
            }

            default:
            {
                var root = new DirectoryInfo(location.Folder);
                if (!root.Exists)
                {
                    return [];
                }

                var items = new List<AppBackupItem>();
                foreach (var file in root.EnumerateFiles(location.SearchPattern))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (IsCompanion(location, file.Name) || file.Attributes.HasFlag(FileAttributes.ReparsePoint)
                        || file.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    long size = file.Length + Companions(location, file.FullName).Sum(c => new FileInfo(c) is { Exists: true } info ? info.Length : 0);
                    items.Add(new AppBackupItem(file.FullName, new DateTimeOffset(file.LastWriteTime), size));
                }

                return items.OrderByDescending(i => i.Created).ToList();
            }
        }
    }

    /// <summary>The sidecar files that belong to a backup file and go with it when it is deleted.</summary>
    public static IReadOnlyList<string> Companions(AppBackupLocation location, string path)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (location.Kind != BackupItemKind.Files)
        {
            return [];
        }

        return location.CompanionExtensions
            .Select(ext => Path.ChangeExtension(path, ext))
            .Where(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>True when <paramref name="path"/> lies directly inside the location's folder (never above or beside it).</summary>
    public static bool IsInside(AppBackupLocation location, string path)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (string.IsNullOrWhiteSpace(path) || location.Kind == BackupItemKind.SingleFile)
        {
            return false;
        }

        var parent = Path.GetDirectoryName(Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var folder = Path.GetFullPath(location.Folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(parent, folder, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCompanion(AppBackupLocation location, string fileName) =>
        location.CompanionExtensions.Any(ext => fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

    private static long FolderSize(DirectoryInfo folder, int depth, CancellationToken cancellationToken)
    {
        if (depth > MaxDepth)
        {
            return 0;
        }

        long total = 0;
        try
        {
            foreach (var file in folder.EnumerateFiles())
            {
                total += file.Length;
            }

            foreach (var child in folder.EnumerateDirectories())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!child.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    total += FolderSize(child, depth + 1, cancellationToken);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A part that cannot be read counts as empty.
        }

        return total;
    }
}
