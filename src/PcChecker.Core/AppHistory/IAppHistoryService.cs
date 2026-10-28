using PcChecker.Core.Common;

namespace PcChecker.Core.AppHistory;

/// <summary>
/// Task Manager's App history: how much processor time, network data and disk traffic each app used over the last
/// days. Reads Windows' own records (the System Resource Usage Monitor database) when it can, and otherwise shows
/// PC Checker's own recording, which a light background recorder adds to once a minute while PC Checker runs.
/// Nothing leaves the PC.
/// </summary>
public interface IAppHistoryService
{
    /// <summary>True while PC Checker's own recorder is running (it follows the History recording setting).</summary>
    bool IsRecording { get; }

    /// <summary>The folder that holds PC Checker's own recording.</summary>
    string DataDirectory { get; }

    /// <summary>Starts the background recorder when the History setting allows it, and keeps following the setting.</summary>
    void Start();

    /// <summary>Writes what was added up so far and stops the recorder.</summary>
    void Stop();

    /// <summary>
    /// Loads every record of the chosen source (all days it keeps; the page filters by date). Reading Windows' records
    /// copies their database first, so it can take a few seconds; the copy is cached for a few minutes unless
    /// <paramref name="forceReload"/> is set. Never throws for a missing or locked database: the result says why and
    /// falls back to PC Checker's own recording when the choice is Automatic.
    /// </summary>
    Task<AppHistoryResult> LoadAsync(AppUsageSourceChoice choice, bool forceReload, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes PC Checker's own recording (all days). Windows' records are not touched: only Windows can clear them.
    /// Journaled through the change guard.
    /// </summary>
    Task<OperationResult> DeleteOwnRecordingAsync(CancellationToken cancellationToken);

    /// <summary>Opens File Explorer with the program file selected.</summary>
    OperationResult OpenFileLocation(string path);
}
