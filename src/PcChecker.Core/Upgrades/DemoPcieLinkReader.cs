using PcChecker.Core.Common;

namespace PcChecker.Core.Upgrades;

/// <summary>Demo link: the sample RTX 4070 runs at PCIe 4.0 x16 in the top slot.</summary>
public sealed class DemoPcieLinkReader : IPcieLinkReader
{
    public async Task<PcieLinkInfo?> ReadAsync(string? pnpDeviceId, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 50, 150).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(pnpDeviceId) ? null : new PcieLinkInfo(4, 4, 16, 16);
    }
}
