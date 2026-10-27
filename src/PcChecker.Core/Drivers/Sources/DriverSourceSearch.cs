using PcChecker.Core.Common;
using PcChecker.Core.Hardware;

namespace PcChecker.Core.Drivers.Sources;

/// <summary>
/// Runs the enabled providers side by side and merges what they found. A provider that fails becomes a status line
/// and a warning; the others still show. Shared by the Windows and demo services.
/// </summary>
public static class DriverSourceSearch
{
    public static async Task<DriverSourcesResult> RunAsync(IReadOnlyList<IDriverUpdateProvider> providers, DriverSourceContext context,
        IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(context);
        var statuses = new List<DriverSourceStatus>();
        var enabled = new List<IDriverUpdateProvider>();
        foreach (var provider in providers)
        {
            if (context.Settings.IsEnabled(provider.Source))
            {
                enabled.Add(provider);
            }
            else
            {
                statuses.Add(new DriverSourceStatus(provider.Source, provider.Name, false, true, "Off in options"));
            }
        }

        if (enabled.Count == 0)
        {
            return new DriverSourcesResult([], [], statuses, ["Every driver source is switched off. Turn one on under Update sources."], DateTimeOffset.Now);
        }

        progress?.Report(new ProgressUpdate("Searching " + string.Join(", ", enabled.Select(p => p.Name)), 5));
        // Each provider starts on the thread pool: some read the registry or WMI before their first await.
        var tasks = enabled.Select(p => Task.Run(() => RunOneAsync(p, context, progress, cancellationToken), cancellationToken)).ToList();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        var offers = new List<DeviceOffer>();
        var warnings = new List<string>();
        var links = new List<VendorLink>();
        for (int i = 0; i < enabled.Count; i++)
        {
            var (provider, result) = (enabled[i], results[i]);
            offers.AddRange(result.Offers);
            warnings.AddRange(result.Warnings);
            links.AddRange(result.VendorLinks);
            statuses.Add(new DriverSourceStatus(provider.Source, provider.Name, true, result.Succeeded, result.Status));
        }

        progress?.Report(new ProgressUpdate("Done", 100));
        var orderedStatuses = statuses.OrderBy(s => s.Source).ToList();
        var uniqueLinks = links.GroupBy(l => l.Vendor + "|" + l.Component, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        return new DriverSourcesResult(DriverOfferMerger.Merge(offers), uniqueLinks, orderedStatuses,
            warnings.Where(w => !string.IsNullOrWhiteSpace(w)).Distinct().ToList(), DateTimeOffset.Now);
    }

    /// <summary>The machine facts from a hardware scan; null fields when unknown.</summary>
    public static DriverSystemFacts FactsFrom(HardwareInventory? inventory)
    {
        if (inventory is null)
        {
            return DriverSystemFacts.Unknown;
        }

        var system = inventory.System;
        bool laptop = DriverDeviceMapper.IsLaptop(system?.PcSystemType, system?.ChassisType) || inventory.Batteries.Count > 0;
        return new DriverSystemFacts(system?.Manufacturer, system?.Model, inventory.Motherboard?.Manufacturer, inventory.Motherboard?.Product, laptop);
    }

    private static async Task<DriverProviderResult> RunOneAsync(IDriverUpdateProvider provider, DriverSourceContext context,
        IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        try
        {
            return await provider.FindAsync(context, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new DriverProviderResult([], [$"{provider.Name}: the search failed ({ex.Message})."], "Failed", false);
        }
    }
}
