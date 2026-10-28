using System.Globalization;

namespace PcChecker.Core.GameReady;

/// <summary>
/// Reads and writes the "Name=Value;" text Windows keeps under HKCU\Software\Microsoft\DirectX\UserGpuPreferences: the
/// per-program graphics preference ("GpuPreference=2;") and the global DirectXUserGlobalSettings
/// ("SwapEffectUpgradeEnable=1;VRROptimizeEnable=0;"). Unknown pairs are kept as they are, in their order. Pure.
/// </summary>
public static class DirectXSettingsText
{
    /// <summary>Registry key (under HKEY_CURRENT_USER) of the graphics preferences.</summary>
    public const string Key = @"Software\Microsoft\DirectX\UserGpuPreferences";

    /// <summary>The value that holds Windows' global graphics settings.</summary>
    public const string GlobalValueName = "DirectXUserGlobalSettings";

    /// <summary>"Optimizations for windowed games" in Settings → Display → Graphics.</summary>
    public const string WindowedOptimizations = "SwapEffectUpgradeEnable";

    /// <summary>"Variable refresh rate" in Settings → Display → Graphics (VRR for games that do not support it).</summary>
    public const string VariableRefreshRate = "VRROptimizeEnable";

    /// <summary>The per-program graphics processor choice.</summary>
    public const string GpuPreferenceName = "GpuPreference";

    /// <summary>Let Windows decide.</summary>
    public const int GpuDefault = 0;

    /// <summary>Power saving (usually the integrated graphics).</summary>
    public const int GpuPowerSaving = 1;

    /// <summary>High performance (usually the graphics card).</summary>
    public const int GpuHighPerformance = 2;

    /// <summary>The pairs in order. Blank parts and parts without a name are skipped; names keep their spelling.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Parse(string? text)
    {
        var pairs = new List<KeyValuePair<string, string>>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return pairs;
        }

        foreach (var part in text.Split(';'))
        {
            var trimmed = part.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            int equals = trimmed.IndexOf('=');
            string name = (equals < 0 ? trimmed : trimmed[..equals]).Trim();
            string value = equals < 0 ? string.Empty : trimmed[(equals + 1)..].Trim();
            if (name.Length > 0)
            {
                pairs.Add(new(name, value));
            }
        }

        return pairs;
    }

    /// <summary>The value of <paramref name="name"/> (compared without case), or null when it is not there.</summary>
    public static string? Get(string? text, string name)
    {
        foreach (var pair in Parse(text))
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        return null;
    }

    /// <summary>The value as a whole number, or null when it is missing or not a number.</summary>
    public static int? GetInt(string? text, string name) =>
        int.TryParse(Get(text, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : null;

    /// <summary>
    /// The text with <paramref name="name"/> set to <paramref name="value"/>: replaced where it is (keeping the other
    /// pairs and their order) or added at the end. Always ends with ";", as Windows writes it.
    /// </summary>
    public static string Set(string? text, string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var pairs = Parse(text).ToList();
        int index = pairs.FindIndex(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            pairs[index] = new(pairs[index].Key, value);
        }
        else
        {
            pairs.Add(new(name, value));
        }

        return Build(pairs);
    }

    /// <summary>The text without <paramref name="name"/>; an empty string when nothing is left.</summary>
    public static string Remove(string? text, string name) =>
        Build(Parse(text).Where(p => !string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)));

    /// <summary>"A=1;B=2;" from pairs.</summary>
    public static string Build(IEnumerable<KeyValuePair<string, string>> pairs) =>
        string.Concat(pairs.Select(p => p.Key + "=" + p.Value + ";"));

    /// <summary>The graphics preference of a program (0, 1 or 2), or null when none is set or it is not a number.</summary>
    public static int? GpuPreference(string? text) => GetInt(text, GpuPreferenceName);

    /// <summary>The program's text with its graphics preference set to <paramref name="preference"/>, other pairs kept.</summary>
    public static string WithGpuPreference(string? text, int preference) =>
        Set(text, GpuPreferenceName, preference.ToString(CultureInfo.InvariantCulture));

    /// <summary>True when a "Name=1" switch is on, false when it is "0", null when it is missing or another value.</summary>
    public static bool? Switch(string? text, string name) => GetInt(text, name) switch
    {
        1 => true,
        0 => false,
        _ => null,
    };
}
