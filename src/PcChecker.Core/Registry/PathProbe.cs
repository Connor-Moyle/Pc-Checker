using System.Collections.Concurrent;

namespace PcChecker.Core.RegistryHealth;

/// <summary>What kind of drive a drive letter is, as far as the registry checks care.</summary>
public enum DriveState
{
    /// <summary>A local fixed disk: a missing file there really is missing.</summary>
    Fixed,

    /// <summary>The letter is not mapped right now (unplugged USB disk, disconnected share).</summary>
    Missing,

    /// <summary>USB stick, SD card, optical drive: may simply be unplugged.</summary>
    Removable,

    /// <summary>A mapped network share: may simply be offline.</summary>
    Network,

    /// <summary>Anything else (RAM disk, unknown): never flagged.</summary>
    Other,
}

/// <summary>Result of checking whether a referenced file or folder exists.</summary>
public enum TargetState
{
    Present,
    Missing,

    /// <summary>Cannot be proven either way (network path, removable drive, relative name, unexpanded variable): never flagged.</summary>
    Unknown,
}

/// <summary>
/// The file-system questions the registry checks ask, as replaceable functions so <see cref="RegistryPathTools"/> stays
/// pure and testable. <see cref="CreateSystem"/> answers them from the real machine (with a per-scan drive cache).
/// </summary>
public sealed class PathProbe
{
    public required Func<string, string?> Environment { get; init; }

    public required Func<string, bool> FileExists { get; init; }

    public required Func<string, bool> DirectoryExists { get; init; }

    public required Func<char, DriveState> Drive { get; init; }

    /// <summary>%SystemRoot%, falling back to %windir% and then C:\Windows.</summary>
    public string WindowsDirectory =>
        (Environment("SystemRoot") ?? Environment("windir") ?? @"C:\Windows").TrimEnd('\\');

    /// <summary>Folders Windows searches for a bare program name: System32, SysWOW64, Windows, Wbem and every PATH entry.</summary>
    public IReadOnlyList<string> SearchDirectories()
    {
        var windows = WindowsDirectory;
        var dirs = new List<string>
        {
            windows + @"\System32",
            windows + @"\SysWOW64",
            windows,
            windows + @"\System32\Wbem",
            windows + @"\System32\WindowsPowerShell\v1.0",
        };

        var path = Environment("PATH") ?? Environment("Path");
        if (!string.IsNullOrWhiteSpace(path))
        {
            foreach (var entry in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var expanded = RegistryPathTools.Expand(entry.Trim('"'), Environment).TrimEnd('\\');
                if (expanded.Length > 0 && !expanded.Contains('%') && !dirs.Contains(expanded, StringComparer.OrdinalIgnoreCase))
                {
                    dirs.Add(expanded);
                }
            }
        }

        return dirs;
    }

    /// <summary>A probe backed by the real file system and environment. Drive lookups are cached for the probe's lifetime.</summary>
    public static PathProbe CreateSystem()
    {
        var drives = new ConcurrentDictionary<char, DriveState>();
        return new PathProbe
        {
            Environment = System.Environment.GetEnvironmentVariable,
            FileExists = SafeFileExists,
            DirectoryExists = SafeDirectoryExists,
            Drive = letter => drives.GetOrAdd(char.ToUpperInvariant(letter), QueryDrive),
        };
    }

    private static bool SafeFileExists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    private static bool SafeDirectoryExists(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    private static DriveState QueryDrive(char letter)
    {
        try
        {
            var info = new DriveInfo(letter + @":\");
            return info.DriveType switch
            {
                DriveType.Fixed => info.IsReady ? DriveState.Fixed : DriveState.Other,
                DriveType.NoRootDirectory => DriveState.Missing,
                DriveType.Removable or DriveType.CDRom => DriveState.Removable,
                DriveType.Network => DriveState.Network,
                _ => DriveState.Other,
            };
        }
        catch
        {
            return DriveState.Other;
        }
    }
}
