namespace PcChecker.Core.History;

/// <summary>
/// Long-term history of temperatures, loads, power, memory, free space and health scores. The recorder runs in the
/// background while the app is open (also when it sits in the tray) and keeps everything in local files; nothing
/// leaves the PC.
/// </summary>
public interface IHistoryService
{
    /// <summary>True while the background recorder is running.</summary>
    bool IsRecording { get; }

    /// <summary>The folder that holds the day files.</summary>
    string DataDirectory { get; }

    /// <summary>When the recorder last wrote a sample, or null.</summary>
    DateTimeOffset? LastRecorded { get; }

    /// <summary>Raised (on a background thread) after each sample is written.</summary>
    event EventHandler? Recorded;

    /// <summary>Starts recording when the settings allow it, and keeps following the settings. Safe to call more than once.</summary>
    void Start();

    /// <summary>Writes what was collected so far and stops recording.</summary>
    void Stop();

    /// <summary>Records the score of a finished health check (ignored when recording is off or the check is already recorded).</summary>
    void RecordHealthScore(DateTimeOffset captured, int score);

    /// <summary>Loads the recorded samples between two moments.</summary>
    Task<HistoryData> LoadAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);

    /// <summary>How much history is stored.</summary>
    Task<HistoryStorageInfo> GetStorageInfoAsync(CancellationToken cancellationToken);
}
