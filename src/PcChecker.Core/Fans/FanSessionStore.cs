using System.Text.Json;
using System.Text.Json.Serialization;

namespace PcChecker.Core.Fans;

/// <summary>
/// Marker written to disk while any fan is under software control. If the app crashes, the next start finds the file,
/// returns those fans to automatic control and tells the user. Deleted again as soon as every fan is automatic.
/// </summary>
public sealed class FanSession
{
    public DateTimeOffset StartedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public int ProcessId { get; set; }
    public string? ProfileName { get; set; }
    public List<string> FanIds { get; set; } = [];
}

/// <summary>Reads and writes <see cref="FanSession"/> files. Pure file I/O; usable on any OS.</summary>
public static class FanSessionStore
{
    public const string FileName = "fan-session.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Default location: next to the settings file (%LOCALAPPDATA%\PcChecker\fan-session.json).</summary>
    public static string DefaultPath(string settingsDirectory) => Path.Combine(settingsDirectory, FileName);

    public static string Serialize(FanSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return JsonSerializer.Serialize(session, JsonOptions);
    }

    /// <summary>Parses a session file; returns null for empty, malformed or unexpected JSON instead of throwing.</summary>
    public static FanSession? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var session = JsonSerializer.Deserialize<FanSession>(json, JsonOptions);
            if (session is null)
            {
                return null;
            }

            session.FanIds ??= [];
            return session;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static void Write(string path, FanSession session)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = path + ".tmp";
        File.WriteAllText(temp, Serialize(session));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Returns the session recorded at <paramref name="path"/>, or null when there is none or it is unreadable.</summary>
    public static FanSession? TryRead(string path)
    {
        try
        {
            return File.Exists(path) ? Deserialize(File.ReadAllText(path)) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Delete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A stale marker only costs one extra "revert to auto" at the next start.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>True when the file belongs to a process that is no longer running (so it is a crash leftover, not a live session).</summary>
    public static bool IsStale(FanSession session, Func<int, bool> isProcessRunning)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(isProcessRunning);
        if (session.ProcessId <= 0)
        {
            return true;
        }

        if (session.ProcessId == Environment.ProcessId)
        {
            return false;
        }

        return !isProcessRunning(session.ProcessId);
    }
}
