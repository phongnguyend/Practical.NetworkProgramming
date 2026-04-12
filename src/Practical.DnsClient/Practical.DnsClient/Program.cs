using System.Net;
using System.Net.Sockets;
using System.Text;

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

public class Program
{
    public static async Task Main()
    {
        var ips = await Query("github.com", DnsRecordType.NS);
        foreach (var ip in ips)
        {
            Console.WriteLine($"IP: {ip}");
        }
    }

    public static async Task<List<string>> Query(string domain, DnsRecordType queryType = DnsRecordType.A)
    {
        var dnsServer = "8.8.8.8";
        var port = 53;

        using var udp = new UdpClient();
        udp.Connect(dnsServer, port);

        var request = BuildQuery(domain, queryType);
        await udp.SendAsync(request, request.Length);

        var remoteEP = new IPEndPoint(IPAddress.Any, 0);
        var result = await udp.ReceiveAsync();
        var response = result.Buffer;

        return ParseResponse(response);
    }

    private static byte[] BuildQuery(string domain, DnsRecordType queryType = DnsRecordType.A)
    {
        var rand = new Random();
        ushort id = (ushort)rand.Next(ushort.MaxValue);

        var ms = new MemoryStream();

        // Header
        WriteUInt16(ms, id);        // ID
        WriteUInt16(ms, 0x0100);   // Flags (standard query)
        WriteUInt16(ms, 1);        // QDCOUNT
        WriteUInt16(ms, 0);        // ANCOUNT
        WriteUInt16(ms, 0);        // NSCOUNT
        WriteUInt16(ms, 0);        // ARCOUNT

        // Question
        foreach (var label in domain.Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            ms.WriteByte((byte)bytes.Length);
            ms.Write(bytes, 0, bytes.Length);
        }

        ms.WriteByte(0);           // End of domain
        WriteUInt16(ms, (ushort)queryType); // QTYPE
        WriteUInt16(ms, 1);        // QCLASS = IN

        return ms.ToArray();
    }

    private static List<string> ParseResponse(byte[] response)
    {
        var results = new List<string>();
        int offset = 0;

        offset += 4; // Skip ID + flags

        ushort qdCount = ReadUInt16(response, ref offset);
        ushort anCount = ReadUInt16(response, ref offset);

        offset += 4; // NSCOUNT + ARCOUNT

        // Skip question
        for (int i = 0; i < qdCount; i++)
        {
            SkipDomain(response, ref offset);
            offset += 4;
        }

        // Parse answers
        for (int i = 0; i < anCount; i++)
        {
            SkipDomain(response, ref offset);

            ushort type = ReadUInt16(response, ref offset);
            ushort cls = ReadUInt16(response, ref offset);
            uint ttl = ReadUInt32(response, ref offset);
            ushort dataLen = ReadUInt16(response, ref offset);

            DnsRecordType recordType = (DnsRecordType)type;

            switch (recordType)
            {
                case DnsRecordType.A when dataLen == 4:
                    var ip = new IPAddress(
                    [
                        response[offset],
                        response[offset + 1],
                        response[offset + 2],
                        response[offset + 3]
                    ]);
                    results.Add(ip.ToString());
                    break;

                case DnsRecordType.AAAA when dataLen == 16:
                    var ipv6Bytes = new byte[16];
                    Array.Copy(response, offset, ipv6Bytes, 0, 16);
                    var ipv6 = new IPAddress(ipv6Bytes);
                    results.Add(ipv6.ToString());
                    break;

                case DnsRecordType.CNAME:
                case DnsRecordType.NS:
                case DnsRecordType.PTR:
                    int domainOffset = offset;
                    string domain = ReadDomain(response, ref domainOffset);
                    results.Add(domain);
                    break;

                case DnsRecordType.MX:
                    int mxOffset = offset;
                    ushort preference = ReadUInt16(response, ref mxOffset);
                    string exchange = ReadDomain(response, ref mxOffset);
                    results.Add($"{preference} {exchange}");
                    break;

                case DnsRecordType.TXT:
                    string txt = ParseTxtRecord(response, offset, dataLen);
                    results.Add(txt);
                    break;

                case DnsRecordType.SOA:
                    int soaOffset = offset;
                    string mname = ReadDomain(response, ref soaOffset);
                    string rname = ReadDomain(response, ref soaOffset);
                    uint serial = ReadUInt32(response, ref soaOffset);
                    uint refresh = ReadUInt32(response, ref soaOffset);
                    uint retry = ReadUInt32(response, ref soaOffset);
                    uint expire = ReadUInt32(response, ref soaOffset);
                    uint minimum = ReadUInt32(response, ref soaOffset);
                    results.Add($"{mname} {rname} {serial} {refresh} {retry} {expire} {minimum}");
                    break;

                case DnsRecordType.SRV:
                    int srvOffset = offset;
                    ushort priority = ReadUInt16(response, ref srvOffset);
                    ushort weight = ReadUInt16(response, ref srvOffset);
                    ushort port = ReadUInt16(response, ref srvOffset);
                    string target = ReadDomain(response, ref srvOffset);
                    results.Add($"{priority} {weight} {port} {target}");
                    break;

                case DnsRecordType.CAA:
                    int caaOffset = offset;
                    byte flags = response[caaOffset];
                    caaOffset++;
                    byte tagLength = response[caaOffset];
                    caaOffset++;
                    string tag = Encoding.ASCII.GetString(response, caaOffset, tagLength);
                    caaOffset += tagLength;
                    int valueLen = dataLen - 2 - tagLength;
                    string value = Encoding.ASCII.GetString(response, caaOffset, valueLen);
                    results.Add($"{flags} {tag} {value}");
                    break;
            }

            offset += dataLen;
        }

        return results;
    }

