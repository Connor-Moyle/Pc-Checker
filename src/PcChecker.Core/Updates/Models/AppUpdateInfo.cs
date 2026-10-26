namespace PcChecker.Core.Updates;

public sealed record AppUpdateInfo(
    string CurrentVersion,
    string LatestVersion,
    string? ReleaseNotes,
    DateTimeOffset? PublishedAt,
    bool IsPrerelease,
    bool IsNewer);
