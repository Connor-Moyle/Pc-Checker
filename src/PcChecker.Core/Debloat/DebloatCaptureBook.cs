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
                    string json = File.ReadAllText(path);
                    if (TryDeserialize(json, out var loaded))
                    {
                        foreach (var (id, values) in loaded)
                        {
                            _captures[id] = values;
                        }
                    }
                    else
                    {
                        // Keep the unreadable file so the next save does not silently destroy the original values.
                        BackupPath = $"{path}.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}";
                        File.Copy(path, BackupPath, overwrite: true);
                        LoadWarning = $"The saved undo values could not be read; the file was kept as {Path.GetFileName(BackupPath)}.";
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

    /// <summary>Set when the capture file existed but could not be parsed; explains where the copy was kept.</summary>
    public string? LoadWarning { get; private set; }

    /// <summary>Where an unreadable capture file was copied before it could be overwritten, or null.</summary>
    public string? BackupPath { get; private set; }

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
            try
            {
                Save();
            }
            catch
            {
                _captures.Remove(tweakId);
                throw;
            }

            return true;
        }
    }

    /// <summary>
    /// Forgets a capture after a successful revert and saves. Never throws: returns null on success, or an explanation
    /// when the file could not be updated (the capture is still forgotten in memory; a stale entry in the file only
    /// means a later revert restores the same original values again).
    /// </summary>
    public string? Remove(string tweakId)
    {
        lock (_gate)
        {
            if (!_captures.Remove(tweakId))
            {
                return null;
            }

            try
            {
                Save();
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return $"The saved undo values could not be updated ({ex.Message}).";
            }
        }
    }

    public static string Serialize(IReadOnlyDictionary<string, Dictionary<string, string?>> captures) => JsonSerializer.Serialize(captures, JsonOptions);

    /// <summary>Parses a capture file; empty or malformed JSON gives an empty map instead of an exception.</summary>
    public static Dictionary<string, Dictionary<string, string?>> Deserialize(string? json)
    {
        TryDeserialize(json, out var result);
        return result;
    }

    /// <summary>Parses a capture file. Returns false (with an empty map) when the JSON is malformed; empty text is valid.</summary>
    public static bool TryDeserialize(string? json, out Dictionary<string, Dictionary<string, string?>> result)
    {
        result = new Dictionary<string, Dictionary<string, string?>>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json))
        {
            return true;
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
            return false;
        }

        return true;
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

        // Antivirus and indexers briefly lock freshly written files; a short retry keeps such a lock from failing the
        // tweak (a capture that cannot be saved means the tweak is not applied).
        var temp = _path + ".tmp";
        string json = Serialize(_captures);
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.WriteAllText(temp, json);
                File.Move(temp, _path, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < SaveAttempts)
            {
                Thread.Sleep(SaveRetryDelay * attempt);
            }
        }
    }

    private const int SaveAttempts = 3;
    private static readonly TimeSpan SaveRetryDelay = TimeSpan.FromMilliseconds(150);
}
