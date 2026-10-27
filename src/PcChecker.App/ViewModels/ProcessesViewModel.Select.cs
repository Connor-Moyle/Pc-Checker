namespace PcChecker.App.ViewModels;

/// <summary>
/// "Go to the process" from other pages (Services): clears the search and the flagged-only filter, opens the group
/// that holds the process and selects its row. When the list has not been read yet, the selection waits for it.
/// </summary>
public sealed partial class ProcessesViewModel
{
    private int? _pendingSelectPid;

    /// <summary>Raised when a row was selected on request, so the page can scroll it into view.</summary>
    public event EventHandler<RunningProcessRow>? SelectionRequested;

    /// <summary>Selects the process with <paramref name="pid"/> (now, or as soon as the list has been read).</summary>
    public void SelectProcess(int pid)
    {
        _pendingSelectPid = pid;

        // Clearing the filters rebuilds the list, which selects the row when it is there.
        SearchText = null;
        ShowFlaggedOnly = false;
        TrySelectPendingProcess();
    }

    /// <summary>Called after every rebuild of the list.</summary>
    private void TrySelectPendingProcess()
    {
        if (_pendingSelectPid is not { } pid)
        {
            return;
        }

        var row = Rows.FirstOrDefault(r => !r.IsGroup && r.Process?.Pid == pid);
        if (row is null)
        {
            var groupRow = Rows.FirstOrDefault(r => r.IsGroup && r.Group?.Members.Any(m => m.Pid == pid) == true);
            if (groupRow?.Group is { } group && _expandedGroups.Add(group.Key))
            {
                // Opening the group rebuilds the list, which calls back here and finds the child row.
                RebuildRows();
                return;
            }

            row = groupRow;
        }

        if (row is null)
        {
            // Not in the snapshot yet: wait for the next read, unless the process is not running any more.
            if (_last.Processes.Count > 0)
            {
                _pendingSelectPid = null;
            }

            return;
        }

        _pendingSelectPid = null;
        SelectedRow = row;
        SelectionRequested?.Invoke(this, row);
    }
}
