using System.Text.Json;
using System.Text.Json.Serialization;

namespace PcChecker.Core.Displays;

/// <summary>Pure helpers for <see cref="DisplayProfile"/>: capture, describe, validate and JSON round trips.</summary>
public static class DisplayProfiles
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Snapshots the current mode of every display and the primary device into a new profile.</summary>
    public static DisplayProfile Capture(string name, IEnumerable<DisplayInfo> displays)
    {
        var profile = new DisplayProfile { Name = (name ?? string.Empty).Trim() };
        foreach (var d in displays ?? [])
        {
            profile.Modes[d.DeviceName] = d.Current;
            if (d.IsPrimary)
            {
                profile.PrimaryDevice = d.DeviceName;
            }
        }

        return profile;
    }

    /// <summary>One-line summary: "2 displays · DISPLAY1 2560 × 1440 @ 240 Hz (main) · DISPLAY2 1920 × 1080 @ 60 Hz".</summary>
    public static string Describe(DisplayProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Modes.Count == 0)
        {
            return "Empty profile";
        }

        var parts = profile.Modes
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => $"{ShortDeviceName(kv.Key)} {kv.Value}{(string.Equals(kv.Key, profile.PrimaryDevice, StringComparison.OrdinalIgnoreCase) ? " (main)" : string.Empty)}");
        string count = profile.Modes.Count == 1 ? "1 display" : $"{profile.Modes.Count} displays";
        return count + " · " + string.Join(" · ", parts);
    }

    /// <summary>Which devices in the profile are currently connected and which are missing.</summary>
    public static (IReadOnlyList<string> Present, IReadOnlyList<string> Missing) Match(DisplayProfile profile, IEnumerable<DisplayInfo> displays)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var connected = new HashSet<string>((displays ?? []).Select(d => d.DeviceName), StringComparer.OrdinalIgnoreCase);
        var present = profile.Modes.Keys.Where(connected.Contains).ToList();
        var missing = profile.Modes.Keys.Where(k => !connected.Contains(k)).ToList();
        return (present, missing);
    }

    /// <summary>True when every display already runs the profile's mode and the primary matches.</summary>
    public static bool IsActive(DisplayProfile profile, IEnumerable<DisplayInfo> displays)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var list = (displays ?? []).ToList();
        if (profile.Modes.Count == 0)
        {
            return false;
        }

        foreach (var (device, mode) in profile.Modes)
        {
            var d = list.FirstOrDefault(x => string.Equals(x.DeviceName, device, StringComparison.OrdinalIgnoreCase));
            if (d is null || d.Current.Width != mode.Width || d.Current.Height != mode.Height || d.Current.RefreshHz != mode.RefreshHz)
            {
                return false;
            }
        }

        if (profile.PrimaryDevice is not null)
        {
            var primary = list.FirstOrDefault(x => x.IsPrimary);
            if (primary is null || !string.Equals(primary.DeviceName, profile.PrimaryDevice, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Validates a profile name against the existing list; returns an error message or null.</summary>
    public static string? ValidateName(string? name, IEnumerable<DisplayProfile> existing, bool allowOverwrite = false)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Give the profile a name first.";
        }

        if (name.Trim().Length > 60)
        {
            return "Profile names are limited to 60 characters.";
        }

        if (!allowOverwrite && (existing ?? []).Any(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            return $"A profile called \"{name.Trim()}\" already exists.";
        }

        return null;
    }

    public static string ToJson(DisplayProfile profile) => JsonSerializer.Serialize(profile, JsonOptions);

    public static DisplayProfile? FromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var profile = JsonSerializer.Deserialize<DisplayProfile>(json, JsonOptions);
            if (profile is null)
            {
                return null;
            }

            // Dictionaries deserialize with the default comparer; rebuild so device names stay case-insensitive.
            profile.Modes = new Dictionary<string, DisplayMode>(profile.Modes ?? [], StringComparer.OrdinalIgnoreCase);
            return profile;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary><c>\\.\DISPLAY1</c> → "DISPLAY1".</summary>
    public static string ShortDeviceName(string? deviceName)
    {
        if (string.IsNullOrEmpty(deviceName))
        {
            return string.Empty;
        }

        int i = deviceName.LastIndexOf('\\');
        return i >= 0 ? deviceName[(i + 1)..] : deviceName;
    }
}
