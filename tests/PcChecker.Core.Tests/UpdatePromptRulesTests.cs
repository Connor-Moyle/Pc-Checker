using PcChecker.Core.Settings;
using PcChecker.Core.Updates;
using Xunit;

namespace PcChecker.Core.Tests;

public class UpdatePromptRulesTests
{
    private static AppUpdateInfo Newer(string latest = "0.5.0") => new("0.4.0", latest, "notes", DateTimeOffset.UtcNow, false, true);

    [Fact]
    public void Asks_by_default_when_a_newer_release_exists()
    {
        Assert.Equal(UpdatePromptAction.Ask, UpdatePromptRules.Decide(Newer(), new UpdateSettings(), canSelfUpdate: true, alreadyHandled: false));
    }

    [Fact]
    public void Does_nothing_without_a_newer_release_or_after_it_was_offered()
    {
        var same = new AppUpdateInfo("0.4.0", "0.4.0", null, null, false, false);
        Assert.Equal(UpdatePromptAction.None, UpdatePromptRules.Decide(same, new UpdateSettings(), true, false));
        Assert.Equal(UpdatePromptAction.None, UpdatePromptRules.Decide(null, new UpdateSettings(), true, false));
        Assert.Equal(UpdatePromptAction.None, UpdatePromptRules.Decide(Newer(), new UpdateSettings(), true, alreadyHandled: true));
    }

    [Theory]
    [InlineData("0.5.0")]
    [InlineData("v0.5.0")]
    [InlineData(" V0.5.0 ")]
    public void A_skipped_version_is_not_offered_again(string skipped)
    {
        var settings = new UpdateSettings { SkippedVersion = skipped };
        Assert.Equal(UpdatePromptAction.None, UpdatePromptRules.Decide(Newer("0.5.0"), settings, true, false));
    }

    [Fact]
    public void A_newer_release_than_the_skipped_one_is_offered()
    {
        var settings = new UpdateSettings { SkippedVersion = "0.5.0" };
        Assert.Equal(UpdatePromptAction.Ask, UpdatePromptRules.Decide(Newer("0.5.1"), settings, true, false));
    }

    [Theory]
    [InlineData(UpdateMode.Automatic, false, UpdatePromptAction.DownloadThenAskToRestart)]
    [InlineData(UpdateMode.NotifyOnly, false, UpdatePromptAction.Notify)]
    [InlineData(UpdateMode.Ask, true, UpdatePromptAction.DownloadThenAskToRestart)]
    [InlineData(UpdateMode.NotifyOnly, true, UpdatePromptAction.Notify)]
    public void Follows_the_chosen_mode(UpdateMode mode, bool legacyAutoDownload, UpdatePromptAction expected)
    {
        var settings = new UpdateSettings { Mode = mode, AutoDownload = legacyAutoDownload };
        Assert.Equal(expected, UpdatePromptRules.Decide(Newer(), settings, true, false));
    }

    [Fact]
    public void A_copy_that_cannot_replace_itself_points_to_the_download_page()
    {
        Assert.Equal(UpdatePromptAction.NotifyDownloadPage, UpdatePromptRules.Decide(Newer(), new UpdateSettings(), canSelfUpdate: false, alreadyHandled: false));
    }

    [Fact]
    public void Highlights_keep_the_changes_and_drop_download_boilerplate()
    {
        const string notes = """
            ## Download and run

            Download **PcChecker.exe** below and double-click it.
            - The first time, SmartScreen may say *Windows protected your PC*.
            **SHA-256 (PcChecker.exe):** `abc`

            ## What's Changed
            * **Disk space** page shows what fills a drive by @someone in [#12](https://github.com/x/y/pull/12)
            - Faster *History* charts
            **Full Changelog**: https://github.com/x/y/compare/v1...v2
            """;
        var text = UpdatePromptRules.Highlights(notes);
        Assert.Equal("• Disk space page shows what fills a drive by @someone in #12" + Environment.NewLine + "• Faster History charts", text);
    }

    [Fact]
    public void Highlights_are_capped_and_empty_notes_give_nothing()
    {
        var many = string.Join("\n", Enumerable.Range(1, 20).Select(i => $"- change {i}"));
        Assert.Equal(3, UpdatePromptRules.Highlights(many, 3).Split(Environment.NewLine).Length);
        Assert.Equal(string.Empty, UpdatePromptRules.Highlights(null));
        Assert.Equal(string.Empty, UpdatePromptRules.Highlights("   "));
    }
}
