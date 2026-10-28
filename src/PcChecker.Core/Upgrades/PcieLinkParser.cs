using System.Globalization;

namespace PcChecker.Core.Upgrades;

/// <summary>
/// Parses "KeyName=Value" lines printed for the DEVPKEY_PciDevice_* link properties. The values are plain integers
/// (link speed 1–6 is the PCIe generation, width is the lane count), so the output is the same in every Windows language.
/// </summary>
public static class PcieLinkParser
{
    public const string CurrentSpeedKey = "DEVPKEY_PciDevice_CurrentLinkSpeed";
    public const string MaxSpeedKey = "DEVPKEY_PciDevice_MaxLinkSpeed";
    public const string CurrentWidthKey = "DEVPKEY_PciDevice_CurrentLinkWidth";
    public const string MaxWidthKey = "DEVPKEY_PciDevice_MaxLinkWidth";

    /// <summary>The link, or null when none of the four values was present.</summary>
    public static PcieLinkInfo? Parse(string? output)
    {
        var values = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in (output ?? string.Empty).Split('\n'))
        {
            var line = raw.Trim();
            int eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            if (int.TryParse(line[(eq + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0)
            {
                values[line[..eq].Trim()] = value;
            }
        }

        int? Get(string key, int max) => values.TryGetValue(key, out var v) && v <= max ? v : null;
        var link = new PcieLinkInfo(Get(CurrentSpeedKey, 6), Get(MaxSpeedKey, 6), Get(CurrentWidthKey, 32), Get(MaxWidthKey, 32));
        return link.CurrentGen is null && link.MaxGen is null && link.CurrentWidth is null && link.MaxWidth is null ? null : link;
    }
}
