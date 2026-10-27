using PcChecker.Core.Common;
using PcChecker.Core.Updates;
using Xunit;

namespace PcChecker.Core.Tests;

public class ExeUpdateTests
{
    private const string Hash = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    private static GitHubRelease Release(string? body = null, params GitHubReleaseAsset[] assets) =>
        new("v0.2.0", "PC Checker 0.2.0", false, false, body, DateTimeOffset.UtcNow, "https://example.test/r") { Assets = assets };

    [Fact]
    public void Parser_reads_assets_with_digest_and_size()
    {
        const string json = """
            [{"tag_name":"v0.2.0","prerelease":false,"draft":false,"assets":[
              {"name":"PcChecker.exe","browser_download_url":"https://example.test/PcChecker.exe","size":1234,"digest":"sha256:ABC"},
              {"name":"notes.txt","browser_download_url":"https://example.test/notes.txt","size":5}]}]
            """;
        var release = Assert.Single(GitHubReleaseParser.Parse(json));
        Assert.Equal(2, release.Assets.Count);
        Assert.Equal("PcChecker.exe", release.Assets[0].Name);
        Assert.Equal(1234, release.Assets[0].Size);
        Assert.Equal("sha256:ABC", release.Assets[0].Digest);
    }

    [Fact]
    public void Parser_tolerates_missing_or_malformed_assets()
    {
        const string json = """[{"tag_name":"v1.0.0","assets":[{"name":"x.exe"},42]}]""";
        Assert.Empty(Assert.Single(GitHubReleaseParser.Parse(json)).Assets);
        Assert.Empty(Assert.Single(GitHubReleaseParser.Parse("""{"tag_name":"v1.0.0"}""")).Assets);
    }

    [Fact]
    public void FindExeAsset_prefers_exact_name_then_single_exe()
    {
        var exact = new GitHubReleaseAsset("pcchecker.EXE", "u1", 1, null);
        var other = new GitHubReleaseAsset("Other.exe", "u2", 1, null);
        Assert.Same(exact, ExeUpdateRules.FindExeAsset(Release(null, other, exact)));
        Assert.Same(other, ExeUpdateRules.FindExeAsset(Release(null, other, new GitHubReleaseAsset("a.zip", "u3", 1, null))));
        Assert.Null(ExeUpdateRules.FindExeAsset(Release(null, other, new GitHubReleaseAsset("Second.exe", "u4", 1, null))));
        Assert.Null(ExeUpdateRules.FindExeAsset(Release()));
    }

    [Fact]
    public void FindExeAsset_picks_the_build_for_the_architecture()
    {
        var x64 = new GitHubReleaseAsset("PcChecker.exe", "u1", 1, null);
        var arm = new GitHubReleaseAsset("PcChecker-arm64.exe", "u2", 1, null);
        var release = Release(null, arm, x64);
        Assert.Same(x64, ExeUpdateRules.FindExeAsset(release, System.Runtime.InteropServices.Architecture.X64));
        Assert.Same(arm, ExeUpdateRules.FindExeAsset(release, System.Runtime.InteropServices.Architecture.Arm64));
        Assert.Null(ExeUpdateRules.FindExeAsset(Release(null, x64), System.Runtime.InteropServices.Architecture.Arm64));
        Assert.Null(ExeUpdateRules.FindExeAsset(Release(null, arm), System.Runtime.InteropServices.Architecture.X64));
    }

    [Fact]
    public void ExpectedSha256_uses_github_digest_first()
    {
        var asset = new GitHubReleaseAsset("PcChecker.exe", "u", 1, "sha256:" + Hash.ToUpperInvariant());
        Assert.Equal(Hash, ExeUpdateRules.ExpectedSha256(asset, "SHA-256: " + new string('0', 64)));
    }

    [Theory]
    [InlineData("**SHA-256:** `" + Hash + "`")]
    [InlineData("sha256 " + Hash)]
    [InlineData("Checksum\nSHA256 (PcChecker.exe) = " + Hash)]
    public void ExpectedSha256_falls_back_to_release_notes(string body)
    {
        var asset = new GitHubReleaseAsset("PcChecker.exe", "u", 1, null);
        Assert.Equal(Hash, ExeUpdateRules.ExpectedSha256(asset, body));
    }

