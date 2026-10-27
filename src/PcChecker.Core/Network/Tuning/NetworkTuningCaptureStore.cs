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

    /// <summary>"RoamAggressiveness=3, *EEE=&lt;not set&gt;" for the journal's Before field.</summary>
    public static string Describe(IReadOnlyDictionary<string, string?> values) =>
        string.Join(", ", values.OrderBy(v => v.Key, StringComparer.OrdinalIgnoreCase).Select(v => $"{v.Key}={v.Value ?? "<not set>"}"));
}
