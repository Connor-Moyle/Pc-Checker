using System.Collections.Concurrent;
using System.Text.Json;

namespace PcChecker.Core.Storage;

/// <summary>
/// Remembers the last SMART reading per drive so a sleeping drive can show "Asleep, last reading at …" instead of being
/// woken. Kept in memory and, when a file path is given, in a small JSON file so it survives a restart. Every file access
/// is guarded: a missing or damaged file just means no earlier readings.
/// </summary>
public sealed class DriveReadingCache
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };
    private readonly ConcurrentDictionary<string, SmartReading> _readings = new(StringComparer.Ordinal);
    private readonly string? _path;
    private bool _loaded;

    public DriveReadingCache(string? path = null)
    {
        _path = path;
    }

    /// <summary>The default file: %LOCALAPPDATA%\PcChecker\drive-readings.json.</summary>
    public static string? DefaultPath()
    {
        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return string.IsNullOrEmpty(local) ? null : Path.Combine(local, "PcChecker", "drive-readings.json");
        }
        catch
        {
            return null;
        }
    }

    public SmartReading? Get(string key)
    {
        EnsureLoaded();
        return _readings.TryGetValue(key, out var reading) ? reading : null;
    }

    public void Put(string key, SmartReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);
        EnsureLoaded();
        _readings[key] = reading;
    }

    /// <summary>Writes the readings to the file (no-op without a path). Failures are ignored: the cache is a convenience.</summary>
    public void Save()
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_readings.ToDictionary(p => p.Key, p => p.Value), Options));
            File.Move(tmp, _path, overwrite: true);
        }
        catch
        {
            // The cache only saves a wake-up; losing it is harmless.
        }
    }

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        if (_path is null)
        {
            return;
        }

        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var stored = JsonSerializer.Deserialize<Dictionary<string, SmartReading>>(File.ReadAllText(_path), Options);
            foreach (var (key, value) in stored ?? [])
            {
                if (value is not null)
                {
                    _readings.TryAdd(key, value);
                }
            }
        }
        catch
        {
            // A damaged file means no earlier readings.
        }
    }
}
