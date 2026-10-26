using System.Text.Json;
using System.Text.RegularExpressions;

namespace PcChecker.Core.WindowsFeatures;

/// <summary>
/// Pure parsing helpers for the output of the DISM and Appx PowerShell cmdlets: feature/capability states that
/// <c>ConvertTo-Json</c> emits as either numbers or names, and the <c>RestartNeeded</c> flag of a change.
/// </summary>
public static partial class FeatureStateParser
{
    /// <summary>Parses an optional-feature state (Get-WindowsOptionalFeature). Accepts names and the numeric enum.</summary>
    public static FeatureState ParseFeatureState(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return FeatureState.Unknown;
        }

        value = value.Trim();
        if (int.TryParse(value, out int number))
        {
            return number switch
            {
                1 => FeatureState.Disabled,
                2 => FeatureState.Enabled,
                3 => FeatureState.Disabled, // DisabledWithPayloadRemoved
                4 => FeatureState.Enabled,  // EnablePending
                5 => FeatureState.Disabled, // DisablePending
                _ => FeatureState.Unknown,
            };
        }

        return value.ToLowerInvariant() switch
        {
            "enabled" or "enablepending" or "installed" or "installpending" or "partiallyinstalled" or "superseded" => FeatureState.Enabled,
            "disabled" or "disabledwithpayloadremoved" or "disablepending" or "staged" or "uninstallpending" => FeatureState.Disabled,
            "notpresent" or "removed" or "absent" => FeatureState.NotPresent,
            _ => FeatureState.Unknown,
        };
    }

    /// <summary>Parses a capability state (Get-WindowsCapability). Accepts names and the numeric DISM package state.</summary>
    public static FeatureState ParseCapabilityState(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return FeatureState.Unknown;
        }

        value = value.Trim();
        if (int.TryParse(value, out int number))
        {
            return number switch
            {
                0 => FeatureState.NotPresent,     // NotPresent
                1 => FeatureState.Disabled,       // UninstallPending
                2 => FeatureState.Disabled,       // Staged
                3 => FeatureState.NotPresent,     // Removed
                4 => FeatureState.Enabled,        // Installed
                5 => FeatureState.Enabled,        // InstallPending
                6 => FeatureState.Enabled,        // Superseded
                7 => FeatureState.Enabled,        // PartiallyInstalled
                _ => FeatureState.Unknown,
            };
        }

        return ParseFeatureState(value);
    }

    /// <summary>Reads a state from a JSON value that may be a number, a string or null.</summary>
    public static string? StateText(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number => element.TryGetInt64(out long l) ? l.ToString(System.Globalization.CultureInfo.InvariantCulture) : element.GetRawText(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.True => "Enabled",
        JsonValueKind.False => "Disabled",
        _ => null,
    };

    /// <summary>
    /// Detects "RestartNeeded" in cmdlet output: JSON (<c>"RestartNeeded":true</c>), Format-List text
    /// (<c>RestartNeeded : True</c>) or DISM's "restart is required" phrasing.
    /// </summary>
    public static bool ParseRestartNeeded(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        var match = RestartNeededRegex().Match(output);
        if (match.Success)
        {
            var flag = match.Groups["value"].Value;
            return flag.Equals("true", StringComparison.OrdinalIgnoreCase) || flag == "1";
        }

        return output.Contains("restart is required", StringComparison.OrdinalIgnoreCase)
            || output.Contains("reboot is required", StringComparison.OrdinalIgnoreCase)
            || output.Contains("restart the computer", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Reads a boolean that ConvertTo-Json may have written as true/false, "True"/"False", 1/0 or null.</summary>
    public static bool ParseBool(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => element.TryGetInt64(out long l) && l != 0,
        JsonValueKind.String => element.GetString() is { } s && (s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1"),
        _ => false,
    };

    [GeneratedRegex("RestartNeeded\"?\\s*[:=]\\s*\"?(?<value>true|false|1|0)", RegexOptions.IgnoreCase)]
    private static partial Regex RestartNeededRegex();
}
