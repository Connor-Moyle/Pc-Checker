namespace PcChecker.Core.Restore;

/// <summary>Shared, user-facing wording of the "restore point before changes" policy for confirmation dialogs.</summary>
public static class RestorePointPolicyText
{
    /// <summary>One-line note telling the user whether a restore point will be created before a guarded change.</summary>
    public static string Note(bool createBeforeChanges) => createBeforeChanges
        ? "A system restore point will be created before the change, which can take a few seconds (you can turn this off in Settings)."
        : "No restore point will be created before this change (turn it on in Settings if you want one).";
}
