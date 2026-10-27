namespace PcChecker.Core.Overclocking.GpuLoad;

/// <summary>
/// Pure adapter choice: leaves out software renderers (Microsoft Basic Render Driver, WARP, remote adapters) and
/// ranks the rest discrete first (by the shared integrated-graphics name rule, so a Strix Halo APU with a big UMA
/// frame buffer does not outrank an eGPU), then by dedicated video memory, so the discrete card comes first and is recommended by default.
/// </summary>
public static class GpuAdapterRanking
{
    public const uint MicrosoftVendorId = 0x1414;
    public const uint BasicRenderDeviceId = 0x8C;

    public static bool IsSoftwareAdapter(GpuAdapterInfo adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        return adapter.IsSoftware
            || (adapter.VendorId == MicrosoftVendorId && adapter.DeviceId == BasicRenderDeviceId)
            || adapter.Name.Contains("Basic Render", StringComparison.OrdinalIgnoreCase)
            || adapter.Name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Hardware adapters, most dedicated memory first (ties keep enumeration order); the first is recommended.</summary>
    public static IReadOnlyList<GpuAdapterChoice> Rank(IEnumerable<GpuAdapterInfo> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        var hardware = adapters
            .Where(a => !IsSoftwareAdapter(a))
            .GroupBy(a => a.Index)
            .Select(g => g.First())
            .OrderBy(a => Hardware.HardwareMappings.IsIntegratedGpu(a.Name, VendorName(a.VendorId) is "AMD" or "Intel" ? VendorName(a.VendorId) : string.Empty))
            .ThenByDescending(a => a.DedicatedVideoMemoryBytes)
            .ThenBy(a => a.Index)
            .ToList();

        return hardware
            .Select((a, i) => new GpuAdapterChoice(a.Index, string.IsNullOrWhiteSpace(a.Name) ? "Graphics adapter" : a.Name.Trim(), VendorName(a.VendorId), Math.Max(0, a.DedicatedVideoMemoryBytes), i == 0))
            .ToList();
    }

    /// <summary>The adapter to use for <paramref name="requestedIndex"/>: that one when present, otherwise the recommended one.</summary>
    public static GpuAdapterChoice? Pick(IReadOnlyList<GpuAdapterChoice> choices, int requestedIndex)
    {
        ArgumentNullException.ThrowIfNull(choices);
        if (requestedIndex >= 0 && choices.FirstOrDefault(c => c.Index == requestedIndex) is { } exact)
        {
            return exact;
        }

        return choices.FirstOrDefault(c => c.IsRecommended) ?? choices.FirstOrDefault();
    }

    /// <summary>PCI vendor id → brand.</summary>
    public static string VendorName(uint vendorId) => vendorId switch
    {
        0x10DE => "NVIDIA",
        0x1002 or 0x1022 => "AMD",
        0x8086 => "Intel",
        0x5143 => "Qualcomm",
        MicrosoftVendorId => "Microsoft",
        _ => "Unknown",
    };
}
