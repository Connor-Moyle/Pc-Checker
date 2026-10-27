using System.Globalization;
using System.Text;
using Microsoft.Win32;
using PcChecker.Core.Common;
using PcChecker.Core.Optimization;

namespace PcChecker.Core.Memory;

/// <summary>
/// The page file configuration from before "Let Windows manage the page file" was applied: whether Windows managed
/// it, and every <c>Win32_PageFileSetting</c> row (drive, initial and maximum MB; 0/0 is "system managed size").
/// Revert restores exactly this, including "no page file at all".
/// </summary>
public sealed record PageFileSnapshot(bool Automatic, IReadOnlyList<PageFileInfo> PageFiles)
{
    /// <summary>Takes the snapshot from an overview. Rows Windows lists for automatic management ("?:") are left out.</summary>
    public static PageFileSnapshot From(MemoryOverview overview)
    {
        ArgumentNullException.ThrowIfNull(overview);
        var files = overview.AutomaticPageFile
            ? []
            : overview.PageFiles.Where(f => IsDrive(f.Drive)).Select(f => f with { Drive = f.Drive.ToUpperInvariant() }).ToList();
        return new PageFileSnapshot(overview.AutomaticPageFile, files);
    }

    /// <summary>Stored form, for example <c>auto=0|C:=0-0|D:=4096-8192</c>.</summary>
    public string Format()
    {
        var sb = new StringBuilder(Automatic ? "auto=1" : "auto=0");
        foreach (var f in PageFiles)
        {
            sb.Append('|').Append(f.Drive.ToUpperInvariant()).Append('=')
                .Append(f.InitialMb.ToString(CultureInfo.InvariantCulture)).Append('-').Append(f.MaximumMb.ToString(CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    /// <summary>Reads the stored form back; null when it is missing or not understood (then nothing is restored).</summary>
    public static PageFileSnapshot? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var parts = text.Trim().Split('|');
        bool automatic;
        switch (parts[0])
        {
            case "auto=1":
                automatic = true;
                break;
            case "auto=0":
                automatic = false;
                break;
            default:
                return null;
        }

        var files = new List<PageFileInfo>();
        foreach (var part in parts.Skip(1))
        {
            int eq = part.IndexOf('=');
            int dash = part.IndexOf('-', eq + 1);
            if (eq != 2 || dash < 0)
            {
                return null;
            }

            string drive = part[..2].ToUpperInvariant();
            if (!IsDrive(drive)
                || !int.TryParse(part[(eq + 1)..dash], NumberStyles.None, CultureInfo.InvariantCulture, out int initial)
                || !int.TryParse(part[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int maximum))
            {
                return null;
            }

            files.Add(new PageFileInfo(drive, initial, maximum, initial == 0 && maximum == 0));
        }

        return new PageFileSnapshot(automatic, files);
    }

    /// <summary>Plain-words description, for example "C: system managed, D: 4096–8192 MB".</summary>
    public string Describe()
    {
        if (Automatic)
        {
            return "page file managed by Windows";
        }

        if (PageFiles.Count == 0)
        {
            return "no page file";
        }

        return string.Join(", ", PageFiles.Select(f => f.SystemManaged ? $"{f.Drive} system managed" : $"{f.Drive} {f.InitialMb}–{f.MaximumMb} MB"));
    }

    private static bool IsDrive(string? drive) =>
        drive is { Length: 2 } && char.IsAsciiLetter(drive[0]) && drive[1] == ':';
}

/// <summary>Remembers the page file configuration from before an Optimize action changed it, so Revert can put it back.</summary>
public interface IPageFileSnapshotStore
{
    string? Get();

    void Set(string value);

    void Remove();
}

/// <summary>
/// Keeps the snapshot in HKCU\Software\PcChecker\Optimization (next to the other remembered Optimize values) so Revert
/// still works after the app has been restarted. Off Windows it is only kept in memory.
/// </summary>
public sealed class RegistryPageFileSnapshotStore : IPageFileSnapshotStore
{
    public const string Key = @"Software\PcChecker\Optimization";
    public const string ValueName = "PreviousPageFiles";

    private string? _memory;

    public string? Get() => PlatformGuard.IsWindows ? RegistryTweaks.GetString(RegistryHive.CurrentUser, Key, ValueName) : _memory;

    public void Set(string value)
    {
        if (PlatformGuard.IsWindows)
        {
            RegistryTweaks.SetString(RegistryHive.CurrentUser, Key, ValueName, value);
        }
        else
        {
            _memory = value;
        }
    }

    public void Remove()
    {
        if (PlatformGuard.IsWindows)
        {
            RegistryTweaks.DeleteValue(RegistryHive.CurrentUser, Key, ValueName);
        }
        else
        {
            _memory = null;
        }
    }
}
