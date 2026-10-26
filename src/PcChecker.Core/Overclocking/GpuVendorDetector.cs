using System.Text.RegularExpressions;
using PcChecker.Core.Hardware;

namespace PcChecker.Core.Overclocking;

/// <summary>Pure helpers that work out the GPU vendor from PNP ids and names.</summary>
public static partial class GpuVendorDetector
{
    public const string NvidiaVendorId = "10DE";
    public const string AmdVendorId = "1002";
    public const string IntelVendorId = "8086";

    [GeneratedRegex(@"VEN_([0-9A-Fa-f]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex VendorIdPattern();

    /// <summary>Vendor from a PNP id such as <c>PCI\VEN_10DE&amp;DEV_2786</c>.</summary>
    public static GpuVendor FromPnpId(string? pnpDeviceId)
    {
        if (string.IsNullOrWhiteSpace(pnpDeviceId))
        {
            return GpuVendor.Unknown;
        }

        var m = VendorIdPattern().Match(pnpDeviceId);
        if (!m.Success)
        {
            return GpuVendor.Unknown;
        }

        return m.Groups[1].Value.ToUpperInvariant() switch
        {
            NvidiaVendorId => GpuVendor.Nvidia,
            AmdVendorId => GpuVendor.Amd,
            IntelVendorId => GpuVendor.Intel,
            _ => GpuVendor.Unknown,
        };
    }

    /// <summary>Vendor from a marketing name or manufacturer string.</summary>
    public static GpuVendor FromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return GpuVendor.Unknown;
        }

        var n = name.ToLowerInvariant();
        if (n.Contains("nvidia") || n.Contains("geforce") || n.Contains("quadro") || n.Contains("rtx") || n.Contains("gtx"))
        {
            return GpuVendor.Nvidia;
        }

        if (n.Contains("amd") || n.Contains("radeon") || n.Contains("ati "))
        {
            return GpuVendor.Amd;
        }

        if (n.Contains("intel") || n.Contains("arc ") || n.Contains("iris") || n.Contains("uhd graphics"))
        {
            return GpuVendor.Intel;
        }

        return GpuVendor.Unknown;
    }

    /// <summary>Best guess from everything the inventory knows about a card.</summary>
    public static GpuVendor Detect(GpuInfo gpu)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        var byId = FromPnpId(gpu.PnpDeviceId);
        if (byId != GpuVendor.Unknown)
        {
            return byId;
        }

        var byVendorField = FromName(gpu.Vendor) is var v && v != GpuVendor.Unknown ? v : FromName(gpu.Manufacturer);
        return byVendorField != GpuVendor.Unknown ? byVendorField : FromName(gpu.Name);
    }

    /// <summary>True for integrated graphics (Intel UHD/Iris, AMD Radeon Graphics on APUs) that cannot be tuned here.</summary>
    public static bool LooksIntegrated(GpuInfo gpu)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        if (gpu.IsIntegrated)
        {
            return true;
        }

        var n = (gpu.Name ?? string.Empty).ToLowerInvariant();
        if (n.Contains("uhd graphics") || n.Contains("iris") || n.Contains("hd graphics") || n.EndsWith("radeon graphics") || n.Contains("radeon(tm) graphics") || n.Contains("vega 8") || n.Contains("vega 7"))
        {
            return true;
        }

        return gpu.AdapterRamBytes is { } ram && ram > 0 && ram < 1024L * 1024 * 1024 && !n.Contains("arc");
    }

    /// <summary>Picks the card to tune: the first discrete GPU, otherwise the first card at all.</summary>
    public static GpuInfo? PickPrimary(IEnumerable<GpuInfo> gpus)
    {
        ArgumentNullException.ThrowIfNull(gpus);
        var list = gpus.ToList();
        return list.FirstOrDefault(g => !LooksIntegrated(g)) ?? list.FirstOrDefault();
    }

    public static string DisplayName(GpuVendor vendor) => vendor switch
    {
        GpuVendor.Nvidia => "NVIDIA",
        GpuVendor.Amd => "AMD",
        GpuVendor.Intel => "Intel",
        _ => "Unknown",
    };
}
