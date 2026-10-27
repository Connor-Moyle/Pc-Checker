namespace PcChecker.Core.Leftovers;

/// <summary>
/// What the scan learned about one candidate, as plain facts for <see cref="LeftoverScoring.Score"/>.
/// <paramref name="SinceLastChange"/> is how long ago anything in a folder was last written.
/// <paramref name="TargetFolderMissing"/> (services, tasks, shortcuts) is true when the folder of the missing file is
/// gone too, which means the whole program went rather than one file during an update.
/// </summary>
public sealed record LeftoverEvidence(
    LeftoverKind Kind,
    bool MatchesFocus = false,
    bool UnderInstalledVendor = false,
    bool ContainsPrograms = false,
    TimeSpan? SinceLastChange = null,
    bool InUserData = false,
    bool TargetFolderMissing = false,
    bool KeyIsEmpty = false);

/// <summary>
/// Confidence scoring and the "why it looks orphaned" text. High means the scan is sure (empty folders, things that
/// point at a program folder that is gone, traces of the program just uninstalled); only High is ticked for removal by
/// default. Folders that hold programs and were used recently, and anything inside a folder of a maker that still has
/// programs installed, are Low: they may be a portable program or shared by another product. Pure.
/// </summary>
public static class LeftoverScoring
{
    /// <summary>A folder nobody wrote to for this long is very likely abandoned.</summary>
    public static readonly TimeSpan LongUnused = TimeSpan.FromDays(180);

    /// <summary>A folder written to more recently than this may belong to something still in use.</summary>
    public static readonly TimeSpan RecentlyUsed = TimeSpan.FromDays(30);

    public static LeftoverConfidence Score(LeftoverEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.MatchesFocus)
        {
            return LeftoverConfidence.High;
        }

        switch (evidence.Kind)
        {
            case LeftoverKind.EmptyFolder:
                return LeftoverConfidence.High;

            case LeftoverKind.Folder:
            {
                if (evidence.UnderInstalledVendor)
                {
                    return LeftoverConfidence.Low;
                }

                bool recent = evidence.SinceLastChange is { } age && age < RecentlyUsed;
                bool old = evidence.SinceLastChange is { } since && since >= LongUnused;
                if (recent)
                {
                    return LeftoverConfidence.Low;
                }

                if (evidence.InUserData)
                {
                    // Settings and saved games: never sure enough to tick them for the user.
                    return old ? LeftoverConfidence.Medium : LeftoverConfidence.Low;
                }

                if (evidence.ContainsPrograms)
                {
                    return old ? LeftoverConfidence.Medium : LeftoverConfidence.Low;
                }

                return old ? LeftoverConfidence.High : LeftoverConfidence.Medium;
            }

            case LeftoverKind.RegistryKey:
                if (evidence.KeyIsEmpty)
                {
                    return LeftoverConfidence.High;
                }

                return evidence.UnderInstalledVendor ? LeftoverConfidence.Low : LeftoverConfidence.Medium;

            case LeftoverKind.Service:
            case LeftoverKind.ScheduledTask:
            case LeftoverKind.Shortcut:
                return evidence.TargetFolderMissing ? LeftoverConfidence.High : LeftoverConfidence.Medium;

            default:
                return LeftoverConfidence.Low;
        }
    }

    /// <summary>One sentence saying what the finding is and why it looks orphaned.</summary>
    public static string Explain(LeftoverEvidence evidence, string? missingTarget = null, string? focusName = null)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        string focus = evidence.MatchesFocus && !string.IsNullOrWhiteSpace(focusName) ? $" It belongs to {focusName}, which you just uninstalled." : string.Empty;
        string missing = string.IsNullOrWhiteSpace(missingTarget) ? "a file" : missingTarget;
        string text = evidence.Kind switch
        {
            LeftoverKind.EmptyFolder => "An empty folder: it holds no files, only what an uninstaller left behind.",
            LeftoverKind.Folder when evidence.UnderInstalledVendor =>
                "Inside the folder of a maker that still has other programs installed, but no installed program uses it.",
            LeftoverKind.Folder => "No installed program or publisher matches this folder's name, and no program is installed in it.",
            LeftoverKind.RegistryKey when evidence.KeyIsEmpty => "An empty settings key with nothing in it.",
            LeftoverKind.RegistryKey when evidence.UnderInstalledVendor =>
                "Settings of a product that is not installed, kept by a maker that still has other programs installed.",
            LeftoverKind.RegistryKey => "Settings of a maker or product that no longer has anything installed.",
            LeftoverKind.Service => $"A service whose program file is gone: {missing}.",
            LeftoverKind.ScheduledTask => $"A scheduled task that starts a file that is gone: {missing}.",
            LeftoverKind.Shortcut => $"A Start menu shortcut to a file that is gone: {missing}.",
            _ => "Looks like a leftover.",
        };

        if (evidence.Kind is LeftoverKind.Service or LeftoverKind.ScheduledTask or LeftoverKind.Shortcut && evidence.TargetFolderMissing)
        {
            text += " Its whole folder is gone too.";
        }
        else if (evidence.Kind == LeftoverKind.Folder && evidence.SinceLastChange is { } age && age >= LongUnused)
        {
            text += $" Nothing in it changed for {Math.Floor(age.TotalDays / 30.4):0} months.";
        }

        return text + focus;
    }
}