    private static string ParseTxtRecord(byte[] data, int offset, int length)
    {
        var parts = new List<string>();
        int endOffset = offset + length;

        while (offset < endOffset)
        {
            byte textLength = data[offset];
            offset++;
            if (textLength > 0 && offset + textLength <= endOffset)
            {
                string text = Encoding.UTF8.GetString(data, offset, textLength);
                parts.Add(text);
                offset += textLength;
            }
        }

        return string.Join("", parts);
    }

    private static void SkipDomain(byte[] data, ref int offset)
    {
        while (data[offset] != 0)
        {
            if ((data[offset] & 0xC0) == 0xC0)
            {
                offset += 2;
                return;
            }

            offset += data[offset] + 1;
        }

        offset++;
    }

    private static string ReadDomain(byte[] data, ref int offset)
    {
        var labels = new List<string>();

        while (data[offset] != 0)
        {
            if ((data[offset] & 0xC0) == 0xC0)
            {
                // Pointer
                ushort pointer = (ushort)(((data[offset] & 0x3F) << 8) | data[offset + 1]);
                offset += 2;
                int pointerOffset = pointer;
                labels.Add(ReadDomain(data, ref pointerOffset));
                break;
            }
            else
            {
                byte length = data[offset];
                offset++;
                var label = Encoding.ASCII.GetString(data, offset, length);
                labels.Add(label);
                offset += length;
            }
        }

        offset++; // Skip null terminator

        return string.Join(".", labels);
    }

    private static void WriteUInt16(Stream s, ushort value)
    {
        s.WriteByte((byte)(value >> 8));
        s.WriteByte((byte)(value & 0xFF));
    }

    private static ushort ReadUInt16(byte[] data, ref int offset)
    {
        return (ushort)((data[offset++] << 8) | data[offset++]);
    }

    private static uint ReadUInt32(byte[] data, ref int offset)
    {
        return (uint)(
            (data[offset++] << 24) |
            (data[offset++] << 16) |
            (data[offset++] << 8) |
            data[offset++]);
    }
}