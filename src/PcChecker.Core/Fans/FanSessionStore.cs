using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PcChecker.Core.Fans;

/// <summary>
/// Marker written to disk while any fan is under software control. If the app crashes, the next start finds the file,
/// returns those fans to automatic control and tells the user. Deleted again as soon as every fan is automatic. When
/// the app exits normally with "revert on exit" off, the marker stays but is flagged <see cref="CleanExit"/>, so the
/// next start knows nothing crashed.
/// </summary>
public sealed class FanSession
{
    public DateTimeOffset StartedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public int ProcessId { get; set; }

    /// <summary>Start time of the process that wrote the file. Windows reuses process ids, so the id alone cannot tell a live PC Checker from an unrelated process that inherited the number after a crash.</summary>
    public DateTimeOffset? ProcessStartedUtc { get; set; }

    /// <summary>Image name of the writing process (without extension), for the same reason.</summary>
    public string? ProcessName { get; set; }

    /// <summary>True when the app exited normally while fans were still under software control (revert on exit was off).</summary>
    public bool CleanExit { get; set; }

    public string? ProfileName { get; set; }
    public List<string> FanIds { get; set; } = [];
}

/// <summary>What the OS reports about a running process: null start time or name when it could not be read.</summary>
public sealed record ProcessIdentity(DateTimeOffset? StartedUtc, string? Name);

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
        ArgumentNullException.ThrowIfNull(isProcessRunning);
        return IsStale(session, pid => isProcessRunning(pid) ? new ProcessIdentity(null, null) : null);
    }

    /// <summary>
    /// True when the file is a crash leftover: its process is gone, or the process that now holds that id is a different
    /// one (another start time or image name), which happens because Windows hands freed ids to new processes quickly.
    /// <paramref name="lookup"/> returns null for an id that is not running.
    /// </summary>
    public static bool IsStale(FanSession session, Func<int, ProcessIdentity?> lookup)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(lookup);
        if (session.ProcessId <= 0)
        {
            return true;
        }

        if (session.ProcessId == Environment.ProcessId)
        {
            return false;
        }

        var live = lookup(session.ProcessId);
        if (live is null)
        {
            return true;
        }

        if (session.ProcessStartedUtc is { } started && live.StartedUtc is { } liveStarted
            && Math.Abs((started - liveStarted).TotalSeconds) > 2)
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(session.ProcessName) && !string.IsNullOrWhiteSpace(live.Name)
               && !session.ProcessName.Equals(live.Name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A fresh session record for this process, with its start time and name so <see cref="IsStale(FanSession, Func{int, ProcessIdentity?})"/> can match it later.</summary>
    public static FanSession NewForCurrentProcess()
    {
        var session = new FanSession { ProcessId = Environment.ProcessId };
        var identity = LookupProcess(Environment.ProcessId);
        session.ProcessStartedUtc = identity?.StartedUtc;
        session.ProcessName = identity?.Name;
        return session;
    }

    /// <summary>What the OS knows about the process with <paramref name="pid"/>; null when it is not running.</summary>
    public static ProcessIdentity? LookupProcess(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited)
            {
                return null;
            }

            DateTimeOffset? started = null;
            try
            {
                started = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
            }
            catch (Exception)
            {
                // Access denied or the process just exited: compare by id and name only.
            }

            string? name = null;
            try
            {
                name = process.ProcessName;
            }
            catch (Exception)
            {
            }

            return new ProcessIdentity(started, name);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Flags the current process's session as a normal exit (fans intentionally left under software control), so the
    /// next start neither reports a crash nor loses track of which fans the app was driving.
    /// </summary>
    public static void MarkCleanExit(string path)
    {
        var session = TryRead(path);
        if (session is null || session.ProcessId != Environment.ProcessId)
        {
            return;
        }

        session.CleanExit = true;
        session.UpdatedUtc = DateTimeOffset.UtcNow;
        try
        {
            Write(path, session);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
