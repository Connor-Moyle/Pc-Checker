using System.Text.Json;

namespace PcChecker.Core.Latency;

/// <summary>
/// Saves the values <see cref="LatencyActionsProvider"/> captured before each change (action id → value name → previous
/// value, null for "was absent") next to the settings file, so Undo after an app restart restores the real previous
/// values instead of guessing defaults. Pure file I/O; usable on any OS.
/// </summary>
public static class LatencyCaptureStore
{
    public const string FileName = "latency-captures.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Default location: next to the settings file (%LOCALAPPDATA%\PcChecker\latency-captures.json).</summary>
    public static string DefaultPath(string settingsDirectory) => Path.Combine(settingsDirectory, FileName);

    public static string Serialize(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> captures)
    {
        ArgumentNullException.ThrowIfNull(captures);
        var plain = captures.ToDictionary(c => c.Key, c => c.Value.ToDictionary(v => v.Key, v => v.Value, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        return JsonSerializer.Serialize(plain, JsonOptions);
    }

    /// <summary>Parses a captures file; returns an empty map for empty, malformed or unexpected JSON instead of throwing.</summary>
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
            if (plain is null)
            {
                return result;
            }

            foreach (var (id, values) in plain)
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

    /// <summary>Returns the captures recorded at <paramref name="path"/>, or an empty map when there are none or the file is unreadable.</summary>
    public static Dictionary<string, IReadOnlyDictionary<string, string?>> TryRead(string path)
    {
        try
        {
            return File.Exists(path) ? Deserialize(File.ReadAllText(path)) : new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.OrdinalIgnoreCase);
        }
        catch (UnauthorizedAccessException)
        {
            return new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
