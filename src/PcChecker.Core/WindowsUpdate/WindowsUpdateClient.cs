using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Drivers;

namespace PcChecker.Core.WindowsUpdate;

/// <summary>
/// The Windows Update Agent COM API (<c>Microsoft.Update.Session</c>) for software updates, late bound like the driver
/// wrapper in <c>Drivers/WindowsUpdateAgent</c> and sharing its helpers and result codes. Every call runs on a
/// thread-pool (MTA) thread and turns COM failures into warnings or per-update results.
/// </summary>
internal sealed class WindowsUpdateClient
{
    private const string Criteria = "IsInstalled=0 and Type='Software' and IsHidden=0";
    private const string CriteriaWithoutHidden = "IsInstalled=0 and Type='Software'";
    private const int UpdateTypeDriver = 2;

    private readonly ILogger _logger;
    private readonly Dictionary<string, object> _comUpdates = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private int _busy;

    public WindowsUpdateClient(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// True while a search or install is running. A cancelled search keeps running until Windows Update answers, because
    /// the agent's search cannot be stopped.
    /// </summary>
    public bool IsBusy => Volatile.Read(ref _busy) != 0;

    /// <summary>Last successful automatic check and install, as Windows Update recorded them, and whether a restart is pending.</summary>
    public (DateTimeOffset? LastSearch, DateTimeOffset? LastInstall, bool? RebootRequired, List<string> Warnings) ReadAgentState()
    {
        var warnings = new List<string>();
        DateTimeOffset? lastSearch = null;
        DateTimeOffset? lastInstall = null;
        bool? reboot = null;
        try
        {
            dynamic au = WuaCom.CreateComObject("Microsoft.Update.AutoUpdate");
            dynamic results = au.Results;
            lastSearch = AsUtc(WuaCom.TryGet<object?>(() => (object?)results.LastSearchSuccessDate, null));
            lastInstall = AsUtc(WuaCom.TryGet<object?>(() => (object?)results.LastInstallationSuccessDate, null));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Windows Update automatic update results could not be read.");
            warnings.Add("The time of the last automatic check could not be read.");
        }

        try
        {
            dynamic info = WuaCom.CreateComObject("Microsoft.Update.SystemInfo");
            reboot = WuaCom.TryGet<bool?>(() => (bool)info.RebootRequired, null);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Microsoft.Update.SystemInfo is not available.");
        }

        return (lastSearch, lastInstall, reboot, warnings);
    }

    /// <summary>Searches for software updates. A second search while one runs is refused instead of racing it.</summary>
    public Task<(IReadOnlyList<PendingUpdate> Updates, IReadOnlyList<string> Warnings)> SearchAsync(bool useConfiguredServer, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            var warnings = new List<string>();
            var list = new List<PendingUpdate>();
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            {
                warnings.Add("Windows Update is still busy with an earlier request. Wait a minute and check again.");
                return ((IReadOnlyList<PendingUpdate>)list, (IReadOnlyList<string>)warnings);
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new ProgressUpdate("Connecting to Windows Update", 10));
                dynamic session = WuaCom.CreateComObject("Microsoft.Update.Session");
                WuaCom.TrySet(() => session.ClientApplicationID = WuaCom.ClientId);
                dynamic searcher = session.CreateUpdateSearcher();
                if (useConfiguredServer)
                {
                    WuaCom.TrySet(() => searcher.ServerSelection = WuaCom.ServerSelectionDefault);
                }
                else
                {
                    WuaCom.TrySet(() => searcher.ServerSelection = WuaCom.ServerSelectionOthers);
                    WuaCom.TrySet(() => searcher.ServiceID = WuaResultCodes.WindowsUpdateServiceId);
                }

                WuaCom.TrySet(() => searcher.Online = true);
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new ProgressUpdate("Searching for updates", 30, "This can take a minute. Cancel takes effect once Windows Update answers."));
                dynamic result;
                try
                {
                    result = searcher.Search(Criteria);
                }
                catch (COMException ex) when (ex.HResult == WuaResultCodes.InvalidCriteria)
                {
                    _logger.LogWarning(ex, "Update search with IsHidden filter was rejected; retrying without it.");
                    result = searcher.Search(CriteriaWithoutHidden);
                }

                cancellationToken.ThrowIfCancellationRequested();
                int count = WuaCom.TryGet(() => (int)result.Updates.Count, 0);
                progress?.Report(new ProgressUpdate("Reading results", 80, $"{count} update(s) found"));
                lock (_gate)
                {
                    _comUpdates.Clear();
                    for (int i = 0; i < count; i++)
                    {
                        object update = result.Updates.Item(i);
                        var info = Map(update);
                        if (info is null)
                        {
                            continue;
                        }

                        list.Add(info);
                        _comUpdates[info.Id] = update;
                    }
                }

                int warningCount = WuaCom.TryGet(() => (int)result.Warnings.Count, 0);
                for (int i = 0; i < warningCount; i++)
                {
                    var text = WuaCom.TryGet(() => (string?)result.Warnings.Item(i).Message, null);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        warnings.Add("Windows Update: " + text);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (COMException ex)
            {
                _logger.LogWarning(ex, "Windows Update search failed.");
                warnings.Add("Windows Update could not be checked: " + WuaResultCodes.Explain(ex.HResult));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Windows Update search failed.");
                warnings.Add("Windows Update could not be checked: " + ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
            }

            return ((IReadOnlyList<PendingUpdate>)list, (IReadOnlyList<string>)warnings);
        }, cancellationToken);
    }

