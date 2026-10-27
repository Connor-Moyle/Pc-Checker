using PcChecker.Core.Updates;
using Xunit;

namespace PcChecker.Core.Tests;

public class UpdatesTests
{
    private const string ReleasesJson = """
        [
          { "tag_name": "v0.3.0-beta.1", "name": "0.3.0 beta 1", "prerelease": true, "draft": false, "body": "Beta notes", "published_at": "2026-10-20T10:00:00Z", "html_url": "https://github.com/x/y/releases/tag/v0.3.0-beta.1" },
          { "tag_name": "v0.2.0", "name": "0.2.0", "prerelease": false, "draft": false, "body": "Stable notes", "published_at": "2026-10-01T10:00:00Z", "html_url": "https://github.com/x/y/releases/tag/v0.2.0" },
          { "tag_name": "v9.9.9", "name": "Draft", "prerelease": false, "draft": true, "body": null, "published_at": null, "html_url": null },
          { "tag_name": "v0.1.5", "name": "0.1.5", "prerelease": false, "draft": false, "body": "Older but published later", "published_at": "2026-10-25T10:00:00Z", "html_url": "https://github.com/x/y/releases/tag/v0.1.5" }
        ]
        """;

    [Fact]
    public void Parser_reads_every_release_with_fields()
    {
        var releases = GitHubReleaseParser.Parse(ReleasesJson);
        Assert.Equal(4, releases.Count);
        var stable = releases.Single(r => r.Tag == "v0.2.0");
        Assert.Equal("0.2.0", stable.Version);
        Assert.False(stable.IsPrerelease);
        Assert.Equal("Stable notes", stable.Body);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero), stable.PublishedAt);
        Assert.Contains("v0.2.0", stable.HtmlUrl);
    }

    [Fact]
    public void Parser_accepts_single_object_and_rejects_garbage()
    {
        var single = GitHubReleaseParser.Parse("""{ "tag_name": "1.0.0", "prerelease": false, "draft": false }""");
        Assert.Single(single);
        Assert.Equal("1.0.0", single[0].Version);
        Assert.Empty(GitHubReleaseParser.Parse("not json"));
        Assert.Empty(GitHubReleaseParser.Parse(string.Empty));
        Assert.Empty(GitHubReleaseParser.Parse(null));
        Assert.Empty(GitHubReleaseParser.Parse("[{ \"name\": \"no tag\" }]"));
    }

    [Fact]
    public void PickLatest_skips_drafts_and_prereleases_on_stable_channel()
    {
        var latest = GitHubReleaseParser.PickLatest(GitHubReleaseParser.Parse(ReleasesJson), allowPrerelease: false);
        Assert.NotNull(latest);
        Assert.Equal("0.2.0", latest!.Version);
    }

    [Fact]
    public void PickLatest_offers_prerelease_on_beta_channel()
    {
        var latest = GitHubReleaseParser.PickLatest(GitHubReleaseParser.Parse(ReleasesJson), allowPrerelease: true);
        Assert.Equal("0.3.0-beta.1", latest!.Version);
        Assert.True(latest.IsPrerelease);
    }

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("V2.0", "2.0")]
    [InlineData("release-1.0.0", "1.0.0")]
    [InlineData("1.0.0", "1.0.0")]
    [InlineData("  v3.1  ", "3.1")]
    [InlineData("version", "version")]
    public void Tag_normalization_strips_prefixes(string tag, string expected) =>
        Assert.Equal(expected, GitHubReleaseParser.NormalizeTag(tag));

    [Theory]
    [InlineData("0.1.0", "0.1.1", true)]
    [InlineData("0.1.0", "v0.2.0", true)]
    [InlineData("v0.1.0", "0.1.0", false)]
    [InlineData("1.2.3.4", "1.2.3.5", true)]
    [InlineData("1.2.3.4", "1.2.3", false)]
    [InlineData("1.2", "1.2.0", false)]
    [InlineData("1.0.0+abc123", "1.0.0", false)]
    [InlineData("1.0.0", "1.0.1+build", true)]
    [InlineData("2.0.0", "1.9.9", false)]
    [InlineData("1.0.0-beta.1", "1.0.0", true)]
    [InlineData("1.0.0", "1.0.0-beta.1", false)]
    [InlineData("1.0.0-beta.1", "1.0.0-beta.2", true)]
    [InlineData("1.2.0-beta.9", "1.2.0-beta.10", true)]
    [InlineData("1.2.0-beta.10", "1.2.0-beta.9", false)]
    [InlineData("1.2.0-alpha", "1.2.0-alpha.1", true)]
    [InlineData("1.2.0-alpha.1", "1.2.0-beta", true)]
    [InlineData("1.2.0-rc.1", "1.2.0-beta.11", false)]
    [InlineData("", "1.0.0", true)]
    [InlineData("1.0.0", "", false)]
    public void IsNewer_follows_version_semantics(string current, string latest, bool expected) =>
        Assert.Equal(expected, UpdateDecision.IsNewer(current, latest));

    [Fact]
    public void FromRelease_builds_update_info_with_notes_and_newer_flag()
    {
        var release = GitHubReleaseParser.Parse(ReleasesJson).Single(r => r.Tag == "v0.2.0");
        var info = UpdateDecision.FromRelease("v0.1.0+deadbeef", release);
        Assert.Equal("0.1.0", info.CurrentVersion);
        Assert.Equal("0.2.0", info.LatestVersion);
        Assert.Equal("Stable notes", info.ReleaseNotes);
        Assert.True(info.IsNewer);
        Assert.False(info.IsPrerelease);
        Assert.NotNull(info.PublishedAt);

        var same = UpdateDecision.FromRelease("0.2.0", release);
        Assert.False(same.IsNewer);
    }

    [Fact]
    public void FromRelease_falls_back_to_name_when_body_is_empty()
    {
        var release = new GitHubRelease("v1.0.0", "First release", false, false, "  ", null, null);
        Assert.Equal("First release", UpdateDecision.FromRelease("0.9.0", release).ReleaseNotes);
    }

    [Fact]
    public async Task Demo_update_service_reports_a_newer_release_and_downloads()
    {
        var svc = new DemoAppUpdateService();
        var info = await svc.CheckAsync(CancellationToken.None);
        Assert.NotNull(info);
        Assert.True(info!.IsNewer);
        Assert.Equal(DemoAppUpdateService.LatestVersion, info.LatestVersion);
        Assert.Contains("Fans", info.ReleaseNotes);
        Assert.False(svc.IsUpdateDownloaded);
        var reports = new List<Common.ProgressUpdate>();
        var result = await svc.DownloadAsync(new Progress<Common.ProgressUpdate>(reports.Add), CancellationToken.None);
        Assert.True(result.Success);
        Assert.True(svc.IsUpdateDownloaded);
    }

    [Fact]
    public void Update_links_point_at_the_project()
    {
        Assert.StartsWith("https://github.com/Connor-Moyle/Pc-Checker", UpdateLinks.ReleasesPage);
        Assert.StartsWith("https://api.github.com/repos/Connor-Moyle/Pc-Checker/releases", UpdateLinks.ReleasesApi);
    }
}
