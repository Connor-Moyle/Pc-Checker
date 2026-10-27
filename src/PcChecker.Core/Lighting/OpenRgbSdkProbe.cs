using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace PcChecker.Core.Lighting;

/// <summary>What a read-only probe of the SDK server returned.</summary>
/// <param name="ServerProtocol">The server's highest protocol version (0 when it did not answer the version request).</param>
/// <param name="UsedProtocol">The version the device data was read with.</param>
/// <param name="Devices">Every controller that could be read, by index.</param>
/// <param name="Failures">Controllers that could not be read: index and reason.</param>
public sealed record OpenRgbProbeResult(int ServerProtocol, int UsedProtocol, IReadOnlyList<RgbDevice> Devices, IReadOnlyList<(int Index, string Reason)> Failures);

/// <summary>
/// A small read-only client for the OpenRGB SDK server. It negotiates the protocol version (up to
/// <see cref="OpenRgbControllerDataReader.MaxProtocolVersion"/>), reads the controller count and each controller one by
/// one with the defensive <see cref="OpenRgbControllerDataReader"/>, so one device the main client cannot read no
/// longer hides the others. It never sends a change.
/// </summary>
public static class OpenRgbSdkProbe
{
    public const uint RequestControllerCount = 0;
    public const uint RequestControllerData = 1;
    public const uint RequestProtocolVersion = 40;
    public const uint SetClientName = 50;

    private static readonly byte[] Magic = "ORGB"u8.ToArray();
    private const int HeaderSize = 16;
    private const int MaxPacket = 16 * 1024 * 1024;

    /// <summary>A packet header followed by its data, as the SDK expects (little-endian).</summary>
    public static byte[] BuildPacket(uint deviceIndex, uint packetId, ReadOnlySpan<byte> data)
    {
        var packet = new byte[HeaderSize + data.Length];
        Magic.CopyTo(packet, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), deviceIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), packetId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), (uint)data.Length);
        data.CopyTo(packet.AsSpan(HeaderSize));
        return packet;
    }

    /// <summary>Parses a 16-byte header. False when the magic is wrong or the size is absurd.</summary>
    public static bool TryParseHeader(ReadOnlySpan<byte> header, out uint deviceIndex, out uint packetId, out int size)
    {
        deviceIndex = 0;
        packetId = 0;
        size = 0;
        if (header.Length < HeaderSize || !header[..4].SequenceEqual(Magic))
        {
            return false;
        }

        deviceIndex = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        packetId = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
        uint raw = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
        if (raw > MaxPacket)
        {
            return false;
        }

        size = (int)raw;
        return true;
    }

    /// <summary>Connects, reads everything and disconnects. Throws on network errors (the caller decides what they mean).</summary>
    public static async Task<OpenRgbProbeResult> ReadAllAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        var ct = cts.Token;
        using var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(host, port, ct).ConfigureAwait(false);
        var stream = tcp.GetStream();

        await SendAsync(stream, 0, SetClientName, Encoding.ASCII.GetBytes("PC Checker (device check)\0"), ct).ConfigureAwait(false);

        // Protocol 0 servers do not answer the version request; give them a moment, then assume 0.
        var version = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(version, (uint)OpenRgbControllerDataReader.MaxProtocolVersion);
        await SendAsync(stream, 0, RequestProtocolVersion, version, ct).ConfigureAwait(false);
        int serverProtocol = 0;
        using (var versionWait = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            versionWait.CancelAfter(TimeSpan.FromSeconds(1));
            try
            {
                var reply = await ReceiveAsync(stream, RequestProtocolVersion, versionWait.Token).ConfigureAwait(false);
                if (reply.Length >= 4)
                {
                    serverProtocol = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(reply), int.MaxValue);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                serverProtocol = 0;
            }
        }

        int used = Math.Min(serverProtocol, OpenRgbControllerDataReader.MaxProtocolVersion);
        await SendAsync(stream, 0, RequestControllerCount, [], ct).ConfigureAwait(false);
        var countReply = await ReceiveAsync(stream, RequestControllerCount, ct).ConfigureAwait(false);
        int count = countReply.Length >= 4 ? (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(countReply), 512) : 0;

        var devices = new List<RgbDevice>();
        var failures = new List<(int, string)>();
        var request = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(request, (uint)used);
        for (int i = 0; i < count; i++)
        {
            await SendAsync(stream, (uint)i, RequestControllerData, used >= 1 ? request : [], ct).ConfigureAwait(false);
            var data = await ReceiveAsync(stream, RequestControllerData, ct).ConfigureAwait(false);
            var parsed = OpenRgbControllerDataReader.TryRead(data, used, out var error);
            if (parsed is null)
            {
                failures.Add((i, error ?? "unknown format"));
            }
            else
            {
                devices.Add(OpenRgbControllerDataReader.ToDevice(parsed, i));
            }
        }

        return new OpenRgbProbeResult(serverProtocol, used, devices, failures);
    }

    private static Task SendAsync(NetworkStream stream, uint device, uint id, ReadOnlySpan<byte> data, CancellationToken ct) =>
        stream.WriteAsync(BuildPacket(device, id, data), ct).AsTask();

    /// <summary>Reads packets until one with <paramref name="packetId"/> arrives (device list notices and the like are skipped).</summary>
    private static async Task<byte[]> ReceiveAsync(NetworkStream stream, uint packetId, CancellationToken ct)
    {
        var header = new byte[HeaderSize];
        while (true)
        {
            await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
            if (!TryParseHeader(header, out _, out var id, out var size))
            {
                throw new InvalidDataException("The OpenRGB SDK server sent a packet PC Checker does not understand.");
            }

            var data = new byte[size];
            if (size > 0)
            {
                await stream.ReadExactlyAsync(data, ct).ConfigureAwait(false);
            }

            if (id == packetId)
            {
                return data;
            }
        }
    }
}
