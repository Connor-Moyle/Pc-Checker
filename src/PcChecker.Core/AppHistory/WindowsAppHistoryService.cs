using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.History;
using PcChecker.Core.Restore;
using PcChecker.Core.Settings;
using static PcChecker.Core.AppHistory.AppHistoryText;

namespace PcChecker.Core.AppHistory;

/// <summary>
/// App history on Windows. <see cref="LoadAsync"/> reads Windows' own records (SRUM, see the Srum part) and falls back
/// to PC Checker's own recording (see the Recorder part); deleting removes only PC Checker's own files. Everything runs
/// off the UI thread; failures are logged and turned into plain reasons, never thrown at the page.
/// </summary>
public sealed partial class WindowsAppHistoryService : IAppHistoryService, IDisposable
{
    private const string Module = "App history";
    private const int MaxIdentityLookups = 600;

    private readonly ISettingsService _settings;
    private readonly IChangeGuard _guard;
    private readonly ILogger<WindowsAppHistoryService> _logger;
    private readonly AppUsageFileStore _store;
    private readonly ConcurrentDictionary<string, (string? Description, string? Company)> _fileInfo = new(StringComparer.OrdinalIgnoreCase);

    public WindowsAppHistoryService(ISettingsService settings, IChangeGuard guard, ILogger<WindowsAppHistoryService> logger)
    {
        _settings = settings;
        _guard = guard;
        _logger = logger;
        _store = new AppUsageFileStore(AppUsageFileStore.DefaultDirectory());
    }

    public string DataDirectory => _store.Directory;

    private HistorySettings HistoryOptions => _settings.Current.History ??= new HistorySettings();

    public async Task<AppHistoryResult> LoadAsync(AppUsageSourceChoice choice, bool forceReload, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        SrumFailure? failure = null;
        if (choice != AppUsageSourceChoice.OwnRecording)
        {
            var srum = await LoadWindowsRecordsAsync(forceReload, progress, cancellationToken).ConfigureAwait(false);
            if (srum.Result is { } windows)
            {
                return windows;
            }

            failure = srum.Failure;
            if (choice == AppUsageSourceChoice.WindowsRecords)
            {
                return new AppHistoryResult(AppUsageSource.WindowsRecords, [], new Dictionary<string, AppIdentity>(), DateTimeOffset.Now)
                {
                    WindowsRecordsFailure = failure,
                    OwnRecordingOn = HistoryOptions.Enabled,
                };
            }
        }

        progress?.Report(new ProgressUpdate(T("Reading PC Checker's own recording…")));
        return await Task.Run(() =>
        {
            FlushRecorder();
            var now = DateTimeOffset.Now;
            var records = _store.Read(DateTimeOffset.MinValue.AddDays(2), now.AddDays(1), cancellationToken);
            var apps = IdentitiesFor(records.Select(r => r.AppKey), cancellationToken);
            return new AppHistoryResult(AppUsageSource.OwnRecording, records, apps, now)
            {
                WindowsRecordsFailure = failure,
                OldestRecord = records.Count == 0 ? null : records.Min(r => r.Start),
                OwnRecordingOn = HistoryOptions.Enabled,
            };
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<OperationResult> DeleteOwnRecordingAsync(CancellationToken cancellationToken)
    {
        var options = new ChangeOptions(CreateBefore: false, CreateAfter: false, Module, _store.Directory, Reversible: false,
            UndoHint: "Deleted usage history cannot be brought back. PC Checker starts adding up app use again while it runs.");
        return _guard.RunGuardedAsync("Delete PC Checker's app usage recording", options, ct => Task.Run(() =>
        {
            try
            {
                DiscardPendingUsage();
                int files = _store.DeleteAll();
                return OperationResult.Ok(files == 0
                    ? "There was no usage history to delete."
                    : "PC Checker's usage history was deleted. Windows' own records are not affected.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "The app usage recording could not be deleted from {Folder}.", _store.Directory);
                return OperationResult.Fail("Some of the usage history could not be deleted because a file is in use. Try again in a moment.", ex);
            }
        }, ct), null, cancellationToken);
    }

    public OperationResult OpenFileLocation(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !File.Exists(path))
        {
            return OperationResult.Fail("The program file is not known or no longer exists.");
        }

        // Explorer only understands /select with the path glued on as one argument; the path is a fully qualified,
        // existing file and Windows file names cannot contain a quote, so it cannot break out of the quotes.
        return ShellLauncher.Open("explorer.exe", $"/select,\"{path}\"");
    }

    public void Dispose()
    {
        Stop();
        UnhookSettings();
    }

    /// <summary>Names and publishers for the apps, from their program files where those still exist.</summary>
    private Dictionary<string, AppIdentity> IdentitiesFor(IEnumerable<string> keys, CancellationToken ct)
    {
        var apps = new Dictionary<string, AppIdentity>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            bool isPath = Path.IsPathFullyQualified(key);
            apps[key] = Enrich(new AppIdentity(key, AppNames.ProgramName(key), isPath ? key : null, null), apps.Count < MaxIdentityLookups);
        }

        return apps;
    }

    /// <summary>Adds the file's description and company, when the file exists and can be read.</summary>
    private AppIdentity Enrich(AppIdentity identity, bool allowLookup)
    {
        if (identity.Path is not { } path || !allowLookup)
        {
            return identity;
        }

        var (description, company) = _fileInfo.GetOrAdd(path, static p =>
        {
            try
            {
                if (!File.Exists(p))
                {
                    return (null, null);
                }

                var info = FileVersionInfo.GetVersionInfo(p);
                return (Clean(info.FileDescription), Clean(info.CompanyName));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return (null, null);
            }
        });

        return identity with { Name = description ?? identity.Name, Publisher = company ?? identity.Publisher };
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
