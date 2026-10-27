namespace PcChecker.Core.DiskSpace;

/// <summary>Where a folder sits, as far as the type breakdown cares. Worked out once per folder, not per file.</summary>
public enum FolderKind
{
    Normal,

    /// <summary>The Windows folder or anything inside it.</summary>
    Windows,

    /// <summary>A game library (Steam, Epic, Xbox, GOG, Battle.net, EA, Ubisoft, Riot…).</summary>
    GameLibrary,

    /// <summary>A Downloads folder, where a plain .exe is almost always an installer.</summary>
    Downloads,
}

/// <summary>
/// Sorts files into the breakdown categories. The folder decides first (everything in the Windows folder is System,
/// everything in a game library is Games), then the extension, then a name hint for installers. Pure.
/// </summary>
public static class FileTypeClassifier
{
    private static readonly Dictionary<string, FileTypeCategory> Extensions = BuildExtensions();

    // Folder names (one or two segments) that hold installed games. Matched anywhere in the path.
    private static readonly string[][] GameLibraryMarkers =
    [
        ["steamapps", "common"],
        ["Epic Games"],
        ["XboxGames"],
        ["GOG Games"],
        ["GOG Galaxy", "Games"],
        ["Riot Games"],
        ["Battle.net"],
        ["Blizzard Entertainment"],
        ["EA Games"],
        ["Electronic Arts"],
        ["Ubisoft Game Launcher", "games"],
        ["Rockstar Games"],
        ["Origin Games"],
        ["Amazon Games", "Library"],
    ];

    private static readonly string[] InstallerNameHints = ["setup", "install", "installer", "redist"];

    /// <summary>The kind of folder <paramref name="folderPath"/> is.</summary>
    public static FolderKind ClassifyFolder(string folderPath, string? windowsDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return FolderKind.Normal;
        }

        if (windowsDirectory is not null
            ? DiskSpacePaths.IsSameOrInside(folderPath, windowsDirectory)
            : IsDefaultWindowsFolder(folderPath))
        {
            return FolderKind.Windows;
        }

        var segments = DiskSpacePaths.Segments(folderPath);
        foreach (var marker in GameLibraryMarkers)
        {
            if (ContainsSequence(segments, marker))
            {
                return FolderKind.GameLibrary;
            }
        }

        return segments.Any(s => s.Equals("Downloads", StringComparison.OrdinalIgnoreCase)) ? FolderKind.Downloads : FolderKind.Normal;
    }

    /// <summary>The category of a file called <paramref name="fileName"/> in a folder of <paramref name="kind"/>.</summary>
    public static FileTypeCategory Classify(ReadOnlySpan<char> fileName, FolderKind kind)
    {
        switch (kind)
        {
            case FolderKind.Windows:
                return FileTypeCategory.System;
            case FolderKind.GameLibrary:
                return FileTypeCategory.Games;
        }

        int dot = fileName.LastIndexOf('.');
        if (dot < 0 || dot == fileName.Length - 1)
        {
            return FileTypeCategory.Other;
        }

        var extension = fileName[(dot + 1)..].ToString().ToLowerInvariant();
        if (extension == "exe")
        {
            return kind == FolderKind.Downloads || LooksLikeInstaller(fileName[..dot]) ? FileTypeCategory.Installers : FileTypeCategory.Other;
        }

        return Extensions.TryGetValue(extension, out var category) ? category : FileTypeCategory.Other;
    }

    /// <summary>The category of the file at <paramref name="fullPath"/>.</summary>
    public static FileTypeCategory Classify(string fullPath, string? windowsDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        var folder = DiskSpacePaths.ParentOf(fullPath) ?? string.Empty;
        return Classify(DiskSpacePaths.NameOf(fullPath).AsSpan(), ClassifyFolder(folder, windowsDirectory));
    }

    private static bool IsDefaultWindowsFolder(string path)
    {
        var segments = DiskSpacePaths.Segments(path);
        return DiskSpacePaths.IsLocalAbsolute(path) && segments.Count > 0 && segments[0].Equals("Windows", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeInstaller(ReadOnlySpan<char> stem)
    {
        foreach (var hint in InstallerNameHints)
        {
            if (stem.Contains(hint, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsSequence(IReadOnlyList<string> segments, string[] marker)
    {
        for (int i = 0; i + marker.Length <= segments.Count; i++)
        {
            bool match = true;
            for (int j = 0; j < marker.Length && match; j++)
            {
                match = segments[i + j].Equals(marker[j], StringComparison.OrdinalIgnoreCase);
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }

    private static Dictionary<string, FileTypeCategory> BuildExtensions()
    {
        var map = new Dictionary<string, FileTypeCategory>(StringComparer.OrdinalIgnoreCase);
        void Add(FileTypeCategory category, params string[] extensions)
        {
            foreach (var e in extensions)
            {
                map[e] = category;
            }
        }

        Add(FileTypeCategory.Video, "mp4", "mkv", "avi", "mov", "wmv", "webm", "m4v", "flv", "mpg", "mpeg", "m2ts", "mts", "ts", "3gp", "vob", "ogv");
        Add(FileTypeCategory.Images, "jpg", "jpeg", "png", "gif", "bmp", "tif", "tiff", "webp", "heic", "heif", "avif", "raw", "cr2", "cr3", "nef",
            "arw", "dng", "orf", "rw2", "psd", "xcf", "svg", "ico", "jxl");
        Add(FileTypeCategory.Games, "pak", "vpk", "upk", "uasset", "ubulk", "uexp", "ucas", "utoc", "forge", "rpf", "bsa", "ba2", "esm", "wad",
            "gcf", "bundle", "assets", "resource", "sav", "pck");
        Add(FileTypeCategory.Archives, "zip", "7z", "rar", "tar", "gz", "tgz", "bz2", "xz", "zst", "lz", "lzma", "cab", "iso", "img", "vhd",
            "vhdx", "vmdk", "dmg", "wim", "esd");
        Add(FileTypeCategory.Installers, "msi", "msix", "msixbundle", "appx", "appxbundle", "msp", "msu", "appinstaller");
        Add(FileTypeCategory.System, "sys", "dll", "drv", "mui", "cat", "inf", "pnf", "mum", "manifest", "evtx", "etl", "dmp", "cpl", "ocx",
            "efi", "msc", "pdb", "regtrans-ms", "blf");
        return map;
    }
}
