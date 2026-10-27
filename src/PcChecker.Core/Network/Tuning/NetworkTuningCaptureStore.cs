using System.Text.Json;

namespace PcChecker.Core.Network.Tuning;

/// <summary>
/// The values the tuning service captured before each change (entry id → value name → previous value, null for
/// "was absent"), saved as <c>%LOCALAPPDATA%\PcChecker\network-tuning.json</c> next to the settings file so Revert
/// restores the real previous values even after the app restarted. Pure file I/O; usable on any OS.
/// </summary>
public static class NetworkTuningCaptureStore
{
    public const string FileName = "network-tuning.json";

    /// <summary>Entry id for an adapter property capture.</summary>
    public static string AdapterEntryId(string adapterName, string keyword) => $"adapter:{adapterName}|{keyword}";

    /// <summary>Entry id for a saved Wi-Fi network (priority, auto-connect and metered before the first change).</summary>
    public static string ProfileEntryId(string profileName) => $"profile:{profileName}";

    /// <summary>The values of a saved network that PC Checker can change, as a capture entry.</summary>
    public static IReadOnlyDictionary<string, string?> ProfileValues(WifiProfileInfo profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["interface"] = profile.Interface,
            ["priority"] = profile.Priority.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["autoConnect"] = profile.AutoConnect ? "true" : "false",
            ["metered"] = profile.Metered ? "true" : "false",
        };
    }

    /// <summary>Rebuilds the captured state of a saved network; null when nothing (or nothing usable) was captured.</summary>
    public static WifiProfileInfo? ProfileFrom(string profileName, IReadOnlyDictionary<string, string?>? values)
    {
        if (values is null
            || !int.TryParse(values.GetValueOrDefault("priority"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int priority)
            || !bool.TryParse(values.GetValueOrDefault("autoConnect"), out bool autoConnect)
            || !bool.TryParse(values.GetValueOrDefault("metered"), out bool metered))
        {
            return null;
        }

        return new WifiProfileInfo(profileName, values.GetValueOrDefault("interface") ?? string.Empty, autoConnect, metered, string.Empty, priority);
    }

    /// <summary>Entry that remembers what <c>ecncapability=default</c> resolved to on this PC.</summary>
    public const string EcnDefaultEntryId = "info:ecn-default";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string DefaultPath(string settingsDirectory) => Path.Combine(settingsDirectory, FileName);

    public static string Serialize(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> captures)
    {
        ArgumentNullException.ThrowIfNull(captures);
        var plain = captures.ToDictionary(c => c.Key, c => c.Value.ToDictionary(v => v.Key, v => v.Value, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        return JsonSerializer.Serialize(plain, JsonOptions);
    }

    /// <summary>Parses a captures file; empty, malformed or unexpected JSON gives an empty map instead of an exception.</summary>
    public static Dictionary<string, IReadOnlyDictionary<string, string?>> Deserialize(string? json)
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }

        try
        {
            var plain = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string?>?>>(json, JsonOptions);
            foreach (var (id, values) in plain ?? [])
            {
                if (!string.IsNullOrWhiteSpace(id) && values is not null)
                {
                    result[id] = new Dictionary<string, string?>(values, StringComparer.OrdinalIgnoreCase);
                }
            }
        }
        catch (JsonException)
        {
            result.Clear();
        }

        return result;
    }

    public static void Write(string path, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> captures)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = path + ".tmp";
        File.WriteAllText(temp, Serialize(captures));
        File.Move(temp, path, overwrite: true);
    }

    public static Dictionary<string, IReadOnlyDictionary<string, string?>> TryRead(string path)
    {
        try
        {
            return File.Exists(path) ? Deserialize(File.ReadAllText(path)) : new(StringComparer.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
        catch (UnauthorizedAccessException)
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Adds the values in <paramref name="current"/> whose names <paramref name="existing"/> does not have yet (an
    /// adapter, server or key that appeared after the first capture), keeping every value already captured: those
    /// hold what the PC looked like before PC Checker touched it. Returns <paramref name="existing"/> itself when
    /// nothing is new.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> Merge(IReadOnlyDictionary<string, string?> existing, IReadOnlyDictionary<string, string?> current)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(current);
        var added = current.Where(c => !existing.ContainsKey(c.Key)).ToList();
        if (added.Count == 0)
        {
            return existing;
        }

        var merged = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in existing)
        {
            merged[key] = value;
        }

        foreach (var (key, value) in added)
        {
            merged[key] = value;
        }

        return merged;
    }

    /// <summary>
    /// True when every captured value still reads the same in <paramref name="current"/>, so a failed change wrote
    /// nothing. A value that can no longer be read counts as changed, so the capture is kept when in doubt.
    /// </summary>
    public static bool Unchanged(IReadOnlyDictionary<string, string?> captured, IReadOnlyDictionary<string, string?> current)
    {
        ArgumentNullException.ThrowIfNull(captured);
        ArgumentNullException.ThrowIfNull(current);
        return captured.All(c => current.TryGetValue(c.Key, out var now) && string.Equals(now, c.Value, StringComparison.Ordinal));
    }

    /// <summary>"RoamAggressiveness=3, *EEE=&lt;not set&gt;" for the journal's Before field.</summary>
    public static string Describe(IReadOnlyDictionary<string, string?> values) =>
        string.Join(", ", values.OrderBy(v => v.Key, StringComparer.OrdinalIgnoreCase).Select(v => $"{v.Key}={v.Value ?? "<not set>"}"));
}
