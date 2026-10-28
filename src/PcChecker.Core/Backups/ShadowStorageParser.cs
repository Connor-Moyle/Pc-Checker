using System.Globalization;
using System.Text.RegularExpressions;
using PcChecker.Core.Cleanup;

namespace PcChecker.Core.Backups;

/// <summary>One "Shadow Copy Storage association" block of <c>vssadmin list shadowstorage</c>.</summary>
public sealed record VssStorageBlock(string? ForDrive, string? ForVolumeId, long? UsedBytes, long? AllocatedBytes, long? MaxBytes, bool MaxUnbounded,
    double? UsedPercent, double? AllocatedPercent, double? MaxPercent);

/// <summary>
/// Reads the space restore points may use, from <c>Win32_ShadowStorage</c> numbers or, as a fallback, from the text of
/// <c>vssadmin list shadowstorage</c>. The text is read by its shape, not its words, so it works in every Windows display
/// language: a block starts at a line naming a <c>\\?\Volume{…}\</c> path, and its next three lines that end in
/// "(N%)" are, in order, the used, allocated and maximum space.
/// </summary>
public static partial class ShadowStorageParser
{
    /// <summary>MaxSpace is reported as the largest 64-bit number when there is no limit.</summary>
    public const ulong UnboundedMarker = ulong.MaxValue;

    [GeneratedRegex(@"\\\\\?\\Volume\{[0-9A-Fa-f-]+\}\\", RegexOptions.CultureInvariant)]
    private static partial Regex VolumeIdPattern();

    [GeneratedRegex(@"\(\s*([A-Za-z]):\s*\)", RegexOptions.CultureInvariant)]
    private static partial Regex DriveInParentheses();

    [GeneratedRegex(@"\(\s*(\d+(?:[.,]\d+)?)\s*%\s*\)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingPercent();

    /// <summary>
    /// Builds the storage figures from the raw <c>Win32_ShadowStorage</c> values. <paramref name="volumeCapacity"/> (the
    /// drive's size) turns the limit into a percentage; a limit of the largest 64-bit number, or one at least the drive's
    /// size, is "no limit".
    /// </summary>
    public static ShadowStorageInfo FromWmi(ulong used, ulong allocated, ulong max, ulong? volumeCapacity)
    {
        bool unbounded = max == UnboundedMarker || max > long.MaxValue || (volumeCapacity is > 0 && max >= volumeCapacity.Value);
        long maxBytes = unbounded ? 0 : (long)max;
        double? percent = unbounded
            ? 100
            : volumeCapacity is > 0 ? Math.Round(100.0 * max / volumeCapacity.Value, 1) : null;
        return new ShadowStorageInfo(ClampToLong(used), ClampToLong(allocated), maxBytes, unbounded, percent);
    }

    /// <summary>
    /// The <c>\\?\Volume{…}\</c> id inside a WMI reference such as
    /// <c>Win32_Volume.DeviceID="\\\\?\\Volume{4c1b…}\\"</c> (backslashes are doubled there); null when there is none.
    /// </summary>
    public static string? VolumeIdFromReference(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        var text = reference.Replace(@"\\", @"\", StringComparison.Ordinal);
        var match = VolumeIdPattern().Match(text);
        return match.Success ? match.Value : null;
    }

    /// <summary>Parses the whole output of <c>vssadmin list shadowstorage</c>. Blocks that cannot be read are skipped.</summary>
    public static IReadOnlyList<VssStorageBlock> ParseVssadmin(string? output)
    {
        var blocks = new List<VssStorageBlock>();
        if (string.IsNullOrWhiteSpace(output))
        {
            return blocks;
        }

        string? drive = null;
        string? volumeId = null;
        bool inBlock = false;
        bool sawStorageVolume = false;
        var values = new List<(long? Bytes, bool Unbounded, double Percent)>();

        void Flush()
        {
            if (inBlock && values.Count >= 3)
            {
                blocks.Add(new VssStorageBlock(drive, volumeId, values[0].Bytes, values[1].Bytes, values[2].Unbounded ? null : values[2].Bytes,
                    values[2].Unbounded, values[0].Percent, values[1].Percent, values[2].Percent));
            }

            drive = null;
            volumeId = null;
            inBlock = false;
            sawStorageVolume = false;
            values.Clear();
        }

        foreach (var raw in output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var volume = VolumeIdPattern().Match(line);
            if (volume.Success)
            {
                // The first volume line of a block names the drive the restore points protect ("For volume"); the
                // second names where they are stored. A volume line after the values starts the next block.
                if (!inBlock || values.Count > 0 || sawStorageVolume)
                {
                    Flush();
                    inBlock = true;
                    volumeId = volume.Value;
                    var letter = DriveInParentheses().Match(line);
                    drive = letter.Success ? letter.Groups[1].Value.ToUpperInvariant() + ":" : null;
                }
                else
                {
                    sawStorageVolume = true;
                }

                continue;
            }

            if (!inBlock || values.Count >= 3)
            {
                continue;
            }

            var percent = TrailingPercent().Match(line);
            if (!percent.Success)
            {
                continue;
            }

            double share = double.Parse(percent.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture);
            var before = line[..percent.Index].TrimEnd();
            var sizeText = AfterLabel(before);
            long? bytes = CleanupCommandRules.ParseSize(sizeText);
            values.Add((bytes, bytes is null, share));
        }

        Flush();
        return blocks;
    }

    /// <summary>Turns a parsed vssadmin block into storage figures.</summary>
    public static ShadowStorageInfo ToInfo(VssStorageBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        return new ShadowStorageInfo(block.UsedBytes ?? 0, block.AllocatedBytes ?? 0, block.MaxBytes ?? 0, block.MaxUnbounded,
            block.MaxUnbounded ? 100 : block.MaxPercent);
    }

    /// <summary>
    /// The size of a limit given as a share of a drive, as vssadmin works it out; null when the drive size is unknown.
    /// </summary>
    public static long? BytesForPercent(long? capacityBytes, int percent) =>
        capacityBytes is > 0 ? (long)Math.Round(capacityBytes.Value * Math.Clamp(percent, 0, 100) / 100.0) : null;

    /// <summary>The text after the line's label: everything after its last colon (any script's colon).</summary>
    private static string AfterLabel(string text)
    {
        int colon = text.LastIndexOfAny([':', '：']);
        return colon >= 0 ? text[(colon + 1)..].Trim() : text.Trim();
    }

    private static long ClampToLong(ulong value) => value > long.MaxValue ? long.MaxValue : (long)value;
}
