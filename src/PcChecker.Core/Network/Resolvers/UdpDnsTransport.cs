using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace PcChecker.Core.Network.Resolvers;

/// <summary>
/// Plain DNS over UDP port 53. Every query uses its own socket (a fresh random source port), so a late reply to an
/// earlier query can never be mistaken for this one. The time is measured from send to receive only.
/// </summary>
public sealed class UdpDnsTransport : IDnsTransport
{
    private const int MaxResponseSize = 4096;

    public async Task<DnsExchange> ExchangeAsync(string server, byte[] query, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(server, out var address))
        {
            return new DnsExchange(DnsExchangeStatus.NetworkError, null, 0);
        }

        using var socket = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var buffer = new byte[MaxResponseSize];
        var watch = new Stopwatch();
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, 53), cancellationToken).ConfigureAwait(false);
            timer.CancelAfter(timeout);
            watch.Start();
            await socket.SendAsync(query, SocketFlags.None, timer.Token).ConfigureAwait(false);
            int received = await socket.ReceiveAsync(buffer, SocketFlags.None, timer.Token).ConfigureAwait(false);
            watch.Stop();
            return new DnsExchange(DnsExchangeStatus.Answered, buffer.AsSpan(0, received).ToArray(), watch.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new DnsExchange(DnsExchangeStatus.Timeout, null, timeout.TotalMilliseconds);
        }
        catch (SocketException)
        {
            // "Port unreachable", no route (an IPv6 server without IPv6 internet) and similar.
            return new DnsExchange(DnsExchangeStatus.NetworkError, null, watch.Elapsed.TotalMilliseconds);
        }
    }
}