    /// <summary>Reads up to <paramref name="maxRecords"/> raw history records, newest first as Windows keeps them.</summary>
    public Task<(IReadOnlyList<UpdateHistoryRecord> Records, IReadOnlyList<string> Warnings)> QueryHistoryAsync(int maxRecords, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            var warnings = new List<string>();
            var records = new List<UpdateHistoryRecord>();
            try
            {
                dynamic session = WuaCom.CreateComObject("Microsoft.Update.Session");
                WuaCom.TrySet(() => session.ClientApplicationID = WuaCom.ClientId);
                dynamic searcher = session.CreateUpdateSearcher();
                int total = WuaCom.TryGet(() => (int)searcher.GetTotalHistoryCount(), 0);
                int take = Math.Min(total, Math.Max(0, maxRecords));
                if (take == 0)
                {
                    return ((IReadOnlyList<UpdateHistoryRecord>)records, (IReadOnlyList<string>)warnings);
                }

                dynamic history = searcher.QueryHistory(0, take);
                int count = WuaCom.TryGet(() => (int)history.Count, 0);
                int unreadable = 0;
                for (int i = 0; i < count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        object entry = history.Item(i);
                        records.Add(MapHistory(entry));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // A few history entries are known to throw when read; skip them rather than lose the list.
                        unreadable++;
                        _logger.LogDebug(ex, "Update history entry {Index} could not be read.", i);
                    }
                }

                if (unreadable > 0)
                {
                    warnings.Add($"{unreadable} history entr{(unreadable == 1 ? "y" : "ies")} could not be read and {(unreadable == 1 ? "is" : "are")} left out.");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (COMException ex)
            {
                _logger.LogWarning(ex, "Windows Update history could not be read.");
                warnings.Add("The update history could not be read: " + WuaResultCodes.Explain(ex.HResult));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Windows Update history could not be read.");
                warnings.Add("The update history could not be read: " + ex.Message);
            }

            return ((IReadOnlyList<UpdateHistoryRecord>)records, (IReadOnlyList<string>)warnings);
        }, cancellationToken);
    }

    /// <summary>
    /// Downloads and installs the updates one at a time so each gets its own progress and result. Cancelling stops
    /// before the next update; the ones not started are reported as skipped.
    /// </summary>
    public Task<UpdateInstallResult> InstallAsync(IReadOnlyList<PendingUpdate> updates, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            var items = new List<UpdateInstallItem>();
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            {
                return new UpdateInstallResult(updates.Select(u => Fail(u, "Windows Update is still busy with an earlier request. Try again in a minute.")).ToList(), false);
            }

            try
            {
                dynamic session = WuaCom.CreateComObject("Microsoft.Update.Session");
                WuaCom.TrySet(() => session.ClientApplicationID = WuaCom.ClientId);
                dynamic probe = session.CreateUpdateInstaller();
                if (WuaCom.TryGet(() => (bool)probe.RebootRequiredBeforeInstallation, false))
                {
                    const string waiting = "Not installed: Windows needs a restart to finish an earlier update first. Restart, then try again.";
                    return new UpdateInstallResult(updates.Select(u => Fail(u, waiting)).ToList(), true);
                }

                bool anyReboot = false;
                for (int i = 0; i < updates.Count; i++)
                {
                    var u = updates[i];
                    if (cancellationToken.IsCancellationRequested)
                    {
                        items.Add(Fail(u, "Skipped: the install was cancelled before this update started."));
                        continue;
                    }

                    var item = InstallOne((object)session, u, i, updates.Count, progress);
                    anyReboot |= item.RebootRequired;
                    items.Add(item);
                }

                progress?.Report(new ProgressUpdate("Install finished", 100));
                return new UpdateInstallResult(items, anyReboot);
            }
            catch (COMException ex)
            {
                _logger.LogWarning(ex, "Windows Update install failed.");
                var text = "Not installed: " + WuaResultCodes.Explain(ex.HResult);
                return new UpdateInstallResult(Complete(items, updates, text), items.Any(i => i.RebootRequired));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Windows Update install failed.");
                return new UpdateInstallResult(Complete(items, updates, "Not installed: " + ex.Message), items.Any(i => i.RebootRequired));
            }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
            }
        }, CancellationToken.None);
    }

    private UpdateInstallItem InstallOne(object sessionObject, PendingUpdate u, int index, int total, IProgress<ProgressUpdate>? progress)
    {
        dynamic session = sessionObject;
        object? com;
        lock (_gate)
        {
            com = _comUpdates.GetValueOrDefault(u.Id);
        }

        if (com is null)
        {
            return Fail(u, "Not installed: check for updates again first, the list is out of date.");
        }

        if (u.NeedsUserInput)
        {
            return Fail(u, "Not installed: this update asks questions while it installs. Install it from Windows Update in Settings.");
        }

        dynamic update = com;
        try
        {
            // The user confirmed the install, which includes Microsoft's licence terms, as Windows Update itself does.
            if (!WuaCom.TryGet(() => (bool)update.EulaAccepted, true))
            {
                update.AcceptEula();
            }

            dynamic coll = WuaCom.CreateComObject("Microsoft.Update.UpdateColl");
            coll.Add(com);
            string label = $"{index + 1} of {total}: {u.Title}";
            if (!WuaCom.TryGet(() => (bool)update.IsDownloaded, false))
            {
                progress?.Report(new ProgressUpdate("Downloading update " + label, 100.0 * index / total));
                dynamic downloader = session.CreateUpdateDownloader();
                downloader.Updates = coll;
                dynamic download = downloader.Download();
                int dCode = WuaCom.TryGet(() => (int)download.ResultCode, WuaResultCodes.Failed);
                int dHr = WuaCom.TryGet(() => (int)download.GetUpdateResult(0).HResult, WuaCom.TryGet(() => (int)download.HResult, 0));
                if (!WuaResultCodes.IsSuccess(dCode))
                {
                    return Fail(u, "Not downloaded: " + Reason(dCode, dHr));
                }
            }

            progress?.Report(new ProgressUpdate("Installing update " + label, 100.0 * (index + 0.5) / total, "Windows may be slow while an update installs."));
            dynamic installer = session.CreateUpdateInstaller();
            installer.Updates = coll;
            WuaCom.TrySet(() => installer.ForceQuiet = true);
            WuaCom.TrySet(() => installer.AllowSourcePrompts = false);
            dynamic result = installer.Install();
            int code = WuaCom.TryGet(() => (int)result.ResultCode, WuaResultCodes.Failed);
            int hr = WuaCom.TryGet(() => (int)result.HResult, 0);
            bool reboot = WuaCom.TryGet(() => (bool)result.RebootRequired, false);
            try
            {
                dynamic one = result.GetUpdateResult(0);
                code = WuaCom.TryGet(() => (int)one.ResultCode, code);
                hr = WuaCom.TryGet(() => (int)one.HResult, hr);
                reboot = WuaCom.TryGet(() => (bool)one.RebootRequired, reboot);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Per-update install result unavailable for {Update}.", u.Title);
            }

            if (!WuaResultCodes.IsSuccess(code))
            {
                return Fail(u, "Not installed: " + Reason(code, hr));
            }

            var text = code == WuaResultCodes.SucceededWithErrors ? "Installed with some errors." : "Installed.";
            return new UpdateInstallItem(u, true, reboot ? text + " Restart Windows to finish." : text, reboot);
        }
        catch (COMException ex)
        {
            _logger.LogWarning(ex, "Installing {Update} failed.", u.Title);
            return Fail(u, "Not installed: " + WuaResultCodes.Explain(ex.HResult));
        }
    }

    private static string Reason(int resultCode, int hresult) =>
        hresult != 0 ? WuaResultCodes.Explain(hresult) : WuaResultCodes.Describe(resultCode) + ".";

    private static UpdateInstallItem Fail(PendingUpdate update, string message) => new(update, false, message, false);

    private static List<UpdateInstallItem> Complete(List<UpdateInstallItem> done, IReadOnlyList<PendingUpdate> all, string message)
    {
        var list = new List<UpdateInstallItem>(done);
        list.AddRange(all.Skip(done.Count).Select(u => Fail(u, message)));
        return list;
    }

    private static PendingUpdate? Map(object com)
    {
        dynamic update = com;
        string id = WuaCom.TryGet(() => (string)update.Identity.UpdateID, string.Empty);
        string title = WuaCom.TryGet(() => (string)update.Title, string.Empty);
        if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            id = title;
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            title = "Windows update";
        }

        var categories = ReadCategoryIds(com);
        var kbs = new List<string>();
        try
        {
            dynamic ids = update.KBArticleIDs;
            int n = (int)ids.Count;
            for (int i = 0; i < n; i++)
            {
                kbs.Add((string)ids.Item(i));
            }
        }
        catch
        {
            // Some updates (definitions) have no KB article.
        }

        bool isDriver = WuaCom.TryGet(() => (int)update.Type, 1) == UpdateTypeDriver;
        int rebootBehavior = WuaCom.TryGet(() => (int)update.InstallationBehavior.RebootBehavior, 2);
        return new PendingUpdate(
            id,
            title.Trim(),
            UpdateClassifier.ExtractKb(title, kbs),
            WuaCom.TryGet(() => Convert.ToInt64((object)update.MaxDownloadSize), 0L),
            UpdateClassifier.Classify(categories, title, isDriver),
            UpdateClassifier.ParseSeverity(WuaCom.TryGet(() => (string?)update.MsrcSeverity, null)),
            UpdateClassifier.MapRebootBehavior(rebootBehavior),
            WuaCom.TryGet(() => (bool)update.IsDownloaded, false),
            WuaCom.TryGet(() => (string?)update.Description, null))
        {
            IsOptional = WuaCom.TryGet(() => (bool)update.BrowseOnly, false),
            NeedsUserInput = WuaCom.TryGet(() => (bool)update.InstallationBehavior.CanRequestUserInput, false),
            Released = WuaCom.TryGet(() => (DateTime?)update.LastDeploymentChangeTime, null),
            SupportUrl = WuaCom.TryGet(() => (string?)update.SupportUrl, null),
        };
    }

    private static UpdateHistoryRecord MapHistory(object com)
    {
        dynamic entry = com;
        return new UpdateHistoryRecord(
            WuaCom.TryGet(() => (string?)entry.Title, null),
            WuaCom.TryGet(() => (int)entry.Operation, 0),
            WuaCom.TryGet(() => (int)entry.ResultCode, 0),
            WuaCom.TryGet(() => (int)entry.HResult, 0),
            WuaCom.TryGet(() => (DateTime?)entry.Date, null),
            ReadCategoryIds(com),
            WuaCom.TryGet(() => (string?)entry.UpdateIdentity.UpdateID, null),
            WuaCom.TryGet(() => (string?)entry.Description, null));
    }

    private static List<string> ReadCategoryIds(object com)
    {
        var ids = new List<string>();
        try
        {
            dynamic item = com;
            dynamic categories = item.Categories;
            int n = (int)categories.Count;
            for (int i = 0; i < n; i++)
            {
                var id = WuaCom.TryGet(() => (string?)categories.Item(i).CategoryID, null);
                if (!string.IsNullOrWhiteSpace(id))
                {
                    ids.Add(id);
                }
            }
        }
        catch
        {
            // Older agents have no categories on history entries.
        }

        return ids;
    }

    /// <summary>WUA reports these dates in UTC; an empty VARIANT means "never".</summary>
    private static DateTimeOffset? AsUtc(object? value) =>
        value is DateTime date && date.Year > 1995 ? new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Utc)) : null;
}
