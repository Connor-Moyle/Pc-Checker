using PcChecker.Core.Common;
using static PcChecker.Core.AppHistory.AppHistoryText;

namespace PcChecker.Core.AppHistory;

/// <summary>
/// Sample App history for demo mode and the screenshots: 30 days of Windows' records for the demo gaming desktop
/// (<see cref="DemoAppHistoryData"/>), and 12 days of PC Checker's own recording when that source is chosen. Nothing is
/// read from or written to the machine.
/// </summary>
public sealed class DemoAppHistoryService : IAppHistoryService
{
    private bool _ownDeleted;

    public bool IsRecording { get; private set; }

    public string DataDirectory => @"C:\Users\Alex\AppData\Local\PcChecker\history\apps";

    public void Start() => IsRecording = true;

    public void Stop() => IsRecording = false;

    public async Task<AppHistoryResult> LoadAsync(AppUsageSourceChoice choice, bool forceReload, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new ProgressUpdate(T("Copying Windows' app usage records…"), 20));
        await DemoData.DelayAsync(cancellationToken, 300, 700).ConfigureAwait(false);
        var now = DateTimeOffset.Now;
        if (choice == AppUsageSourceChoice.OwnRecording)
        {
            var own = _ownDeleted ? [] : DemoAppHistoryData.Generate(now, 12, withNetwork: false);
            return new AppHistoryResult(AppUsageSource.OwnRecording, own, DemoAppHistoryData.Identities, now)
            {
                OldestRecord = own.Count == 0 ? null : own.Min(r => r.Start),
                OwnRecordingOn = true,
            };
        }

        progress?.Report(new ProgressUpdate(T("Reading Windows' app usage records…"), 60));
        var records = DemoAppHistoryData.Generate(now, 30, withNetwork: true);
        return new AppHistoryResult(AppUsageSource.WindowsRecords, records, DemoAppHistoryData.Identities, now)
        {
            OldestRecord = records.Count == 0 ? null : records.Min(r => r.Start),
            OwnRecordingOn = true,
        };
    }

    public async Task<OperationResult> DeleteOwnRecordingAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        _ownDeleted = true;
        return OperationResult.Ok("PC Checker's usage history was deleted (demo). Windows' own records are not affected.");
    }

    public OperationResult OpenFileLocation(string path) =>
        OperationResult.Fail("Demo mode: no files are opened.");
}
