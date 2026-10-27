using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;

namespace PcChecker.Core.Drivers;

/// <summary>
/// Thin wrapper over the Windows Update Agent COM API (<c>Microsoft.Update.Session</c>) using late binding, so the
/// project builds without interop assemblies. Searches the public Windows Update catalog for driver updates and can
/// download/install them. Every call runs on a thread-pool (MTA) thread and degrades to warnings on COM failures.
/// </summary>
internal sealed class WindowsUpdateAgent
{
    private readonly ILogger _logger;
    private readonly Dictionary<string, object> _comUpdates = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private int _searchesInFlight;

    public WindowsUpdateAgent(ILogger logger)
    {
        _logger = logger;
    }

    public static bool IsSupported => PlatformGuard.IsWindows;

    /// <summary>
    /// Searches for driver updates that are not installed yet. The blocking <c>Search()</c> call cannot be aborted, so
    /// cancellation takes effect between steps; while one search is running a second request is refused instead of
    /// racing it for the cached update objects.
    /// </summary>
    public Task<(IReadOnlyList<DriverUpdateInfo> Updates, IReadOnlyList<string> Warnings)> SearchAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            var warnings = new List<string>();
            var list = new List<DriverUpdateInfo>();
            if (!IsSupported)
            {
                warnings.Add("Windows Update is only available on Windows.");
                return ((IReadOnlyList<DriverUpdateInfo>)list, (IReadOnlyList<string>)warnings);
            }

