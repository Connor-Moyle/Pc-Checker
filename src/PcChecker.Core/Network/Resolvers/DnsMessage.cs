using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace PcChecker.Core.Network.Resolvers;

/// <summary>DNS record types PC Checker asks for or reads (RFC 1035, RFC 3596, RFC 6891).</summary>
public static class DnsRecordType
{
    public const ushort A = 1;
    public const ushort Cname = 5;
    public const ushort Aaaa = 28;
    public const ushort Opt = 41;
}

/// <summary>DNS response codes that matter for the benchmark.</summary>
public static class DnsResponseCode
{
    public const int NoError = 0;
    public const int FormatError = 1;
    public const int ServerFailure = 2;
    public const int NameError = 3;
    public const int Refused = 5;
}

/// <summary>One record of the answer section. <see cref="Data"/> is the address for A/AAAA and the name for CNAME.</summary>
public sealed record DnsAnswer(string Name, ushort Type, uint Ttl, string? Data);

/// <summary>
/// A parsed DNS response. <see cref="IsComplete"/> is false when the answer section was cut short or malformed; the
/// answers read up to that point are kept.
/// </summary>
public sealed record DnsResponse(
    ushort Id,
    bool IsResponse,
    bool Truncated,
    bool RecursionAvailable,
    bool AuthenticatedData,
    int ResponseCode,
    string? QuestionName,
    ushort QuestionType,
    IReadOnlyList<DnsAnswer> Answers,
    bool IsComplete)
{
    /// <summary>True for NOERROR and NXDOMAIN: the server did its job (a name that does not exist is a valid answer).</summary>
    public bool IsUsableAnswer => IsResponse && ResponseCode is DnsResponseCode.NoError or DnsResponseCode.NameError;
}

/// <summary>Builds DNS query packets (RFC 1035 section 4, EDNS0 from RFC 6891). Pure.</summary>
public static class DnsQueryBuilder
{
    /// <summary>The UDP payload size advertised with EDNS0 (the DNS flag day 2020 value).</summary>
    public const ushort EdnsPayloadSize = 1232;

    private const int MaxNameLength = 255;
    private const int MaxLabelLength = 63;

    /// <summary>
    /// A standard query for <paramref name="name"/>. With <paramref name="dnssecOk"/> the query carries an EDNS0 OPT
    /// record with the DO bit and the AD bit, which asks a validating resolver to say whether it checked the signatures.
    /// Throws <see cref="ArgumentException"/> for a name that cannot be encoded.
    /// </summary>
    public static byte[] Build(ushort id, string name, ushort type, bool recursionDesired = true, bool dnssecOk = false)
    {
        var encodedName = EncodeName(name);
        int length = 12 + encodedName.Length + 4 + (dnssecOk ? 11 : 0);
        var packet = new byte[length];
        var span = packet.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(span, id);
        ushort flags = 0;
        if (recursionDesired)
        {
            flags |= 0x0100;
        }

        if (dnssecOk)
        {
            flags |= 0x0020; // AD: "tell me whether you validated" (RFC 6840 5.7)
        }

        BinaryPrimitives.WriteUInt16BigEndian(span[2..], flags);
        BinaryPrimitives.WriteUInt16BigEndian(span[4..], 1); // QDCOUNT
        BinaryPrimitives.WriteUInt16BigEndian(span[10..], (ushort)(dnssecOk ? 1 : 0)); // ARCOUNT
        int offset = 12;
        encodedName.CopyTo(span[offset..]);
        offset += encodedName.Length;
        BinaryPrimitives.WriteUInt16BigEndian(span[offset..], type);
        BinaryPrimitives.WriteUInt16BigEndian(span[(offset + 2)..], 1); // class IN
        offset += 4;
        if (dnssecOk)
        {
            span[offset] = 0; // root name
            BinaryPrimitives.WriteUInt16BigEndian(span[(offset + 1)..], DnsRecordType.Opt);
            BinaryPrimitives.WriteUInt16BigEndian(span[(offset + 3)..], EdnsPayloadSize);
            BinaryPrimitives.WriteUInt32BigEndian(span[(offset + 5)..], 0x00008000); // extended RCODE 0, version 0, DO
            BinaryPrimitives.WriteUInt16BigEndian(span[(offset + 9)..], 0); // RDLENGTH
        }

        return packet;
    }

    /// <summary>The wire form of a name: length-prefixed labels ending with a zero byte. A trailing dot is allowed.</summary>
    public static byte[] EncodeName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        string trimmed = name.Trim().TrimEnd('.');
        if (trimmed.Length == 0)
        {
            return [0];
        }

        var bytes = new List<byte>(trimmed.Length + 2);
        foreach (var label in trimmed.Split('.'))
        {
            if (label.Length == 0 || label.Length > MaxLabelLength || !label.All(c => c is > ' ' and < (char)127))
            {
                throw new ArgumentException($"'{name}' is not a valid DNS name.", nameof(name));
            }

            bytes.Add((byte)label.Length);
            bytes.AddRange(Encoding.ASCII.GetBytes(label));
        }

        bytes.Add(0);
        if (bytes.Count > MaxNameLength)
        {
            throw new ArgumentException($"'{name}' is longer than 255 bytes.", nameof(name));
        }

        return [.. bytes];
    }
}

