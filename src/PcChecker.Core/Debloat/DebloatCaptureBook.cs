using System.Text.Json;

namespace PcChecker.Core.Debloat;

/// <summary>
/// The "before" values captured when a tweak is applied: tweak id → operation key → previous value (null = it was
/// absent). Saved as JSON next to the settings file (<c>%LOCALAPPDATA%\PcChecker\debloat-capture.json</c>) so a revert
/// after restarting the app restores exactly what was there. A tweak's capture is kept until it is reverted, so
/// applying the same tweak twice never overwrites the original values. Pure file I/O; works on any OS.
/// </summary>
public sealed class DebloatCaptureBook
{
    public const string FileName = "debloat-capture.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly object _gate = new();
    private readonly Dictionary<string, Dictionary<string, string?>> _captures = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _path;

    /// <summary>Creates a book backed by <paramref name="path"/> (loaded now), or an in-memory one when null.</summary>
    public DebloatCaptureBook(string? path = null)
    {
        _path = path;
        if (path is not null)
        {
            try
            {
                if (File.Exists(path))
                {
                    foreach (var (id, values) in Deserialize(File.ReadAllText(path)))
                    {
                        _captures[id] = values;
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    public static string DefaultPath(string settingsDirectory) => Path.Combine(settingsDirectory, FileName);

    /// <summary>The ids with a capture on record.</summary>
    public IReadOnlyCollection<string> TweakIds
    {
        get
        {
            lock (_gate)
            {
                return _captures.Keys.ToList();
            }
        }
    }

    public bool Has(string tweakId)
    {
        lock (_gate)
        {
            return _captures.ContainsKey(tweakId);
        }
    }

    /// <summary>Returns the captured values for a tweak, or null when nothing was captured.</summary>
    public IReadOnlyDictionary<string, string?>? Get(string tweakId)
    {
        lock (_gate)
        {
            return _captures.TryGetValue(tweakId, out var values) ? new Dictionary<string, string?>(values, StringComparer.OrdinalIgnoreCase) : null;
        }
    }

    /// <summary>Records a capture unless one already exists (the first capture is the true original). Saves immediately.</summary>
    public bool TryAdd(string tweakId, IReadOnlyDictionary<string, string?> values)
    {
        lock (_gate)
        {
            if (_captures.ContainsKey(tweakId))
            {
                return false;
            }

            _captures[tweakId] = new Dictionary<string, string?>(values, StringComparer.OrdinalIgnoreCase);
            Save();
            return true;
        }
    }

    /// <summary>Forgets a capture after a successful revert. Saves immediately.</summary>
    public void Remove(string tweakId)
    {
        lock (_gate)
        {
            if (_captures.Remove(tweakId))
            {
                Save();
            }
        }
    }

    public static string Serialize(IReadOnlyDictionary<string, Dictionary<string, string?>> captures) => JsonSerializer.Serialize(captures, JsonOptions);

    /// <summary>Parses a capture file; empty or malformed JSON gives an empty map instead of an exception.</summary>
    public static Dictionary<string, Dictionary<string, string?>> Deserialize(string? json)
    {
        var result = new Dictionary<string, Dictionary<string, string?>>(StringComparer.OrdinalIgnoreCase);
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

    private void Save()
    {
        if (_path is null)
        {
            return;
        }

        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = _path + ".tmp";
        File.WriteAllText(temp, Serialize(_captures));
        File.Move(temp, _path, overwrite: true);
    }
}
