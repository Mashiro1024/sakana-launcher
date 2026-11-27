using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace Sakana.Minecraft;

internal sealed record MinecraftServerEndpoint(string Host, int Port);

internal sealed record MinecraftSrvResolution(
    IReadOnlyList<MinecraftServerEndpoint> Endpoints,
    bool ServiceUnavailable);

/// <summary>Resolves the optional _minecraft._tcp SRV record for Minecraft Java server addresses.</summary>
internal static class MinecraftSrvResolver
{
    private const ushort DnsTypeSrv = 33;
    private const ushort DnsClassInternet = 1;
    private static readonly TimeSpan DnsServerTimeout = TimeSpan.FromMilliseconds(800);

    public static async Task<MinecraftSrvResolution> ResolveAsync(string hostname, CancellationToken cancellationToken)
    {
        var queryName = $"_minecraft._tcp.{hostname.TrimEnd('.')}.";
        byte[] query;
        ushort transactionId;
        try
        {
            transactionId = (ushort)Random.Shared.Next(ushort.MaxValue + 1);
            query = BuildQuery(queryName, transactionId);
        }
        catch (ArgumentException)
        {
            return new MinecraftSrvResolution([], false);
        }

        foreach (var dnsServer in GetDnsServers())
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(DnsServerTimeout);
            try
            {
                using var udp = new UdpClient(dnsServer.AddressFamily);
                udp.Connect(dnsServer, 53);
                await udp.SendAsync(query, timeout.Token);
                var response = await udp.ReceiveAsync(timeout.Token);
                if (TryReadResponse(response.Buffer, transactionId, queryName.TrimEnd('.'), out var records, out var unavailable))
                {
                    if (unavailable)
                    {
                        return new MinecraftSrvResolution([], true);
                    }

                    if (records.Count > 0)
                    {
                        return new MinecraftSrvResolution(OrderByPriorityAndWeight(records), false);
                    }

                    return new MinecraftSrvResolution([], false);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Try the next DNS server when this resolver does not answer promptly.
            }
            catch (SocketException)
            {
                // The system may have stale DNS configuration; try its next resolver.
            }
            catch (IOException)
            {
                // A malformed response from one resolver should not prevent direct fallback.
            }
        }

        return new MinecraftSrvResolution([], false);
    }

