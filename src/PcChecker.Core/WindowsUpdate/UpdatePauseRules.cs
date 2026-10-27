using System.Globalization;

namespace PcChecker.Core.WindowsUpdate;

/// <summary>The values a pause writes, worked out by <see cref="UpdatePauseRules.Plan"/>.</summary>
public sealed record UpdatePausePlan(bool Allowed, DateTimeOffset Start, DateTimeOffset Expiry, bool IsExtension, bool WasShortened, string Message);

/// <summary>
/// Date maths for pausing Windows Update the way the Settings app does. Settings stores the pause as UTC times in
/// <c>HKLM\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings</c> (<c>PauseUpdatesStartTime</c>/<c>PauseUpdatesExpiryTime</c>
/// plus the feature and quality pairs), formatted as <c>2025-01-31T09:15:00Z</c>. A pause can be extended a week at a
/// time, but never beyond the limit counted from when it started (35 days unless <c>FlightSettingsMaxPauseDays</c>
/// says otherwise). Pure.
/// </summary>
public static class UpdatePauseRules
{
    /// <summary>The longest pause Windows 11 offers in Settings: five weeks.</summary>
    public const int DefaultMaxPauseDays = 35;

    /// <summary>The page offers whole weeks, 1 to 5, like Settings.</summary>
    public const int MaxWeeks = 5;

    public const string SettingsKey = @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings";
    public const string StartValue = "PauseUpdatesStartTime";
    public const string ExpiryValue = "PauseUpdatesExpiryTime";
    public const string FeatureStartValue = "PauseFeatureUpdatesStartTime";
    public const string FeatureEndValue = "PauseFeatureUpdatesEndTime";
    public const string QualityStartValue = "PauseQualityUpdatesStartTime";
    public const string QualityEndValue = "PauseQualityUpdatesEndTime";
    public const string MaxPauseDaysValue = "FlightSettingsMaxPauseDays";

    /// <summary>Every value a pause writes and a resume removes.</summary>
    public static IReadOnlyList<string> AllValues { get; } =
        [StartValue, ExpiryValue, FeatureStartValue, FeatureEndValue, QualityStartValue, QualityEndValue];

    private const string RegistryFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    /// <summary>Formats a time the way Settings writes it: UTC, whole seconds, a trailing Z.</summary>
    public static string FormatRegistryTime(DateTimeOffset time) =>
        time.ToUniversalTime().ToString(RegistryFormat, CultureInfo.InvariantCulture);

