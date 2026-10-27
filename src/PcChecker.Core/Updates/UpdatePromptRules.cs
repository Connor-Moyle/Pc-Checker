using System.Text.RegularExpressions;
using PcChecker.Core.Settings;

namespace PcChecker.Core.Updates;

/// <summary>What PC Checker does when GitHub has a newer release.</summary>
public enum UpdateMode
{
    /// <summary>Show a dialog: Update now, Later or Skip this version (the default).</summary>
    [System.ComponentModel.Description("Ask me")]
    Ask,

    /// <summary>Download and verify the update straight away, then ask before restarting.</summary>
    [System.ComponentModel.Description("Download it, then ask to restart")]
    Automatic,

    /// <summary>Only show a notification.</summary>
    [System.ComponentModel.Description("Just tell me")]
    NotifyOnly,
}

/// <summary>The decision for one check result.</summary>
public enum UpdatePromptAction
{
    None,
    Ask,
    DownloadThenAskToRestart,
    Notify,

    /// <summary>A copy that cannot replace itself (not a published PcChecker.exe): point to the releases page.</summary>
    NotifyDownloadPage,
}

/// <summary>Pure rules for the automatic update check, so the prompter's behaviour is testable.</summary>
public static partial class UpdatePromptRules
{
    /// <summary>How often a running PC Checker (including one in the tray) checks GitHub again.</summary>
    public static readonly TimeSpan RecheckInterval = TimeSpan.FromHours(6);

    /// <summary>The mode in effect, honouring the older "download in the background" switch.</summary>
    public static UpdateMode EffectiveMode(UpdateSettings settings) =>
        settings.Mode == UpdateMode.Ask && settings.AutoDownload ? UpdateMode.Automatic : settings.Mode;

    /// <summary>
    /// Decides what to do with a check result. <paramref name="alreadyHandled"/> is true when this version was already
    /// offered in this run (so a re-check every few hours does not nag).
    /// </summary>
    public static UpdatePromptAction Decide(AppUpdateInfo? info, UpdateSettings settings, bool canSelfUpdate, bool alreadyHandled)
    {
        if (info is null || !info.IsNewer || alreadyHandled)
        {
            return UpdatePromptAction.None;
        }

        if (!string.IsNullOrWhiteSpace(settings.SkippedVersion) &&
            string.Equals(Normalize(settings.SkippedVersion), Normalize(info.LatestVersion), StringComparison.OrdinalIgnoreCase))
        {
            return UpdatePromptAction.None;
        }

        if (!canSelfUpdate)
        {
            return UpdatePromptAction.NotifyDownloadPage;
        }

        return EffectiveMode(settings) switch
        {
            UpdateMode.Automatic => UpdatePromptAction.DownloadThenAskToRestart,
            UpdateMode.NotifyOnly => UpdatePromptAction.Notify,
            _ => UpdatePromptAction.Ask,
        };
    }

    /// <summary>
    /// The first few meaningful lines of GitHub release notes for the update dialog: headings, download instructions,
    /// checksums and links are left out; list items keep their text.
    /// </summary>
    public static string Highlights(string? releaseNotes, int maxLines = 6)
    {
        if (string.IsNullOrWhiteSpace(releaseNotes))
        {
            return string.Empty;
        }

        var lines = new List<string>();
        foreach (var raw in releaseNotes.Replace("\r", string.Empty).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("**SHA", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("SHA-256", StringComparison.OrdinalIgnoreCase) || line.StartsWith("**Full Changelog", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("PcChecker.exe", StringComparison.OrdinalIgnoreCase) || line.Contains("SmartScreen", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Download ", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            line = Bullet().Replace(line, "• ");
            line = Link().Replace(line, "$1");
            line = line.Replace("**", string.Empty).Replace("`", string.Empty);
            line = Emphasis().Replace(line, "$1");
            if (line.Trim('•', ' ').Length == 0)
            {
                continue;
            }

            lines.Add(line);
            if (lines.Count == maxLines)
            {
                break;
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string Normalize(string version) => version.Trim().TrimStart('v', 'V');

    [GeneratedRegex(@"^[-*+]\s+")]
    private static partial Regex Bullet();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]+\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"(?<!\w)[*_]([^*_]+)[*_](?!\w)")]
    private static partial Regex Emphasis();
}
