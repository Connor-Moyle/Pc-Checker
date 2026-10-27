namespace PcChecker.Core.WindowsUpdate;

/// <summary>The text for an install run, shared by the Windows and demo services and the page. Pure.</summary>
public static class UpdateInstallSummary
{
    /// <summary>"2 of 3 updates installed. Restart Windows to finish."</summary>
    public static string Summarize(UpdateInstallResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var text = result.Items.Count == 1
            ? (result.Installed == 1 ? "The update was installed." : "The update was not installed.")
            : $"{result.Installed} of {result.Items.Count} updates installed.";
        return result.RebootRequired ? text + " Restart Windows to finish." : text;
    }

    /// <summary>One line per update for the results list: "title: Installed." or "title: Not installed: reason".</summary>
    public static IReadOnlyList<string> Lines(UpdateInstallResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Items.Select(i => $"{i.Update.Title}: {i.Message}").ToList();
    }

    /// <summary>
    /// The updates "Install all" picks: everything listed except optional ones (previews and new Windows versions,
    /// which Settings also leaves to the user) and ones that must be installed from Settings.
    /// </summary>
    public static IReadOnlyList<PendingUpdate> InstallAllSelection(IEnumerable<PendingUpdate> updates) =>
        updates.Where(u => !u.IsOptional && !u.NeedsUserInput).ToList();
}
