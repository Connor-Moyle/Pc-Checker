using PcChecker.Core.Drivers;

namespace PcChecker.Core.Storage;

/// <summary>Maps a drive model to the vendor's own diagnostic tool.</summary>
public static class StorageVendorRules
{
    private static readonly (string[] Keywords, string Vendor, string Tool, string Url)[] Table =
    [
        (["samsung"], "Samsung", "Samsung Magician", "https://semiconductor.samsung.com/consumer-storage/magician/"),
        (["wdc", "wd ", "western digital", "sandisk", "wd_black", "wd blue", "wd red"], "Western Digital", "Western Digital Dashboard", "https://support-en.wd.com/app/products/downloads/softwaredownloads"),
        (["seagate", "st1", "st2", "st3", "st4", "st6", "st8", "barracuda", "ironwolf", "firecuda"], "Seagate", "SeaTools", "https://www.seagate.com/support/downloads/seatools/"),
        (["crucial", "micron", "ct500", "ct1000", "ct2000"], "Crucial", "Crucial Storage Executive", "https://www.crucial.com/support/storage-executive"),
        (["kingston"], "Kingston", "Kingston SSD Manager", "https://www.kingston.com/en/support/technical/ssdmanager"),
        (["intel", "solidigm"], "Solidigm", "Solidigm Storage Tool", "https://www.solidigm.com/support-page/drivers-downloads.html"),
        (["sk hynix", "hynix", "skhynix"], "SK hynix", "Drive Manager Easy Kit", "https://ssd.skhynix.com/support/"),
        (["toshiba", "kioxia"], "KIOXIA", "KIOXIA SSD Utility", "https://europe.kioxia.com/en-europe/personal/software/ssd-utility.html"),
        (["corsair"], "Corsair", "Corsair SSD Toolbox", "https://www.corsair.com/us/en/s/downloads"),
        (["adata", "xpg"], "ADATA", "ADATA SSD ToolBox", "https://www.adata.com/us/support/consumer?tab=downloads"),
    ];

    /// <summary>Returns the vendor tool for a drive model, or null when the vendor is unknown.</summary>
    public static VendorLink? ToolFor(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        var m = model.ToLowerInvariant();
        foreach (var (keywords, vendor, tool, url) in Table)
        {
            if (keywords.Any(k => m.Contains(k, StringComparison.Ordinal)))
            {
                return new VendorLink(vendor, "Storage", url, $"{tool}: firmware updates, health and secure erase for {vendor} drives.");
            }
        }

        return null;
    }

    public static IReadOnlyList<VendorLink> ToolsFor(IEnumerable<StorageDevice> devices)
    {
        var links = new List<VendorLink>();
        foreach (var device in devices ?? [])
        {
            if (ToolFor(device.Model) is { } link && links.All(l => l.Vendor != link.Vendor))
            {
                links.Add(link);
            }
        }

        return links;
    }
}
