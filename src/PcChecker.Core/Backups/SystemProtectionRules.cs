using System.Text.RegularExpressions;

namespace PcChecker.Core.Backups;

/// <summary>One drive that System Protection covers, from the SPP client list.</summary>
public sealed record ProtectedVolume(string? VolumeId, string? Drive);

/// <summary>
/// Which drives System Protection covers. Windows lists them under
/// <c>HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SPP\Clients</c> in the multi-string value named
/// <see cref="SystemRestoreClientValue"/>, one entry per drive such as <c>\\?\Volume{4c1b…}\:(C%3A)</c>. The entries
/// are paths and URL-escaped drive letters, never words, so reading them works in every Windows language.
/// </summary>
public static partial class SystemProtectionRules
{
    public const string SppClientsKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SPP\Clients";

    /// <summary>The System Restore client id; its value lists the protected volumes.</summary>
    public const string SystemRestoreClientValue = "{09F7EDC5-294E-4180-AF6A-FB0E6A0E9513}";

    /// <summary>Group policy that turns System Restore off for the whole PC.</summary>
    public const string PolicyKey = @"SOFTWARE\Policies\Microsoft\Windows NT\SystemRestore";

    /// <summary>The smallest and largest limit, in percent of the drive, that the page offers.</summary>
    public const int MinPercent = 1;

    public const int MaxPercent = 100;

    /// <summary>The limit Windows suggests for a new drive, and a sensible default.</summary>
    public const int SuggestedPercent = 5;

    [GeneratedRegex(@"\\\\\?\\Volume\{[0-9A-Fa-f-]+\}\\", RegexOptions.CultureInvariant)]
    private static partial Regex VolumeIdPattern();

    [GeneratedRegex(@"\(([A-Za-z])(?:%3A|:)\)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DrivePattern();

    /// <summary>Parses the entries of the System Restore client value. Entries with neither a volume nor a drive are skipped.</summary>
    public static IReadOnlyList<ProtectedVolume> ParseClients(IEnumerable<string?>? entries)
    {
        var list = new List<ProtectedVolume>();
        foreach (var entry in entries ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            var volume = VolumeIdPattern().Match(entry);
            var drive = DrivePattern().Match(entry);
            var item = new ProtectedVolume(
                volume.Success ? volume.Value : null,
                drive.Success ? drive.Groups[1].Value.ToUpperInvariant() + ":" : null);
            if (item.VolumeId is not null || item.Drive is not null)
            {
                list.Add(item);
            }
        }

        return list;
    }

    /// <summary>
    /// Whether <paramref name="drive"/> (for example "C:") is protected. Matches the drive letter, or the volume id when
    /// <paramref name="volumeId"/> is known, so a drive whose letter changed is still recognised.
    /// </summary>
    public static bool IsProtected(IEnumerable<ProtectedVolume> protectedVolumes, string drive, string? volumeId = null)
    {
        var letter = NormalizeDrive(drive);
        return (protectedVolumes ?? []).Any(v =>
            (letter is not null && string.Equals(v.Drive, letter, StringComparison.OrdinalIgnoreCase))
            || (volumeId is not null && v.VolumeId is not null && string.Equals(v.VolumeId, volumeId, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>"c", "C:", "C:\" → "C:"; null for anything that is not a drive letter.</summary>
    public static string? NormalizeDrive(string? drive)
    {
        if (string.IsNullOrWhiteSpace(drive))
        {
            return null;
        }

        var text = drive.Trim().TrimEnd('\\');
        if (text.Length == 1 && char.IsAsciiLetter(text[0]))
        {
            return char.ToUpperInvariant(text[0]) + ":";
        }

        return text.Length == 2 && char.IsAsciiLetter(text[0]) && text[1] == ':' ? char.ToUpperInvariant(text[0]) + ":" : null;
    }

    /// <summary>The value for <c>vssadmin resize shadowstorage /MaxSize=</c>: a whole percentage between 1 and 100.</summary>
    public static string MaxSizeArgument(int percent) =>
        "/MaxSize=" + Math.Clamp(percent, MinPercent, MaxPercent).ToString(System.Globalization.CultureInfo.InvariantCulture) + "%";

    /// <summary>The value that puts a limit back: bytes, or UNBOUNDED for no limit.</summary>
    public static string MaxSizeArgument(ShadowStorageInfo previous)
    {
        ArgumentNullException.ThrowIfNull(previous);
        return previous.IsUnbounded || previous.MaxBytes <= 0
            ? "/MaxSize=UNBOUNDED"
            : "/MaxSize=" + previous.MaxBytes.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The whole percentage to show in the box for a drive: its current limit, or the suggestion.</summary>
    public static int PercentForEditing(ShadowStorageInfo? storage) =>
        storage?.MaxPercent is { } percent ? (int)Math.Clamp(Math.Round(percent), MinPercent, MaxPercent) : SuggestedPercent;
}
