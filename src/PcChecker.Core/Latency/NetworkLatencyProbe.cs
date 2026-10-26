using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PcChecker.Core.Latency;

/// <summary>Ping, DNS timing and default-gateway discovery. Pure .NET networking; guarded so failures become "no reply".</summary>
public static class NetworkLatencyProbe
{
    public const string GatewayToken = "gateway";
    public const int PingTimeoutMs = 2000;
    public const int PingIntervalMs = 120;

    /// <summary>Friendly labels for well-known targets.</summary>
    public static string LabelFor(string target) => target.ToLowerInvariant() switch
    {
        GatewayToken => "Your router",
        "1.1.1.1" or "1.0.0.1" => "Cloudflare DNS",
        "8.8.8.8" or "8.8.4.4" => "Google DNS",
        "9.9.9.9" => "Quad9 DNS",
        _ => target,
    };

    /// <summary>The IPv4 default gateway of the first connected adapter, or null.</summary>
    public static IPAddress? FindDefaultGateway()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                var gateway = nic.GetIPProperties().GatewayAddresses
                    .Select(g => g.Address)
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
                if (gateway is not null)
                {
                    return gateway;
                }
            }
        }
        catch
        {
            // No network stack information available.
        }

        return null;
    }

    /// <summary>
    /// Pings one target <paramref name="count"/> times. "gateway" resolves to the default gateway; when none is found the
    /// result has no replies. Never throws except for cancellation.
    /// </summary>
    public static async Task<NetworkLatencyResult> PingAsync(string target, string label, int count, CancellationToken cancellationToken)
    {
        count = Math.Clamp(count, 1, 100);
        string host = target;
        if (string.Equals(target, GatewayToken, StringComparison.OrdinalIgnoreCase))
        {
            var gateway = FindDefaultGateway();
            if (gateway is null)
            {
                return new NetworkLatencyResult(target, label, null, null, null, null, 100, count);
            }

            host = gateway.ToString();
        }

        var samples = new List<double>(count);
        int lost = 0;
        using var ping = new Ping();
        for (int i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var reply = await ping.SendPingAsync(host, PingTimeoutMs).WaitAsync(cancellationToken).ConfigureAwait(false);
                if (reply.Status == IPStatus.Success)
                {
                    // RoundtripTime is whole milliseconds; use the stopwatch for sub-millisecond LAN replies.
                    double ms = reply.RoundtripTime == 0 ? Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2) : reply.RoundtripTime;
                    samples.Add(ms);
                }
                else
                {
                    lost++;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                lost++;
            }

            if (i < count - 1)
            {
                await Task.Delay(PingIntervalMs, cancellationToken).ConfigureAwait(false);
            }
        }

        return NetworkLatencyMath.Summarize(target, label, samples, lost);
    }

    /// <summary>Time to resolve a host name in milliseconds, or null when the lookup fails.</summary>
    public static async Task<double?> MeasureDnsAsync(string host, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        try
        {
            var stopwatch = Stopwatch.StartNew();
            var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            return addresses.Length == 0 ? null : Math.Round(stopwatch.Elapsed.TotalMilliseconds, 1);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }
}
