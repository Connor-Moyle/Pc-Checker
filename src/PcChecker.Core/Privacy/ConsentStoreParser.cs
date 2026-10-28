using System.Text;

namespace PcChecker.Core.Privacy;

/// <summary>
/// Pure helpers for Windows' capability consent store
/// (<c>Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore</c>): FILETIME use times,
/// '#'-encoded desktop app paths, Allow/Deny values and how records from the two hives are merged.
/// </summary>
public static class ConsentStoreParser
{
    public const string StorePath = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";
    public const string NonPackaged = "NonPackaged";
    public const string ValueName = "Value";
    public const string StartName = "LastUsedTimeStart";
    public const string StopName = "LastUsedTimeStop";

    /// <summary>The consent store key of a capability.</summary>
    public static string KeyName(CapabilityKind capability) => capability switch
    {
        CapabilityKind.Camera => "webcam",
        CapabilityKind.Microphone => "microphone",
        CapabilityKind.Location => "location",
        _ => throw new ArgumentOutOfRangeException(nameof(capability)),
    };

    /// <summary>The Windows Settings privacy page of a capability.</summary>
    public static string SettingsUri(CapabilityKind capability) => capability switch
    {
        CapabilityKind.Camera => "ms-settings:privacy-webcam",
        CapabilityKind.Microphone => "ms-settings:privacy-microphone",
        CapabilityKind.Location => "ms-settings:privacy-location",
        _ => throw new ArgumentOutOfRangeException(nameof(capability)),
    };

    public static string Name(CapabilityKind capability) => capability switch
    {
        CapabilityKind.Camera => "Camera",
        CapabilityKind.Microphone => "Microphone",
        _ => "Location",
    };

    /// <summary>
    /// A FILETIME (100-ns ticks since 1601, UTC) stored as a QWORD. 0, negative or out-of-range values give null.
    /// Accepts the boxed long the registry returns and an 8-byte binary value.
    /// </summary>
    public static DateTimeOffset? FromFileTime(object? raw)
    {
        if (Ticks(raw) is not { } t || t <= 0)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromFileTime(t).ToUniversalTime();
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>True when the stop time is 0 (or missing) while a start time exists: the app is using it right now.</summary>
    public static bool IsInUse(object? startRaw, object? stopRaw) =>
        FromFileTime(startRaw) is not null && Ticks(stopRaw) is null or 0;

    private static long? Ticks(object? raw) => raw switch
    {
        long l => l,
        ulong u when u <= long.MaxValue => (long)u,
        int i => i,
        byte[] { Length: 8 } bytes => BitConverter.ToInt64(bytes, 0),
        _ => null,
    };

    /// <summary>How long the last use lasted; null while in use, when unknown or when the times are out of order.</summary>
    public static TimeSpan? Duration(DateTimeOffset? start, DateTimeOffset? stop, bool inUseNow)
    {
        if (inUseNow || start is not { } s || stop is not { } e || e < s)
        {
            return null;
        }

        return e - s;
    }

    /// <summary>A NonPackaged key name back to its path: "C:#Program Files#Zoom#bin#Zoom.exe" → "C:\Program Files\Zoom\bin\Zoom.exe".</summary>
    public static string DecodeNonPackagedPath(string keyName)
    {
        ArgumentNullException.ThrowIfNull(keyName);
        return keyName.Replace('#', '\\');
    }

    /// <summary>The program's file name without extension, as a fallback display name ("Zoom" for …\Zoom.exe).</summary>
    public static string ProgramName(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var trimmed = path.TrimEnd('\\');
        int slash = trimmed.LastIndexOf('\\');
        var file = slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
        int dot = file.LastIndexOf('.');
        return dot > 0 ? file[..dot] : file;
    }

    /// <summary>"Allow", "Deny" or "Prompt" (any case); anything else is unknown.</summary>
    public static ConsentValue ParseValue(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "ALLOW" => ConsentValue.Allow,
        "DENY" => ConsentValue.Deny,
        "PROMPT" => ConsentValue.Prompt,
        _ => ConsentValue.Unknown,
    };

    /// <summary>The string Windows writes for a value.</summary>
    public static string Format(ConsentValue value) => value switch
    {
        ConsentValue.Allow => "Allow",
        ConsentValue.Deny => "Deny",
        ConsentValue.Prompt => "Prompt",
        _ => throw new ArgumentOutOfRangeException(nameof(value), "Unknown is not a value Windows stores."),
    };

    /// <summary>The overall switch state in words, device switch first.</summary>
    public static string AccessText(ConsentValue device, ConsentValue apps, ConsentValue desktopApps)
    {
        if (device == ConsentValue.Deny)
        {
            return "Off for the whole device";
        }

        if (apps == ConsentValue.Deny)
        {
            return "Off for all apps";
        }

        return desktopApps == ConsentValue.Deny ? "On for Store apps, off for desktop apps" : "On";
    }

    /// <summary>
    /// Store apps have their own switch that can be turned off here. Desktop apps share one switch in Windows
    /// ("Let desktop apps access…"), so they are not revoked one by one.
    /// </summary>
    public static bool CanRevoke(CapabilityAppUsage app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.IsPackaged && app.Value != ConsentValue.Deny;
    }

    /// <summary>
    /// Merges the per-user and machine records of one capability: one row per app (the most recent use wins, the
    /// per-user record on a tie), apps in use now first, then the most recently used.
    /// </summary>
    public static IReadOnlyList<CapabilityAppUsage> Merge(IEnumerable<CapabilityAppUsage> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        return records
            .GroupBy(r => (r.IsPackaged, Key: r.KeyName.ToUpperInvariant()))
            .Select(g => g
                .OrderByDescending(r => r.InUseNow)
                .ThenByDescending(r => r.LastStart ?? DateTimeOffset.MinValue)
                .ThenBy(r => r.Scope == ConsentScope.CurrentUser ? 0 : 1)
                .First())
            .OrderByDescending(r => r.InUseNow)
            .ThenByDescending(r => r.LastStart ?? DateTimeOffset.MinValue)
            .ThenBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>"3 min", "1 h 5 min", "12 s".</summary>
    public static string FormatDuration(TimeSpan? duration)
    {
        if (duration is not { } d)
        {
            return "—";
        }

        if (d.TotalSeconds < 60)
        {
            return $"{Math.Max(0, (int)d.TotalSeconds)} s";
        }

        var text = new StringBuilder();
        if (d.TotalHours >= 1)
        {
            text.Append((int)d.TotalHours).Append(" h ");
        }

        text.Append(d.Minutes).Append(" min");
        return text.ToString();
    }
}