    private static IReadOnlyList<IPAddress> GetDnsServers()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(network => network.OperationalStatus == OperationalStatus.Up)
                .SelectMany(network => network.GetIPProperties().DnsAddresses)
                .Where(address => address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                .Distinct()
                .Take(6)
                .ToArray();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    private static byte[] BuildQuery(string queryName, ushort transactionId)
    {
        var asciiName = new IdnMapping().GetAscii(queryName.TrimEnd('.'));
        var bytes = new List<byte>(asciiName.Length + 18);
        var header = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(0, 2), transactionId);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2, 2), 0x0100);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4, 2), 1);
        bytes.AddRange(header);

        foreach (var label in asciiName.Split('.'))
        {
            var labelBytes = Encoding.ASCII.GetBytes(label);
            if (labelBytes.Length is < 1 or > 63)
            {
                throw new ArgumentException("The DNS name contains an invalid label.", nameof(queryName));
            }

            bytes.Add((byte)labelBytes.Length);
            bytes.AddRange(labelBytes);
        }

        bytes.Add(0);
        bytes.Add((byte)(DnsTypeSrv >> 8));
        bytes.Add((byte)DnsTypeSrv);
        bytes.Add((byte)(DnsClassInternet >> 8));
        bytes.Add((byte)DnsClassInternet);
        return bytes.ToArray();
    }

    private static bool TryReadResponse(
        byte[] message,
        ushort transactionId,
        string queryName,
        out List<SrvRecord> records,
        out bool serviceUnavailable)
    {
        records = [];
        serviceUnavailable = false;
        if (message.Length < 12 || BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(0, 2)) != transactionId)
        {
            return false;
        }

        var flags = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(2, 2));
        if ((flags & 0x8000) == 0 || (flags & 0x0200) != 0 || (flags & 0x000F) != 0)
        {
            return false;
        }

        var questionCount = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(4, 2));
        var answerCount = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(6, 2));
        var offset = 12;
        try
        {
            for (var index = 0; index < questionCount; index++)
            {
                _ = ReadName(message, ref offset);
                EnsureAvailable(message, offset, 4);
                offset += 4;
            }

            for (var index = 0; index < answerCount; index++)
            {
                var ownerName = ReadName(message, ref offset);
                EnsureAvailable(message, offset, 10);
                var type = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(offset, 2));
                var recordClass = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(offset + 2, 2));
                var dataLength = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(offset + 8, 2));
                offset += 10;
                EnsureAvailable(message, offset, dataLength);
                if (type == DnsTypeSrv && recordClass == DnsClassInternet &&
                    string.Equals(ownerName, queryName, StringComparison.OrdinalIgnoreCase) && dataLength >= 7)
                {
                    var dataEnd = offset + dataLength;
                    var priority = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(offset, 2));
                    var weight = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(offset + 2, 2));
                    var port = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(offset + 4, 2));
                    var targetOffset = offset + 6;
                    var target = ReadName(message, ref targetOffset).TrimEnd('.');
                    if (targetOffset > dataEnd) throw new IOException("The DNS SRV record is truncated.");
                    if (target == ".")
                    {
                        serviceUnavailable = true;
                    }
                    else if (target.Length > 0 && port > 0)
                    {
                        records.Add(new SrvRecord(priority, weight, port, target));
                    }
                }

                offset += dataLength;
            }

            return true;
        }
        catch (IOException)
        {
            records.Clear();
            serviceUnavailable = false;
            return false;
        }
    }

    private static IReadOnlyList<MinecraftServerEndpoint> OrderByPriorityAndWeight(IReadOnlyCollection<SrvRecord> records)
    {
        var ordered = new List<MinecraftServerEndpoint>(records.Count);
        foreach (var priorityGroup in records.GroupBy(record => record.Priority).OrderBy(group => group.Key))
        {
            var remaining = priorityGroup.ToList();
            while (remaining.Count > 0)
            {
                remaining = remaining.OrderBy(record => record.Weight == 0 ? 0 : 1).ToList();
                var totalWeight = remaining.Sum(record => (long)record.Weight);
                var selection = Random.Shared.NextInt64(0, totalWeight + 1);
                long runningWeight = 0;
                var selectedIndex = remaining.Count - 1;
                for (var index = 0; index < remaining.Count; index++)
                {
                    runningWeight += remaining[index].Weight;
                    if (runningWeight >= selection)
                    {
                        selectedIndex = index;
                        break;
                    }
                }

                var selected = remaining[selectedIndex];
                ordered.Add(new MinecraftServerEndpoint(selected.Target, selected.Port));
                remaining.RemoveAt(selectedIndex);
            }
        }

        return ordered;
    }

    private static string ReadName(byte[] message, ref int offset)
    {
        var labels = new List<string>();
        var cursor = offset;
        var jumped = false;
        var pointerCount = 0;
        while (true)
        {
            EnsureAvailable(message, cursor, 1);
            var length = message[cursor++];
            if (length == 0)
            {
                if (!jumped) offset = cursor;
                return string.Join('.', labels);
            }

            if ((length & 0xC0) == 0xC0)
            {
                EnsureAvailable(message, cursor, 1);
                var pointer = ((length & 0x3F) << 8) | message[cursor++];
                if (pointer >= message.Length || ++pointerCount > 32) throw new IOException("The DNS response contains an invalid name pointer.");
                if (!jumped) offset = cursor;
                cursor = pointer;
                jumped = true;
                continue;
            }

            if ((length & 0xC0) != 0 || length > 63)
            {
                throw new IOException("The DNS response contains an invalid label length.");
            }

            EnsureAvailable(message, cursor, length);
            labels.Add(Encoding.ASCII.GetString(message, cursor, length));
            cursor += length;
            if (!jumped) offset = cursor;
        }
    }

    private static void EnsureAvailable(byte[] message, int offset, int count)
    {
        if (offset < 0 || count < 0 || offset > message.Length - count)
        {
            throw new IOException("The DNS response is truncated.");
        }
    }

    private sealed record SrvRecord(ushort Priority, ushort Weight, ushort Port, string Target);
}
