using System.Text.Json;

namespace PcChecker.Core.Updates;

/// <summary>One release as returned by the GitHub Releases API, reduced to the fields the updater needs.</summary>
public sealed record GitHubRelease(
    string Tag,
    string? Name,
    bool IsPrerelease,
    bool IsDraft,
    string? Body,
    DateTimeOffset? PublishedAt,
    string? HtmlUrl)
{
    /// <summary>The tag without a leading "v"/"V" or "release-" prefix ("v1.2.3" → "1.2.3").</summary>
    public string Version => GitHubReleaseParser.NormalizeTag(Tag);
}

/// <summary>
/// Parses the JSON from <c>https://api.github.com/repos/{owner}/{repo}/releases</c> (an array) or
/// <c>/releases/latest</c> (a single object). Pure: no network, safe on any platform.
/// </summary>
public static class GitHubReleaseParser
{
    /// <summary>Parses release JSON. Returns an empty list for empty, invalid or unexpected JSON.</summary>
    public static IReadOnlyList<GitHubRelease> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return root.ValueKind switch
            {
                JsonValueKind.Array => root.EnumerateArray().Select(ReadRelease).Where(r => r is not null).Select(r => r!).ToList(),
                JsonValueKind.Object when ReadRelease(root) is { } single => [single],
                _ => [],
            };
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Picks the newest usable release: never a draft, prereleases only when <paramref name="allowPrerelease"/> is true.
    /// Releases are ordered by version, then by publish date, so an older tag published later does not win.
    /// </summary>
    public static GitHubRelease? PickLatest(IEnumerable<GitHubRelease> releases, bool allowPrerelease)
    {
        GitHubRelease? best = null;
        foreach (var release in releases)
        {
            if (release.IsDraft || (release.IsPrerelease && !allowPrerelease) || string.IsNullOrWhiteSpace(release.Tag))
            {
                continue;
            }

            if (best is null || IsBetter(release, best))
            {
                best = release;
            }
        }

        return best;
    }

    /// <summary>Strips a leading "v", "V", "version-" or "release-" and surrounding whitespace from a tag.</summary>
    public static string NormalizeTag(string? tag)
    {
        var text = (tag ?? string.Empty).Trim();
        foreach (var prefix in new[] { "release-", "release/", "version-", "version/", "v", "V" })
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && text.Length > prefix.Length && char.IsDigit(text[prefix.Length]))
            {
                text = text[prefix.Length..];
                break;
            }
        }

        return text;
    }

    private static bool IsBetter(GitHubRelease candidate, GitHubRelease current)
    {
        int byVersion = Common.VersionText.Compare(candidate.Version, current.Version);
        if (byVersion != 0)
        {
            return byVersion > 0;
        }

        return (candidate.PublishedAt ?? DateTimeOffset.MinValue) > (current.PublishedAt ?? DateTimeOffset.MinValue);
    }

    private static GitHubRelease? ReadRelease(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var tag = GetString(element, "tag_name");
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        DateTimeOffset? published = null;
        if (element.TryGetProperty("published_at", out var publishedElement) && publishedElement.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(publishedElement.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
        {
            published = parsed;
        }

        return new GitHubRelease(
            tag.Trim(),
            GetString(element, "name"),
            GetBool(element, "prerelease"),
            GetBool(element, "draft"),
            GetString(element, "body"),
            published,
            GetString(element, "html_url"));
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool GetBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
