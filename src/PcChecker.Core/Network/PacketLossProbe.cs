using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using PcChecker.Core.Common;
using PcChecker.Core.Latency;

namespace PcChecker.Core.Network;

/// <summary>
/// Sends ICMP echo requests at a fixed pace (one every <see cref="IntervalMs"/> ms, each with a
/// <see cref="TimeoutMs"/> ms timeout) and summarises them with <see cref="PacketLossMath"/>. Pure .NET; failures count
/// as lost packets and never throw except for cancellation.
/// </summary>
public static class PacketLossProbe
{
    public const int IntervalMs = 200;
    public const int TimeoutMs = 1000;

    /// <summary>
    /// Pings one target <paramref name="count"/> times. "gateway" resolves to the default gateway. <paramref name="onReply"/>
    /// is called after every request (for progress).
    /// </summary>
    public static async Task<PacketLossResult> RunAsync(string target, string label, int count, Action? onReply, CancellationToken cancellationToken)
    {
        count = Math.Clamp(count, 1, 1000);
        var address = await ResolveAsync(target, cancellationToken).ConfigureAwait(false);
        if (address is null)
        {
            for (int i = 0; i < count; i++)
            {
                onReply?.Invoke();
            }

            return new PacketLossResult(target, label + (target.Equals(NetworkLatencyProbe.GatewayToken, StringComparison.OrdinalIgnoreCase) ? " (not found)" : " (name not resolved)"),
                count, 0, 100, null, null, null, null, null);
        }

        var slots = new double?[count];
        var pending = new List<Task>(count);
        for (int i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int index = i;
            pending.Add(Task.Run(async () =>
            {
                slots[index] = await PingOnceAsync(address, cancellationToken).ConfigureAwait(false);
                onReply?.Invoke();
            }, cancellationToken));

            if (i < count - 1)
            {
                await Task.Delay(IntervalMs, cancellationToken).ConfigureAwait(false);
            }
        }

        await Task.WhenAll(pending).ConfigureAwait(false);
        var replies = slots.Where(s => s is not null).Select(s => s!.Value).ToList();
        return PacketLossMath.Summarize(target, label, replies, count - replies.Count);
    }

    private static async Task<double?> PingOnceAsync(IPAddress address, CancellationToken cancellationToken)
    {
        try
        {
            using var ping = new Ping();
            var stopwatch = Stopwatch.StartNew();
            var reply = await ping.SendPingAsync(address, TimeoutMs).WaitAsync(cancellationToken).ConfigureAwait(false);
            if (reply.Status != IPStatus.Success)
            {
                return null;
            }

            // RoundtripTime is whole milliseconds; use the stopwatch for sub-millisecond LAN replies.
            return reply.RoundtripTime == 0 ? Math.Round(Math.Min(stopwatch.Elapsed.TotalMilliseconds, 1), 2) : reply.RoundtripTime;
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

    private static async Task<IPAddress?> ResolveAsync(string target, CancellationToken cancellationToken)
    {
        if (string.Equals(target, NetworkLatencyProbe.GatewayToken, StringComparison.OrdinalIgnoreCase))
        {
            return NetworkLatencyProbe.FindDefaultGateway();
        }

        if (IPAddress.TryParse(target, out var ip))
        {
            return ip;
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(target, cancellationToken).ConfigureAwait(false);
            return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
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

    /// <summary>The targets from settings with friendly labels; the defaults when the list is empty.</summary>
    public static IReadOnlyList<(string Target, string Label)> Targets(NetworkSettings? settings)
    {
        var list = (settings?.PacketLossTargets ?? [])
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(t => (t, NetworkLatencyProbe.LabelFor(t)))
            .ToList();
        return list.Count > 0 ? list : new NetworkSettings().PacketLossTargets.Select(t => (t, NetworkLatencyProbe.LabelFor(t))).ToList();
    }

    /// <summary>Progress text "Pinging 3 targets (120/300)".</summary>
    public static ProgressUpdate Progress(int done, int total, int targets) =>
        new($"Pinging {targets} target{(targets == 1 ? string.Empty : "s")}", total == 0 ? 0 : 100.0 * done / total, $"{done} of {total} pings");
}