    /// <summary>Reads a stored pause time. Accepts the Settings format and any ISO 8601 UTC time; anything else is null.</summary>
    public static DateTimeOffset? ParseRegistryTime(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();
        if (DateTimeOffset.TryParseExact(trimmed, RegistryFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var exact))
        {
            return exact;
        }

        return DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var loose)
            ? loose
            : null;
    }

    /// <summary>The pause state from the stored values: paused while the expiry time is still ahead.</summary>
    public static UpdatePauseState ReadState(string? startText, string? expiryText, DateTimeOffset now)
    {
        var expiry = ParseRegistryTime(expiryText);
        if (expiry is null || expiry <= now)
        {
            return UpdatePauseState.NotPaused;
        }

        var start = ParseRegistryTime(startText);
        return new UpdatePauseState(true, start is { } s && s <= now ? s : null, expiry);
    }

    /// <summary>
    /// The first Windows build (Windows 10 version 2004) whose Settings app pauses through these values with the
    /// week-by-week limit this class follows. Older builds are sent to Settings instead of being written to.
    /// </summary>
    public const int FirstSupportedBuild = 19041;

    /// <summary>True when PC Checker may write the pause values itself on this Windows build.</summary>
    public static bool SupportsRegistryPause(int windowsBuild) => windowsBuild >= FirstSupportedBuild;

    /// <summary>The configured pause limit in days; a missing or nonsense value means the 35-day default.</summary>
    public static int EffectiveMaxPauseDays(int? configured) =>
        configured is >= 7 and <= 3650 ? configured.Value : DefaultMaxPauseDays;

    /// <summary>How many whole weeks can still be added to the current pause (or a new one), 0 to 5.</summary>
    public static int WeeksAvailable(UpdatePauseState current, DateTimeOffset now, int maxPauseDays)
    {
        if (HasUnknownStart(current, now))
        {
            return 0;
        }

        var limit = Limit(current, now, maxPauseDays);
        var from = current.IsPaused && current.PausedUntil is { } until ? until : now;
        var days = (limit - from).TotalDays;
        return Math.Clamp((int)Math.Floor(days / 7.0 + 1e-9), 0, MaxWeeks);
    }

    /// <summary>
    /// Works out the start and expiry for pausing <paramref name="weeks"/> more weeks. A running pause is extended
    /// from its current expiry and keeps its start; the expiry is capped at the start plus the pause limit.
    /// </summary>
    public static UpdatePausePlan Plan(UpdatePauseState current, DateTimeOffset now, int weeks, int maxPauseDays)
    {
        ArgumentNullException.ThrowIfNull(current);
        var nowUtc = TruncateToSeconds(now.ToUniversalTime());
        if (weeks is < 1 or > MaxWeeks)
        {
            return Refuse(nowUtc, $"Choose between 1 and {MaxWeeks} weeks.");
        }

        bool extending = current.IsPaused && current.PausedUntil is { } until && until > nowUtc;
        if (extending && current.PausedSince is null)
        {
            // Without the start, the limit (counted from the start) is unknown; guessing "now" could pass it.
            return Refuse(nowUtc, UnknownStartMessage);
        }

        var start = extending ? TruncateToSeconds(current.PausedSince!.Value.ToUniversalTime()) : nowUtc;
        var from = extending ? TruncateToSeconds(current.PausedUntil!.Value.ToUniversalTime()) : nowUtc;
        var limit = start.AddDays(EffectiveMaxPauseDays(maxPauseDays));
        var expiry = from.AddDays(7 * weeks);
        bool shortened = false;
        if (expiry > limit)
        {
            expiry = limit;
            shortened = true;
        }

        if (expiry <= from.AddHours(1))
        {
            return Refuse(nowUtc, "Updates are already paused for as long as Windows allows. When the pause ends, Windows installs the latest updates before it can be paused again.");
        }

        var message = extending
            ? $"Updates stay paused until {expiry.ToLocalTime():d}."
            : $"Updates are paused until {expiry.ToLocalTime():d}.";
        if (shortened)
        {
            message += " That is as long as Windows allows a pause to last.";
        }

        return new UpdatePausePlan(true, start, expiry, extending, shortened, message);
    }

    /// <summary>Why a running pause whose start cannot be read is not extended here.</summary>
    public const string UnknownStartMessage =
        "Windows did not record when the current pause started, so PC Checker cannot tell how much longer it may last. Extend the pause in Settings → Windows Update instead.";

    /// <summary>True for a running pause whose start time is missing or unreadable: it can only be extended in Settings.</summary>
    public static bool HasUnknownStart(UpdatePauseState current, DateTimeOffset now) =>
        current is { IsPaused: true, PausedSince: null, PausedUntil: { } until } && until > now;

    private static DateTimeOffset Limit(UpdatePauseState current, DateTimeOffset now, int maxPauseDays)
    {
        var start = current.IsPaused ? current.PausedSince ?? now : now;
        return start.AddDays(EffectiveMaxPauseDays(maxPauseDays));
    }

    private static UpdatePausePlan Refuse(DateTimeOffset now, string message) => new(false, now, now, false, false, message);

    private static DateTimeOffset TruncateToSeconds(DateTimeOffset time) =>
        new(time.Ticks - time.Ticks % TimeSpan.TicksPerSecond, time.Offset);
}
