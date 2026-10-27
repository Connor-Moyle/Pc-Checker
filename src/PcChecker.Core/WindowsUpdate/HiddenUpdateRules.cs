namespace PcChecker.Core.WindowsUpdate;

/// <summary>What to do when asked to hide or show an update, given what Windows Update says about it.</summary>
public enum HideDecision
{
    /// <summary>Set <c>IUpdate.IsHidden</c> to the wanted value.</summary>
    Change,

    /// <summary>It already is in the wanted state; nothing to do.</summary>
    AlreadyDone,

    /// <summary>Windows Update does not let mandatory updates be hidden.</summary>
    Mandatory,
}

/// <summary>
/// The rules behind "Hide update" and "Show again": the same <c>IUpdate.IsHidden</c> flag Microsoft's "Show or hide
/// updates" troubleshooter sets, the search criteria that find an update by id or list the hidden ones, and the
/// messages. Pure.
/// </summary>
public static class HiddenUpdateRules
{
    /// <summary>Finds every hidden update that is not installed.</summary>
    public const string HiddenCriteria = "IsHidden=1 and IsInstalled=0";

    /// <summary>Every software update that is not installed, hidden or not (to find a removed update by KB).</summary>
    public const string NotInstalledCriteria = "IsInstalled=0 and Type='Software'";

    /// <summary>Decides what to do. <paramref name="hide"/> is the wanted state.</summary>
    public static HideDecision Decide(bool hide, bool isHidden, bool isMandatory)
    {
        if (hide == isHidden)
        {
            return HideDecision.AlreadyDone;
        }

        return hide && isMandatory ? HideDecision.Mandatory : HideDecision.Change;
    }

    /// <summary>The message for a decision about <paramref name="title"/>.</summary>
    public static string Message(HideDecision decision, bool hide, string title) => decision switch
    {
        HideDecision.AlreadyDone => hide ? $"\"{title}\" is already hidden." : $"\"{title}\" is not hidden.",
        HideDecision.Mandatory => $"Windows Update marks \"{title}\" as required, so it cannot be hidden.",
        _ => hide
            ? $"\"{title}\" is hidden. Windows Update will not offer or install it until you show it again."
            : $"\"{title}\" is shown again. Windows Update may install it the next time it checks.",
    };

    /// <summary>
    /// The search criteria that find one update by its Windows Update id, or null when the id is not a GUID (so nothing
    /// unexpected ever reaches the search string).
    /// </summary>
    public static string? CriteriaForId(string? updateId) =>
        Guid.TryParse(updateId?.Trim(), out var id) ? $"UpdateID='{id:D}'" : null;

    /// <summary>The state as a word for the lists.</summary>
    public static string StateText(bool isHidden, bool isInstalled) => (isHidden, isInstalled) switch
    {
        (true, false) => "Hidden",
        (true, true) => "Installed (hidden)",
        (false, true) => "Installed",
        _ => "Offered",
    };

    /// <summary>
    /// True when <paramref name="kbs"/> (as Windows Update lists them, digits or "KB…") or the title contain
    /// <paramref name="kb"/>. Used to find a removed update when its id is unknown.
    /// </summary>
    public static bool MatchesKb(string? kb, IEnumerable<string>? kbs, string? title)
    {
        if (InstalledUpdateRules.NormalizeKb(kb) is not { } wanted)
        {
            return false;
        }

        if ((kbs ?? []).Any(k => string.Equals(InstalledUpdateRules.NormalizeKb(k), wanted, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return string.Equals(UpdateClassifier.ExtractKb(title), wanted, StringComparison.OrdinalIgnoreCase);
    }
}
