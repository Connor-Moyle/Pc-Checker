using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;

namespace PcChecker.Core.Upgrades;

/// <summary>Reads the PCIe link properties of a device with <c>Get-PnpDeviceProperty</c>. Read only.</summary>
public sealed class WindowsPcieLinkReader : IPcieLinkReader
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private readonly ILogger<WindowsPcieLinkReader> _logger;

    public WindowsPcieLinkReader(ILogger<WindowsPcieLinkReader> logger) => _logger = logger;

    public async Task<PcieLinkInfo?> ReadAsync(string? pnpDeviceId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(pnpDeviceId) || !pnpDeviceId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase) || !PlatformGuard.IsWindows)
        {
            return null;
        }

        string keys = string.Join(",", new[] { PcieLinkParser.CurrentSpeedKey, PcieLinkParser.MaxSpeedKey, PcieLinkParser.CurrentWidthKey, PcieLinkParser.MaxWidthKey }
            .Select(PowerShellRunner.Quote));
        string command = $"Get-PnpDeviceProperty -InstanceId {PowerShellRunner.Quote(pnpDeviceId)} -KeyName {keys} -ErrorAction Stop | "
            + "ForEach-Object { $_.KeyName + '=' + [string]$_.Data }";
        try
        {
            var result = await PowerShellRunner.RunAsync(command, Timeout, cancellationToken).ConfigureAwait(false);
            if (!result.Success)
            {
                _logger.LogDebug("PCIe link properties could not be read: {Error}", PowerShellRunner.DecodeCliXml(result.StdErr));
                return null;
            }

            return PcieLinkParser.Parse(result.StdOut);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Reading the PCIe link failed.");
            return null;
        }
    }
}