    [Fact]
    public void ExpectedSha256_takes_the_line_that_names_the_asset()
    {
        const string arm = "1111111111111111111111111111111111111111111111111111111111111111";
        const string body = "## Checksums\n\n**SHA-256 (PcChecker.exe):** ``" + Hash + "``\n\n**SHA-256 (PcChecker-arm64.exe):** ``" + arm + "``\n";
        Assert.Equal(arm, ExeUpdateRules.ExpectedSha256(new GitHubReleaseAsset("PcChecker-arm64.exe", "u", 1, null), body));
        Assert.Equal(Hash, ExeUpdateRules.ExpectedSha256(new GitHubReleaseAsset("PcChecker.exe", "u", 1, null), body));
    }

    [Fact]
    public void ExpectedSha256_never_uses_the_hash_of_another_build()
    {
        const string body = "**SHA-256 (PcChecker.exe):** `" + Hash + "`";
        Assert.Null(ExeUpdateRules.ExpectedSha256(new GitHubReleaseAsset("PcChecker-arm64.exe", "u", 1, null), body));
    }

    [Fact]
    public void ExpectedSha256_is_null_without_a_valid_checksum()
    {
        var asset = new GitHubReleaseAsset("PcChecker.exe", "u", 1, "sha256:nothex");
        Assert.Null(ExeUpdateRules.ExpectedSha256(asset, "no checksum here"));
        Assert.Null(ExeUpdateRules.ExpectedSha256(asset, null));
    }

    [Theory]
    [InlineData(@"C:\Tools\PcChecker.exe", true)]
    [InlineData(@"C:\Tools\PcChecker-0.1.0.exe", true)]
    [InlineData(@"C:\Program Files\dotnet\dotnet.exe", false)]
    [InlineData(@"C:\Tools\Other.exe", false)]
    [InlineData(null, false)]
    public void CanSelfUpdate_only_for_published_exe(string? path, bool expected) =>
        Assert.Equal(expected, ExeUpdateRules.CanSelfUpdate(path));

    [Fact]
    public void MatchesSha256_rechecks_the_content_before_install()
    {
        // Hash is the SHA-256 of "test".
        Assert.True(ExeUpdateRules.MatchesSha256(new MemoryStream("test"u8.ToArray()), Hash));
        Assert.True(ExeUpdateRules.MatchesSha256(new MemoryStream("test"u8.ToArray()), Hash.ToUpperInvariant()));
        Assert.False(ExeUpdateRules.MatchesSha256(new MemoryStream("swapped"u8.ToArray()), Hash));
        Assert.False(ExeUpdateRules.MatchesSha256(new MemoryStream("test"u8.ToArray()), null));
        Assert.False(ExeUpdateRules.MatchesSha256(new MemoryStream("test"u8.ToArray()), " "));
    }

    [Fact]
    public void Download_and_old_paths_are_safe()
    {
        Assert.EndsWith("PcChecker-0.2.0-beta.1.exe", ExeUpdateRules.DownloadFilePath("updates", "0.2.0-beta.1"));
        Assert.EndsWith("PcChecker-1.0_x.exe", ExeUpdateRules.DownloadFilePath("updates", "1.0/x"));
        Assert.Equal(@"C:\a\PcChecker.exe.old", ExeUpdateRules.OldFilePath(@"C:\a\PcChecker.exe"));
    }

    [Fact]
    public void Command_line_reads_update_hand_over_switches()
    {
        var options = CommandLineOptions.Parse(["--wait-pid", "4321", "--updated-from=0.1.0"]);
        Assert.Equal(4321, options.WaitForProcessId);
        Assert.Equal("0.1.0", options.UpdatedFrom);
        Assert.Empty(options.UnknownArguments);
        Assert.Equal(AppMode.Normal, options.Mode);
    }

    [Fact]
    public void Command_line_rejects_bad_pid()
    {
        var options = CommandLineOptions.Parse(["--wait-pid", "abc"]);
        Assert.Null(options.WaitForProcessId);
        Assert.Contains("--wait-pid", options.UnknownArguments);
    }
}
