namespace PcChecker.Core.Drivers.Sources;

/// <summary>
/// Merges the offers of every source into one list grouped by device: compares each offer with the installed
/// driver, marks the newest per device, orders the offers (newest first, then pages and tools) and picks the default
/// choice. Pure.
/// </summary>
public static class DriverOfferMerger
{
    /// <summary>Group key of the PC maker tools (they cover the whole PC, not one device).</summary>
    public const string SystemKey = "system";

    /// <summary>Merges provider offers into device groups: devices with something newer first, the PC maker group last.</summary>
    public static IReadOnlyList<DriverDeviceOffers> Merge(IEnumerable<DeviceOffer> offers)
    {
        ArgumentNullException.ThrowIfNull(offers);
        var groups = new List<DriverDeviceOffers>();
        foreach (var group in offers.GroupBy(o => o.Device.Key, StringComparer.OrdinalIgnoreCase))
        {
            var device = PickDevice(group.Select(g => g.Device));
            var classified = group
                .Select(g => g.Offer)
                .GroupBy(o => o.Id, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .Select(o => o with { Comparison = Classify(o, device), IsNewest = false })
                .ToList();
            groups.Add(Rank(device, classified));
        }

        return groups
            .OrderBy(g => g.Device.Key.Equals(SystemKey, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenByDescending(g => g.HasNewer)
            .ThenBy(g => g.Device.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// How an offer compares with the installed driver. Windows Update only offers drivers it ranks above the installed
    /// one, so a Windows Update offer whose version cannot be compared still counts as newer.
    /// </summary>
    public static DriverVersionComparison Classify(DriverOffer offer, DriverDeviceRef device)
    {
        ArgumentNullException.ThrowIfNull(offer);
        ArgumentNullException.ThrowIfNull(device);
        if (offer.Scheme == DriverVersionScheme.None)
        {
            return DriverVersionComparison.Unknown;
        }

        var c = DriverVersionMath.Classify(offer.Version, offer.Scheme, device);
        return c == DriverVersionComparison.Unknown && offer.Action == DriverOfferAction.WindowsUpdate ? DriverVersionComparison.Newer : c;
    }

    /// <summary>Orders one device's offers, marks the newest and picks the recommended one.</summary>
    public static DriverDeviceOffers Rank(DriverDeviceRef device, IReadOnlyList<DriverOffer> offers)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(offers);
        var versioned = offers.Where(o => o.Scheme != DriverVersionScheme.None && o.Version is not null).ToList();
        var others = offers.Except(versioned).OrderBy(o => ActionPriority(o.Action)).ThenBy(o => o.Title, StringComparer.CurrentCultureIgnoreCase).ToList();

        // Insertion sort with the cross-scheme comparison: newest first, ties by source priority.
        var sorted = new List<DriverOffer>();
        foreach (var offer in versioned.OrderBy(o => ActionPriority(o.Action)))
        {
            int index = sorted.FindIndex(s => (DriverVersionMath.CompareOffers(offer, s, device) ?? 0) > 0);
            sorted.Insert(index < 0 ? sorted.Count : index, offer);
        }

        if (sorted.Count > 0)
        {
            var top = sorted[0];
            for (int i = 0; i < sorted.Count; i++)
            {
                int? c = i == 0 ? 0 : DriverVersionMath.CompareOffers(sorted[i], top, device);
                if (c == 0)
                {
                    sorted[i] = sorted[i] with { IsNewest = true };
                }
            }
        }

        var all = sorted.Concat(others).ToList();
        return new DriverDeviceOffers(device, all) { Recommended = all.FirstOrDefault() };
    }

    /// <summary>
    /// The installed device a Windows Update driver is for: the connected device whose hardware ids include the
    /// update's hardware id (exact match first, then a device id that extends it). Null when none.
    /// </summary>
    public static DriverInfo? FindDevice(DriverUpdateInfo update, IReadOnlyList<DriverInfo> installed)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(installed);
        var id = update.DeviceId?.Trim();
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        var present = installed.Where(d => d.IsPresent).ToList();
        var exact = present.FirstOrDefault(d => d.HardwareIds.Any(h => string.Equals(h, id, StringComparison.OrdinalIgnoreCase)));
        if (exact is not null)
        {
            return exact;
        }

        // A shorter update id ("PCI\VEN_8086&DEV_7A4C") matches a longer device id only at a field boundary, so
        // DEV_7A4 never matches DEV_7A4C.
        return present.FirstOrDefault(d => d.HardwareIds.Append(d.DeviceId).Any(h => !string.IsNullOrEmpty(h)
            && (string.Equals(h, id, StringComparison.OrdinalIgnoreCase)
                || h.StartsWith(id + "&", StringComparison.OrdinalIgnoreCase)
                || h.StartsWith(id + "\\", StringComparison.OrdinalIgnoreCase))));
    }

    /// <summary>Default order of sources when versions tie: Windows Update, the vendor package, then pages and tools.</summary>
    public static int ActionPriority(DriverOfferAction action) => action switch
    {
        DriverOfferAction.WindowsUpdate => 0,
        DriverOfferAction.VendorPackage => 1,
        DriverOfferAction.OpenTool => 2,
        DriverOfferAction.InstallTool => 3,
        DriverOfferAction.OpenPage => 4,
        _ => 5,
    };

    /// <summary>The most informative device record among duplicates (one with a vendor version wins).</summary>
    private static DriverDeviceRef PickDevice(IEnumerable<DriverDeviceRef> devices)
    {
        var list = devices.ToList();
        var best = list.FirstOrDefault(d => d.InstalledVendorVersion is not null) ?? list[0];
        if (best.InstalledVersion is null && list.FirstOrDefault(d => d.InstalledVersion is not null) is { } withVersion)
        {
            best = best with { InstalledVersion = withVersion.InstalledVersion, InstalledDate = withVersion.InstalledDate };
        }

        return best;
    }
}