            if (Interlocked.CompareExchange(ref _searchesInFlight, 1, 0) != 0)
            {
                warnings.Add("A Windows Update search is still running from an earlier request. Wait a minute and check again.");
                return ((IReadOnlyList<DriverUpdateInfo>)list, (IReadOnlyList<string>)warnings);
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new ProgressUpdate("Connecting to Windows Update", 10));
                dynamic session = WuaCom.CreateComObject("Microsoft.Update.Session");
                WuaCom.TrySet(() => session.ClientApplicationID = WuaCom.ClientId);
                dynamic searcher = session.CreateUpdateSearcher();
                WuaCom.TrySet(() => searcher.ServerSelection = WuaCom.ServerSelectionOthers);
                WuaCom.TrySet(() => searcher.ServiceID = WuaResultCodes.WindowsUpdateServiceId);
                WuaCom.TrySet(() => searcher.Online = true);
                WuaCom.TrySet(() => searcher.IncludePotentiallySupersededUpdates = false);

                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new ProgressUpdate("Searching the Windows Update driver catalog", 30, "This can take a minute. Cancel takes effect once Windows Update answers."));
                dynamic result;
                try
                {
                    result = searcher.Search("IsInstalled=0 and Type='Driver' and IsHidden=0");
                }
                catch (COMException ex) when (ex.HResult == WuaResultCodes.InvalidCriteria)
                {
                    // Older agents may not accept the IsHidden clause; any other failure is a real one and is reported.
                    _logger.LogWarning(ex, "Driver search with IsHidden filter was rejected; retrying without it.");
                    result = searcher.Search("IsInstalled=0 and Type='Driver'");
                }

                cancellationToken.ThrowIfCancellationRequested();
                int count = WuaCom.TryGet(() => (int)result.Updates.Count, 0);
                progress?.Report(new ProgressUpdate("Reading results", 80, $"{count} driver update(s) found"));
                lock (_gate)
                {
                    _comUpdates.Clear();
                    for (int i = 0; i < count; i++)
                    {
                        dynamic update = result.Updates.Item(i);
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
                warnings.Add("Windows Update service unavailable (" + WuaResultCodes.DescribeHResult(ex.HResult) + ").");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Windows Update search failed.");
                warnings.Add("Windows Update could not be queried: " + ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _searchesInFlight, 0);
            }

            return ((IReadOnlyList<DriverUpdateInfo>)list, (IReadOnlyList<string>)warnings);
        }, cancellationToken);
    }

    /// <summary>Asks the cached update objects which of the given updates are downloaded; unknown ids are left out.</summary>
    public Task<IReadOnlySet<string>> GetDownloadedIdsAsync(IEnumerable<string> updateIds, CancellationToken cancellationToken)
    {
        var ids = updateIds.ToList();
        return Task.Run(() =>
        {
            var downloaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!IsSupported)
            {
                return (IReadOnlySet<string>)downloaded;
            }

            foreach (var id in ids)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Has(id))
                {
                    continue;
                }

                dynamic update = Get(id);
                if (WuaCom.TryGet(() => (bool)update.IsDownloaded, false))
                {
                    downloaded.Add(id);
                }
            }

            return (IReadOnlySet<string>)downloaded;
        }, cancellationToken);
    }

    /// <summary>Downloads the given updates one at a time so progress can be reported per update.</summary>
    public Task<OperationResult> DownloadAsync(IReadOnlyList<DriverUpdateInfo> updates, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            if (!IsSupported)
            {
                return OperationResult.Fail("Windows Update is only available on Windows.");
            }

            var missing = updates.Where(u => !Has(u.Id)).Select(u => u.Title).ToList();
            if (missing.Count > 0)
            {
                return OperationResult.Fail("Check for updates again before downloading: " + string.Join(", ", missing));
            }

            if (updates.Count == 0)
            {
                return OperationResult.Fail("No updates were selected.");
            }

            try
            {
                dynamic session = WuaCom.CreateComObject("Microsoft.Update.Session");
                WuaCom.TrySet(() => session.ClientApplicationID = WuaCom.ClientId);
                int ok = 0;
                var failures = new List<string>();
                for (int i = 0; i < updates.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var u = updates[i];
                    progress?.Report(new ProgressUpdate("Downloading", 100.0 * i / updates.Count, u.Title));
                    dynamic coll = WuaCom.CreateComObject("Microsoft.Update.UpdateColl");
                    coll.Add(Get(u.Id));
                    dynamic downloader = session.CreateUpdateDownloader();
                    downloader.Updates = coll;
                    dynamic result = downloader.Download();
                    int code = WuaCom.TryGet(() => (int)result.ResultCode, WuaResultCodes.Failed);
                    int hr = WuaCom.TryGet(() => (int)result.HResult, 0);
                    if (WuaResultCodes.IsSuccess(code))
                    {
                        ok++;
                    }
                    else
                    {
                        failures.Add($"{u.Title}: {WuaResultCodes.Summarize(code, hr)}");
                    }
                }

                progress?.Report(new ProgressUpdate("Download finished", 100));
                if (failures.Count == 0)
                {
                    return OperationResult.Ok($"{ok} update(s) downloaded.");
                }

                var message = $"{ok} of {updates.Count} update(s) downloaded. " + string.Join(" ", failures);
                return ok > 0 ? OperationResult.Ok(message) : OperationResult.Fail(message);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (COMException ex)
            {
                _logger.LogWarning(ex, "Driver download failed.");
                return OperationResult.Fail("Download failed: " + WuaResultCodes.DescribeHResult(ex.HResult), ex);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Driver download failed.");
                return OperationResult.Fail("Download failed: " + ex.Message, ex);
            }
        }, cancellationToken);
    }

    /// <summary>Installs the given (downloaded) updates quietly and maps each one's result.</summary>
    public Task<DriverInstallResult> InstallAsync(IReadOnlyList<DriverUpdateInfo> updates, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            if (!IsSupported)
            {
                return Failed(updates, "Windows Update is only available on Windows.");
            }

            var missing = updates.Where(u => !Has(u.Id)).Select(u => u.Title).ToList();
            if (missing.Count > 0)
            {
                return Failed(updates, "Check for updates again before installing: " + string.Join(", ", missing));
            }

            if (updates.Count == 0)
            {
                return new DriverInstallResult(false, false, []);
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                dynamic session = WuaCom.CreateComObject("Microsoft.Update.Session");
                WuaCom.TrySet(() => session.ClientApplicationID = WuaCom.ClientId);
                dynamic coll = WuaCom.CreateComObject("Microsoft.Update.UpdateColl");
                foreach (var u in updates)
                {
                    coll.Add(Get(u.Id));
                }

                progress?.Report(new ProgressUpdate("Installing driver updates", 20, $"{updates.Count} update(s). Windows may flicker while a display driver installs."));
                dynamic installer = session.CreateUpdateInstaller();
                installer.Updates = coll;
                WuaCom.TrySet(() => installer.ForceQuiet = true);
                WuaCom.TrySet(() => installer.AllowSourcePrompts = false);
                dynamic result = installer.Install();
                int overall = WuaCom.TryGet(() => (int)result.ResultCode, WuaResultCodes.Failed);
                bool reboot = WuaCom.TryGet(() => (bool)result.RebootRequired, false);

                var results = new List<(DriverUpdateInfo, OperationResult)>();
                for (int i = 0; i < updates.Count; i++)
                {
                    int code = overall;
                    int hr = WuaCom.TryGet(() => (int)result.HResult, 0);
                    bool itemReboot = reboot;
                    try
                    {
                        dynamic item = result.GetUpdateResult(i);
                        code = WuaCom.TryGet(() => (int)item.ResultCode, code);
                        hr = WuaCom.TryGet(() => (int)item.HResult, hr);
                        itemReboot = WuaCom.TryGet(() => (bool)item.RebootRequired, itemReboot);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Per-update install result unavailable for index {Index}.", i);
                    }

                    var text = WuaResultCodes.Summarize(code, hr);
                    results.Add((updates[i], WuaResultCodes.IsSuccess(code) ? OperationResult.Ok(text, itemReboot) : OperationResult.Fail(text)));
                }

                progress?.Report(new ProgressUpdate("Install finished", 100));
                bool anyOk = results.Any(r => r.Item2.Success);
                return new DriverInstallResult(anyOk && WuaResultCodes.IsSuccess(overall), reboot, results);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (COMException ex)
            {
                _logger.LogWarning(ex, "Driver install failed.");
                return Failed(updates, "Install failed: " + WuaResultCodes.DescribeHResult(ex.HResult));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Driver install failed.");
                return Failed(updates, "Install failed: " + ex.Message);
            }
        }, cancellationToken);
    }

    private static DriverInstallResult Failed(IReadOnlyList<DriverUpdateInfo> updates, string message) =>
        new(false, false, updates.Select(u => (u, OperationResult.Fail(message))).ToList());

    private bool Has(string id)
    {
        lock (_gate)
        {
            return _comUpdates.ContainsKey(id);
        }
    }

    private object Get(string id)
    {
        lock (_gate)
        {
            return _comUpdates[id];
        }
    }

    private DriverUpdateInfo? Map(dynamic update)
    {
        string id = WuaCom.TryGet(() => (string)update.Identity.UpdateID, string.Empty);
        string title = WuaCom.TryGet(() => (string)update.Title, string.Empty);
        if (string.IsNullOrWhiteSpace(id))
        {
            id = string.IsNullOrWhiteSpace(title) ? Guid.NewGuid().ToString() : title;
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            title = "Driver update";
        }

        string? description = WuaCom.TryGet(() => (string?)update.Description, null);
        string? model = WuaCom.TryGet(() => (string?)update.DriverModel, null);
        string? provider = WuaCom.TryGet(() => (string?)update.DriverProvider, null);
        string? driverClass = WuaCom.TryGet(() => (string?)update.DriverClass, null);
        string? hardwareId = WuaCom.TryGet(() => (string?)update.DriverHardwareID, null);
        DateTime? date = WuaCom.TryGet(() => (DateTime?)update.DriverVerDate, null) ?? WuaCom.TryGet(() => (DateTime?)update.LastDeploymentChangeTime, null);
        long size = WuaCom.TryGet(() => Convert.ToInt64((object)update.MaxDownloadSize), 0L);
        bool downloaded = WuaCom.TryGet(() => (bool)update.IsDownloaded, false);
        bool installed = WuaCom.TryGet(() => (bool)update.IsInstalled, false);
        bool reboot = WuaCom.TryGet(() => (bool)update.RebootRequired, false);
        string? kb = null;
        try
        {
            dynamic kbs = update.KBArticleIDs;
            int n = (int)kbs.Count;
            if (n > 0)
            {
                kb = "KB" + (string)kbs.Item(0);
            }
        }
        catch
        {
            // No KB article for most drivers.
        }

        string? version = ExtractVersion(title);
        var detail = string.Join(" · ", new[] { model, provider, driverClass }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var fullDescription = string.IsNullOrWhiteSpace(detail) ? description : (string.IsNullOrWhiteSpace(description) ? detail : detail + "\n" + description);

        return new DriverUpdateInfo(id, title, fullDescription, version, date, size, DriverUpdateSource.WindowsUpdate,
            hardwareId, kb, null, downloaded, installed, reboot)
        {
            Provider = string.IsNullOrWhiteSpace(provider) ? null : provider.Trim(),
        };
    }

    /// <summary>Windows Update driver titles look like "Intel Corporation - System - 30.100.2417.47"; the last part is the version.</summary>
    internal static string? ExtractVersion(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var last = title.Split(" - ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        return last is not null && VersionText.TryParse(last, out _) && last.Any(char.IsDigit) && last.Contains('.') ? last : null;
    }
}