/// <summary>
/// Parses DNS responses (RFC 1035 section 4, with name compression). Never throws: a packet too short for its header
/// or question gives false, a broken answer section gives a response marked incomplete. Pure.
/// </summary>
public static class DnsResponseParser
{
    private const int MaxPointerJumps = 32;
    private const int MaxAnswers = 256;

    public static bool TryParse(ReadOnlySpan<byte> packet, out DnsResponse? response)
    {
        response = null;
        try
        {
            return TryParseCore(packet, out response);
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or FormatException)
        {
            // Defensive: every read is bounds-checked, but a parser fed by the network must never take the app down.
            response = null;
            return false;
        }
    }

    private static bool TryParseCore(ReadOnlySpan<byte> packet, out DnsResponse? response)
    {
        response = null;
        if (packet.Length < 12)
        {
            return false;
        }

        ushort id = BinaryPrimitives.ReadUInt16BigEndian(packet);
        ushort flags = BinaryPrimitives.ReadUInt16BigEndian(packet[2..]);
        int questions = BinaryPrimitives.ReadUInt16BigEndian(packet[4..]);
        int answers = BinaryPrimitives.ReadUInt16BigEndian(packet[6..]);
        bool isResponse = (flags & 0x8000) != 0;
        bool truncated = (flags & 0x0200) != 0;
        bool recursionAvailable = (flags & 0x0080) != 0;
        bool authenticated = (flags & 0x0020) != 0;
        int rcode = flags & 0x000F;

        int offset = 12;
        string? questionName = null;
        ushort questionType = 0;
        for (int i = 0; i < questions; i++)
        {
            if (!TryReadName(packet, ref offset, out var name) || offset + 4 > packet.Length)
            {
                return false;
            }

            if (i == 0)
            {
                questionName = name;
                questionType = BinaryPrimitives.ReadUInt16BigEndian(packet[offset..]);
            }

            offset += 4;
        }

        var list = new List<DnsAnswer>();
        bool complete = true;
        for (int i = 0; i < answers && i < MaxAnswers; i++)
        {
            if (!TryReadName(packet, ref offset, out var name) || offset + 10 > packet.Length)
            {
                complete = false;
                break;
            }

            ushort type = BinaryPrimitives.ReadUInt16BigEndian(packet[offset..]);
            uint ttl = BinaryPrimitives.ReadUInt32BigEndian(packet[(offset + 4)..]);
            int rdLength = BinaryPrimitives.ReadUInt16BigEndian(packet[(offset + 8)..]);
            offset += 10;
            if (offset + rdLength > packet.Length)
            {
                complete = false;
                break;
            }

            string? data = type switch
            {
                DnsRecordType.A when rdLength == 4 => new IPAddress(packet.Slice(offset, 4)).ToString(),
                DnsRecordType.Aaaa when rdLength == 16 => new IPAddress(packet.Slice(offset, 16)).ToString(),
                DnsRecordType.Cname => ReadNameAt(packet, offset),
                _ => null,
            };
            if (type is DnsRecordType.A or DnsRecordType.Aaaa && data is null)
            {
                complete = false; // An address record of the wrong size: stop trusting the rest.
                break;
            }

            list.Add(new DnsAnswer(name, type, ttl, data));
            offset += rdLength;
        }

        response = new DnsResponse(id, isResponse, truncated, recursionAvailable, authenticated, rcode, questionName, questionType, list,
            complete && (answers <= MaxAnswers));
        return true;
    }

    private static string? ReadNameAt(ReadOnlySpan<byte> packet, int offset) =>
        TryReadName(packet, ref offset, out var name) ? name : null;

    /// <summary>
    /// Reads a possibly compressed name starting at <paramref name="offset"/> and moves the offset past it (past the
    /// first pointer when the name is compressed). Pointer loops, reserved label types and names over 255 bytes fail.
    /// </summary>
    internal static bool TryReadName(ReadOnlySpan<byte> packet, ref int offset, out string name)
    {
        name = string.Empty;
        var builder = new StringBuilder();
        int position = offset;
        int jumps = 0;
        int wireLength = 0;
        int? resumeAt = null;
        while (true)
        {
            if (position >= packet.Length)
            {
                return false;
            }

            byte length = packet[position];
            if (length == 0)
            {
                position++;
                break;
            }

            switch (length & 0xC0)
            {
                case 0xC0:
                {
                    if (position + 1 >= packet.Length || ++jumps > MaxPointerJumps)
                    {
                        return false;
                    }

                    int target = ((length & 0x3F) << 8) | packet[position + 1];
                    resumeAt ??= position + 2;
                    if (target >= packet.Length)
                    {
                        return false;
                    }

                    position = target;
                    continue;
                }

                case 0x00:
                {
                    if (position + 1 + length > packet.Length)
                    {
                        return false;
                    }

                    wireLength += length + 1;
                    if (wireLength > 255)
                    {
                        return false;
                    }

                    if (builder.Length > 0)
                    {
                        builder.Append('.');
                    }

                    foreach (byte b in packet.Slice(position + 1, length))
                    {
                        builder.Append(b is > 32 and < 127 ? (char)b : '?');
                    }

                    position += 1 + length;
                    continue;
                }

                default:
                    return false; // 0x40 and 0x80 label types are not used by DNS today.
            }
        }

        offset = resumeAt ?? position;
        name = builder.ToString();
        return true;
    }
}
