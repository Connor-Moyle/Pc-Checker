using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Drivers;

namespace PcChecker.Core.WindowsUpdate;

/// <summary>
/// Hiding and showing updates: <c>IUpdate.IsHidden</c>, the flag Microsoft's "Show or hide updates" troubleshooter
/// sets. A hidden update is neither offered nor installed until it is shown again.
/// </summary>
internal sealed partial class WindowsUpdateClient
{
    /// <summary>
    /// Lists the hidden updates. The local Windows Update data is read first (fast, no download); when that fails the
    /// update service is asked.
    /// </summary>
    public Task<(IReadOnlyList<HiddenUpdate> Updates, IReadOnlyList<string> Warnings)> ListHiddenAsync(bool useConfiguredServer, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            var warnings = new List<string>();
            var list = new List<HiddenUpdate>();
            try
            {
                dynamic session = WuaCom.CreateComObject("Microsoft.Update.Session");
                WuaCom.TrySet(() => session.ClientApplicationID = WuaCom.ClientId);
                dynamic result = SearchWithFallback((object)session, HiddenUpdateRules.HiddenCriteria, useConfiguredServer, cancellationToken);
                int count = WuaCom.TryGet(() => (int)result.Updates.Count, 0);
                for (int i = 0; i < count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (MapHidden(result.Updates.Item(i)) is { } hidden)
                    {
                        list.Add(hidden);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (COMException ex)
            {
                _logger.LogWarning(ex, "Hidden updates could not be listed.");
                warnings.Add("The hidden updates could not be read: " + WuaResultCodes.Explain(ex.HResult));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Hidden updates could not be listed.");
                warnings.Add("The hidden updates could not be read: " + ex.Message);
            }

            return ((IReadOnlyList<HiddenUpdate>)list.OrderBy(u => u.Title, StringComparer.CurrentCultureIgnoreCase).ToList(), (IReadOnlyList<string>)warnings);
        }, cancellationToken);
    }

    /// <summary>
    /// Hides (<paramref name="hide"/> true) or shows an update. It is found by its Windows Update id, in the last check's
    /// results or the local data first, then by asking the update service; a removed update whose id is unknown is
    /// found by its KB among the updates that are not installed.
    /// </summary>
    public Task<OperationResult> SetHiddenAsync(UpdateHideTarget target, bool hide, bool useConfiguredServer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        return Task.Run(() =>
        {
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            {
                return OperationResult.Fail("Windows Update is still busy with an earlier request. Try again in a minute.");
            }

            try
            {
                var com = FindForHiding(target, useConfiguredServer, cancellationToken);
                if (com is null)
                {
                    return OperationResult.Fail(hide
                        ? $"Windows Update does not offer \"{target.Title}\" right now, so there is nothing to hide yet. After a restart, check for updates and hide it when it appears."
                        : $"Windows Update no longer lists \"{target.Title}\", so there is nothing to show again.");
                }

                dynamic update = com;
                bool isHidden = WuaCom.TryGet(() => (bool)update.IsHidden, false);
                bool mandatory = WuaCom.TryGet(() => (bool)update.IsMandatory, false);
                var decision = HiddenUpdateRules.Decide(hide, isHidden, mandatory);
                var message = HiddenUpdateRules.Message(decision, hide, target.Title);
                switch (decision)
                {
                    case HideDecision.AlreadyDone:
                        return OperationResult.Ok(message);
                    case HideDecision.Mandatory:
                        return OperationResult.Fail(message);
                }

                update.IsHidden = hide;
                bool now = WuaCom.TryGet(() => (bool)update.IsHidden, !hide);
                return now == hide
                    ? OperationResult.Ok(message)
                    : OperationResult.Fail($"Windows Update did not keep the change to \"{target.Title}\".");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (COMException ex)
            {
                _logger.LogWarning(ex, "Changing IsHidden for {Update} failed.", target.Title);
                return OperationResult.Fail("Windows Update refused the change: " + WuaResultCodes.Explain(ex.HResult), ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                return OperationResult.Fail("PC Checker needs to run as an administrator to hide or show updates.", ex);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Changing IsHidden for {Update} failed.", target.Title);
                return OperationResult.Fail("Windows Update refused the change: " + ex.Message, ex);
            }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
            }
        }, cancellationToken);
    }

    private object? FindForHiding(UpdateHideTarget target, bool useConfiguredServer, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(target.UpdateId))
        {
            lock (_gate)
            {
                if (_comUpdates.TryGetValue(target.UpdateId, out var cached))
                {
                    return cached;
                }
            }
        }

        dynamic session = WuaCom.CreateComObject("Microsoft.Update.Session");
        WuaCom.TrySet(() => session.ClientApplicationID = WuaCom.ClientId);
        if (HiddenUpdateRules.CriteriaForId(target.UpdateId) is { } byId)
        {
            dynamic found = SearchWithFallback((object)session, byId, useConfiguredServer, cancellationToken, requireResults: true);
            if (WuaCom.TryGet(() => (int)found.Updates.Count, 0) > 0)
            {
                return found.Updates.Item(0);
            }
        }

        if (InstalledUpdateRules.NormalizeKb(target.Kb) is null)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        dynamic searcher = CreateSearcher((object)session, useConfiguredServer, online: true);
        dynamic result = searcher.Search(HiddenUpdateRules.NotInstalledCriteria);
        int count = WuaCom.TryGet(() => (int)result.Updates.Count, 0);
        for (int i = 0; i < count; i++)
        {
            object candidate = result.Updates.Item(i);
            dynamic u = candidate;
            if (HiddenUpdateRules.MatchesKb(target.Kb, ReadKbs(candidate), WuaCom.TryGet(() => (string?)u.Title, null)))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Searches the local data first and the update service when that fails (or, with <paramref name="requireResults"/>, finds nothing).</summary>
    private static object SearchWithFallback(object sessionObject, string criteria, bool useConfiguredServer, CancellationToken cancellationToken, bool requireResults = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            dynamic offline = CreateSearcher(sessionObject, useConfiguredServer, online: false);
            dynamic result = offline.Search(criteria);
            if (!requireResults || WuaCom.TryGet(() => (int)result.Updates.Count, 0) > 0)
            {
                return result;
            }
        }
        catch (COMException) when (!cancellationToken.IsCancellationRequested)
        {
            // No local data yet: ask the service below.
        }

        cancellationToken.ThrowIfCancellationRequested();
        dynamic online = CreateSearcher(sessionObject, useConfiguredServer, online: true);
        return online.Search(criteria);
    }

    private static object CreateSearcher(object sessionObject, bool useConfiguredServer, bool online)
    {
        dynamic session = sessionObject;
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

        WuaCom.TrySet(() => searcher.Online = online);
        WuaCom.TrySet(() => searcher.IncludePotentiallySupersededUpdates = true);
        return searcher;
    }

    private static List<string> ReadKbs(object com)
    {
        var kbs = new List<string>();
        try
        {
            dynamic update = com;
            dynamic ids = update.KBArticleIDs;
            int n = (int)ids.Count;
            for (int i = 0; i < n; i++)
            {
                kbs.Add((string)ids.Item(i));
            }
        }
        catch
        {
            // Some updates (definitions, drivers) have no KB article.
        }

        return kbs;
    }

    private static HiddenUpdate? MapHidden(object com)
    {
        dynamic update = com;
        string id = WuaCom.TryGet(() => (string)update.Identity.UpdateID, string.Empty);
        string title = WuaCom.TryGet(() => (string)update.Title, string.Empty);
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        bool isDriver = WuaCom.TryGet(() => (int)update.Type, 1) == UpdateTypeDriver;
        title = string.IsNullOrWhiteSpace(title) ? "Windows update" : title.Trim();
        return new HiddenUpdate(id, title, UpdateClassifier.ExtractKb(title, ReadKbs(com)), UpdateClassifier.Classify(ReadCategoryIds(com), title, isDriver));
    }
}
