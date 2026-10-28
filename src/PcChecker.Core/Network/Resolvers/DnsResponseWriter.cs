using System.Buffers.Binary;
using System.Net;

namespace PcChecker.Core.Network.Resolvers;

/// <summary>
/// Builds a response to a query packet: the question copied back, then address answers whose names point at the
/// question with a compression pointer. Demo mode answers its simulated lookups with it, so the real parser runs. Pure.
/// </summary>
public static class DnsResponseWriter
{
    /// <summary>
    /// A response to <paramref name="query"/> (its id and question) with <paramref name="addresses"/> as answers
    /// (A or AAAA by address family) and the given response code. Returns null when the query has no readable question.
    /// </summary>
    public static byte[]? Answer(ReadOnlySpan<byte> query, int responseCode, IReadOnlyList<IPAddress> addresses, bool authenticatedData = false, uint ttl = 300)
    {
        if (query.Length < 12)
        {
            return null;
        }

        int offset = 12;
        if (!DnsResponseParser.TryReadName(query, ref offset, out _) || offset + 4 > query.Length)
        {
            return null;
        }

        int questionEnd = offset + 4;
        var packet = new List<byte>(questionEnd + addresses.Count * 28);
        packet.AddRange(query[..questionEnd].ToArray());
        ushort flags = (ushort)(0x8000 | 0x0080 | (responseCode & 0x0F));
        if ((BinaryPrimitives.ReadUInt16BigEndian(query[2..]) & 0x0100) != 0)
        {
            flags |= 0x0100;
        }

        if (authenticatedData)
        {
            flags |= 0x0020;
        }

        Span<byte> header = stackalloc byte[12];
        query[..2].CopyTo(header);
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], flags);
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], 1);
        BinaryPrimitives.WriteUInt16BigEndian(header[6..], (ushort)addresses.Count);
        for (int i = 0; i < 12; i++)
        {
            packet[i] = header[i];
        }

        Span<byte> record = stackalloc byte[12];
        foreach (var address in addresses)
        {
            byte[] raw = address.GetAddressBytes();
            ushort type = raw.Length == 16 ? DnsRecordType.Aaaa : DnsRecordType.A;
            BinaryPrimitives.WriteUInt16BigEndian(record, 0xC00C); // pointer to the question name
            BinaryPrimitives.WriteUInt16BigEndian(record[2..], type);
            BinaryPrimitives.WriteUInt16BigEndian(record[4..], 1);
            BinaryPrimitives.WriteUInt32BigEndian(record[6..], ttl);
            BinaryPrimitives.WriteUInt16BigEndian(record[10..], (ushort)raw.Length);
            packet.AddRange(record.ToArray());
            packet.AddRange(raw);
        }

        return [.. packet];
    }
}
