using System.Net;
using System.Net.Sockets;
using System.Text;

await DnsServer.RunAsync();

public enum DnsRecordType : ushort
{
    A = 1,
    NS = 2,
    CNAME = 5,
    SOA = 6,
    PTR = 12,
    MX = 15,
    TXT = 16,
    AAAA = 28,
    SRV = 33,
    CAA = 257
}

public static class DnsServer
{
    private const int Port = 1989;
    private const string UpstreamDns = "8.8.8.8";

    // Local records: key = (name lower, type), value = list of rdata strings
    private static readonly Dictionary<(string, DnsRecordType), List<string>> LocalRecords = new()
    {
        { ("localhost", DnsRecordType.A),       ["127.0.0.1"] },
        { ("myserver.local", DnsRecordType.A),  ["192.168.1.10"] },
        { ("mail.local", DnsRecordType.MX),     ["10 myserver.local"] },
        { ("myserver.local", DnsRecordType.TXT),["v=spf1 mx ~all"] },
    };

    public static async Task RunAsync()
    {
        using var udp = new UdpClient(Port);
        Console.WriteLine($"DNS Server listening on port {Port}...");
        Console.WriteLine("Local records:");
        foreach (var kv in LocalRecords)
            Console.WriteLine($"  {kv.Key.Item1,-30} {kv.Key.Item2,-6} -> {string.Join(", ", kv.Value)}");
        Console.WriteLine();

        while (true)
        {
            UdpReceiveResult result;
            try
            {
                result = await udp.ReceiveAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Receive error] {ex.Message}");
                continue;
            }

            _ = HandleQueryAsync(udp, result.Buffer, result.RemoteEndPoint);
        }
    }

    private static async Task HandleQueryAsync(UdpClient udp, byte[] request, IPEndPoint client)
    {
        try
        {
            int offset = 0;
            ushort id = ReadUInt16(request, ref offset);
            offset += 2; // flags
            ushort qdCount = ReadUInt16(request, ref offset);
            offset += 6; // anCount, nsCount, arCount

            if (qdCount == 0) return;

            int questionStart = offset;
            string qname = ReadDomain(request, ref offset);
            ushort qtype = ReadUInt16(request, ref offset);
            ushort qclass = ReadUInt16(request, ref offset);

            var recordType = (DnsRecordType)qtype;
            var key = (qname.ToLowerInvariant(), recordType);

            Console.WriteLine($"[Query] {client} -> {qname} {recordType}");

            byte[] response;

            if (LocalRecords.TryGetValue(key, out var records))
            {
                Console.WriteLine($"[Local]  {qname} {recordType} -> {string.Join(", ", records)}");
                response = BuildResponse(id, request, questionStart, qname, recordType, qclass, records);
            }
            else
            {
                Console.WriteLine($"[Forward] {qname} {recordType} -> {UpstreamDns}");
                response = await ForwardAsync(request);
            }

            await udp.SendAsync(response, response.Length, client);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Handler error] {ex.Message}");
        }
    }

    private static byte[] BuildResponse(ushort id, byte[] request, int questionStart,
        string qname, DnsRecordType recordType, ushort qclass, List<string> records)
    {
        var ms = new MemoryStream();

        // Header
        WriteUInt16(ms, id);
        WriteUInt16(ms, 0x8180); // QR=1, Opcode=0, AA=1, RD=1, RA=1, RCODE=0
        WriteUInt16(ms, 1);                      // QDCOUNT
        WriteUInt16(ms, (ushort)records.Count);  // ANCOUNT
        WriteUInt16(ms, 0);                      // NSCOUNT
        WriteUInt16(ms, 0);                      // ARCOUNT

        // Question (copy from original request)
        int qEnd = questionStart;
        SkipDomain(request, ref qEnd);
        qEnd += 4; // type + class
        ms.Write(request, questionStart, qEnd - questionStart);

        // Answers
        foreach (var rdata in records)
        {
            // Name pointer back to question
            ms.WriteByte(0xC0);
            ms.WriteByte((byte)questionStart);

            WriteUInt16(ms, (ushort)recordType);
            WriteUInt16(ms, qclass);
            WriteUInt32(ms, 300); // TTL

            WriteRData(ms, recordType, rdata);
        }

        return ms.ToArray();
    }

    private static void WriteRData(MemoryStream ms, DnsRecordType type, string rdata)
    {
        switch (type)
        {
            case DnsRecordType.A:
            {
                var bytes = IPAddress.Parse(rdata).GetAddressBytes();
                WriteUInt16(ms, (ushort)bytes.Length);
                ms.Write(bytes);
                break;
            }
            case DnsRecordType.AAAA:
            {
                var bytes = IPAddress.Parse(rdata).GetAddressBytes();
                WriteUInt16(ms, (ushort)bytes.Length);
                ms.Write(bytes);
                break;
            }
            case DnsRecordType.CNAME:
            case DnsRecordType.NS:
            case DnsRecordType.PTR:
            {
                var domainBytes = EncodeDomain(rdata);
                WriteUInt16(ms, (ushort)domainBytes.Length);
                ms.Write(domainBytes);
                break;
            }
            case DnsRecordType.MX:
            {
                var parts = rdata.Split(' ', 2);
                ushort pref = ushort.Parse(parts[0]);
                var domainBytes = EncodeDomain(parts[1]);
                WriteUInt16(ms, (ushort)(2 + domainBytes.Length));
                WriteUInt16(ms, pref);
                ms.Write(domainBytes);
                break;
            }
            case DnsRecordType.TXT:
            {
                var textBytes = Encoding.UTF8.GetBytes(rdata);
                WriteUInt16(ms, (ushort)(1 + textBytes.Length));
                ms.WriteByte((byte)textBytes.Length);
                ms.Write(textBytes);
                break;
            }
            default:
            {
                // Unsupported: write empty rdata
                WriteUInt16(ms, 0);
                break;
            }
        }
    }

    private static async Task<byte[]> ForwardAsync(byte[] request)
    {
        using var upstream = new UdpClient();
        upstream.Connect(UpstreamDns, 53);
        await upstream.SendAsync(request, request.Length);
        upstream.Client.ReceiveTimeout = 3000;
        var result = await upstream.ReceiveAsync();
        return result.Buffer;
    }

    // ── Wire-format helpers ──────────────────────────────────────────────────

    private static byte[] EncodeDomain(string domain)
    {
        var ms = new MemoryStream();
        foreach (var label in domain.TrimEnd('.').Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            ms.WriteByte((byte)bytes.Length);
            ms.Write(bytes);
        }
        ms.WriteByte(0);
        return ms.ToArray();
    }

    private static string ReadDomain(byte[] data, ref int offset)
    {
        var labels = new List<string>();

        while (data[offset] != 0)
        {
            if ((data[offset] & 0xC0) == 0xC0)
            {
                int ptr = ((data[offset] & 0x3F) << 8) | data[offset + 1];
                offset += 2;
                int ptrOff = ptr;
                labels.Add(ReadDomain(data, ref ptrOff));
                return string.Join(".", labels);
            }

            byte len = data[offset++];
            labels.Add(Encoding.ASCII.GetString(data, offset, len));
            offset += len;
        }

        offset++; // null terminator
        return string.Join(".", labels);
    }

    private static void SkipDomain(byte[] data, ref int offset)
    {
        while (data[offset] != 0)
        {
            if ((data[offset] & 0xC0) == 0xC0) { offset += 2; return; }
            offset += data[offset] + 1;
        }
        offset++;
    }

    private static void WriteUInt16(Stream s, ushort value)
    {
        s.WriteByte((byte)(value >> 8));
        s.WriteByte((byte)(value & 0xFF));
    }

    private static void WriteUInt32(Stream s, uint value)
    {
        s.WriteByte((byte)(value >> 24));
        s.WriteByte((byte)(value >> 16));
        s.WriteByte((byte)(value >> 8));
        s.WriteByte((byte)(value & 0xFF));
    }

    private static ushort ReadUInt16(byte[] data, ref int offset)
    {
        return (ushort)((data[offset++] << 8) | data[offset++]);
    }
}
