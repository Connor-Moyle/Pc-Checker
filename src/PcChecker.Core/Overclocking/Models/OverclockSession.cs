using System.Text.Json;
using System.Text.Json.Serialization;

namespace PcChecker.Core.Overclocking;

/// <summary>
/// What was applied in the current overclock session. Written to <c>oc-session.json</c> before a profile is applied
/// so the next start can tell whether the previous session ended cleanly (watchdog).
/// </summary>
public sealed class OverclockSession
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public OverclockProfile Profile { get; set; } = OverclockProfile.Stock();
    public DateTimeOffset AppliedUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>False while a profile is live; set to true on a clean exit.</summary>
    public bool Clean { get; set; }

    /// <summary>
    /// When Windows last started, as seen when the profile was applied. A clean exit leaves the tuning live until
    /// the next reboot, so a later start compares this with its own boot time to know whether it still is.
    /// Null in files written before this was recorded.
    /// </summary>
    public DateTimeOffset? BootUtc { get; set; }

    public GpuControlMethod GpuMethod { get; set; } = GpuControlMethod.None;
    public CpuControlMethod CpuMethod { get; set; } = CpuControlMethod.None;

    /// <summary>True when CPU tunables were changed and must be reset too.</summary>
    public bool CpuTouched { get; set; }

    /// <summary>Which XTU tunable ids were changed (so a reset can target them).</summary>
    public List<int> XtuTunableIds { get; set; } = [];
}

/// <summary>Reads and writes the session file. Serialization is pure; the file helpers never throw.</summary>
public static class OverclockSessionStore
{
    public const string FileName = "oc-session.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string Serialize(OverclockSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return JsonSerializer.Serialize(session, Options);
    }

    /// <summary>Parses a session file; returns null for empty, malformed or unknown content.</summary>
    public static OverclockSession? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var session = JsonSerializer.Deserialize<OverclockSession>(json, Options);
            if (session is null)
            {
                return null;
            }

            session.Profile ??= OverclockProfile.Stock();
            session.XtuTunableIds ??= [];
            return session;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string PathFor(string directory) => Path.Combine(directory, FileName);

    /// <summary>How far two boot-time readings of the same Windows session may drift apart (clock corrections).</summary>
    public static readonly TimeSpan BootTolerance = TimeSpan.FromMinutes(2);

    /// <summary>When Windows started, from the tick count (which only resets on a reboot).</summary>
    public static DateTimeOffset CurrentBootUtc() => DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

    /// <summary>
    /// True when the session was applied during the current Windows session, i.e. the PC has not rebooted since, so
    /// the driver and XTU settings it recorded are still live. A session without a boot time cannot tell and counts as
    /// an earlier boot.
    /// </summary>
    public static bool IsFromCurrentBoot(OverclockSession session, DateTimeOffset currentBootUtc)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.BootUtc is not { } boot)
        {
            return false;
        }

        // A reboot after the profile was applied puts the current boot after AppliedUtc.
        return (currentBootUtc - boot).Duration() <= BootTolerance && currentBootUtc <= session.AppliedUtc + BootTolerance;
    }

    public static OverclockSession? Load(string path)
    {
        try
        {
            return File.Exists(path) ? Deserialize(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static bool Save(string path, OverclockSession session)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var temp = path + ".tmp";
            File.WriteAllText(temp, Serialize(session));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing to do; the next clean start will overwrite it.
        }
    }
}
