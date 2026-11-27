using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Sakana.Application;
using Sakana.Domain;

namespace Sakana.Minecraft;

public sealed class MinecraftServerStatusService : IMinecraftServerStatusService
{
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(4);
    private const int MaximumPacketBytes = 1024 * 1024;

    public async Task<MinecraftServerStatus> PingAsync(string address, CancellationToken cancellationToken = default)
    {
        if (!TryParseAddress(address, out var host, out var port, out var hasExplicitPort))
        {
            return Offline("服务器地址无效");
        }

        IReadOnlyList<MinecraftServerEndpoint> endpoints = [new MinecraftServerEndpoint(host, port)];
        if (!hasExplicitPort && !IPAddress.TryParse(host, out _))
        {
            try
            {
                var srv = await MinecraftSrvResolver.ResolveAsync(host, cancellationToken);
                if (srv.ServiceUnavailable)
                {
                    return Offline("此域名未发布可用的 Minecraft 服务器地址");
                }

                if (srv.Endpoints.Count > 0)
                {
                    endpoints = srv.Endpoints;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is SocketException or IOException)
            {
                // SRV is optional; continue with the supplied hostname and default port.
            }
        }

        var lastFailure = Offline("无法连接到服务器");
        foreach (var endpoint in endpoints.Take(4))
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ConnectionTimeout);
            try
            {
                var result = await PingEndpointAsync(endpoint, timeout.Token);
                if (result.IsOnline)
                {
                    return result;
                }

                lastFailure = result;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lastFailure = Offline("连接超时");
            }
            catch (SocketException)
            {
                lastFailure = Offline("无法连接到服务器");
            }
            catch (IOException)
            {
                lastFailure = Offline("服务器未响应或返回了无效数据");
            }
            catch (JsonException)
            {
                lastFailure = Offline("服务器状态数据格式无效");
            }
        }

        return lastFailure;
    }

    private static async Task<MinecraftServerStatus> PingEndpointAsync(MinecraftServerEndpoint endpoint, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(endpoint.Host, endpoint.Port, cancellationToken);
        await using var stream = client.GetStream();

        var handshake = new List<byte>();
        WriteVarInt(handshake, 0);
        WriteVarInt(handshake, -1);
        WriteString(handshake, endpoint.Host);
        var portBytes = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(portBytes, (ushort)endpoint.Port);
        handshake.AddRange(portBytes);
        WriteVarInt(handshake, 1);
        await WritePacketAsync(stream, handshake, cancellationToken);

        var statusRequest = new List<byte>();
        WriteVarInt(statusRequest, 0);
        await WritePacketAsync(stream, statusRequest, cancellationToken);

        var statusPacket = await ReadPacketAsync(stream, cancellationToken);
        var packetOffset = 0;
        var packetId = ReadVarInt(statusPacket, ref packetOffset);
        if (packetId != 0)
        {
            return Offline("服务器返回了无法识别的状态");
        }

        var jsonLength = ReadVarInt(statusPacket, ref packetOffset);
        if (jsonLength is < 2 or > MaximumPacketBytes || packetOffset + jsonLength > statusPacket.Length)
        {
            return Offline("服务器状态数据格式无效");
        }

        using var json = JsonDocument.Parse(statusPacket.AsMemory(packetOffset, jsonLength));
        var status = ParseStatus(json.RootElement);

        var pingPayload = new byte[8];
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        BinaryPrimitives.WriteInt64BigEndian(pingPayload, timestamp);
        var pingPacket = new List<byte>();
        WriteVarInt(pingPacket, 1);
        pingPacket.AddRange(pingPayload);
        var started = Stopwatch.GetTimestamp();
        await WritePacketAsync(stream, pingPacket, cancellationToken);

        var pongPacket = await ReadPacketAsync(stream, cancellationToken);
        var pongOffset = 0;
        var pongId = ReadVarInt(pongPacket, ref pongOffset);
        if (pongId != 1 || pongPacket.Length - pongOffset < sizeof(long) ||
            BinaryPrimitives.ReadInt64BigEndian(pongPacket.AsSpan(pongOffset, sizeof(long))) != timestamp)
        {
            return Offline("服务器没有返回有效的延迟响应");
        }

        var latency = (int)Math.Clamp(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 0, int.MaxValue);
        return status with { IsOnline = true, LatencyMilliseconds = latency };
    }

    private static MinecraftServerStatus ParseStatus(JsonElement root)
    {
        var versionName = root.TryGetProperty("version", out var version) && version.TryGetProperty("name", out var name)
            ? name.GetString()
            : null;
        int? onlinePlayers = null;
        int? maxPlayers = null;
        if (root.TryGetProperty("players", out var players))
        {
            if (players.TryGetProperty("online", out var online) && online.TryGetInt32(out var onlineCount)) onlinePlayers = onlineCount;
            if (players.TryGetProperty("max", out var max) && max.TryGetInt32(out var maxCount)) maxPlayers = maxCount;
        }

        var description = root.TryGetProperty("description", out var descriptionNode)
            ? ReadTextComponent(descriptionNode).Trim()
            : string.Empty;
        if (description.Length > 240) description = description[..237] + "…";

        return new MinecraftServerStatus(true, null, versionName, onlinePlayers, maxPlayers, description, null);
    }

    private static string ReadTextComponent(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return element.GetString() ?? string.Empty;
            case JsonValueKind.Array:
                return string.Concat(element.EnumerateArray().Select(ReadTextComponent));
            case JsonValueKind.Object:
                var builder = new StringBuilder();
                if (element.TryGetProperty("text", out var text)) builder.Append(ReadTextComponent(text));
                if (element.TryGetProperty("extra", out var extra)) builder.Append(ReadTextComponent(extra));
                if (element.TryGetProperty("with", out var with)) builder.Append(ReadTextComponent(with));
                return builder.ToString();
            default:
                return string.Empty;
        }
    }

    private static bool TryParseAddress(string? address, out string host, out int port, out bool hasExplicitPort)
    {
        host = string.Empty;
        port = 25565;
        hasExplicitPort = false;
        var value = address?.Trim();
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace)) return false;

        if (value.StartsWith("[", StringComparison.Ordinal))
        {
            var closeBracket = value.IndexOf(']');
            if (closeBracket < 2) return false;
            host = value[1..closeBracket];
            if (closeBracket + 1 < value.Length &&
                (value[closeBracket + 1] != ':' || !TryParsePort(value[(closeBracket + 2)..], out port))) return false;
            hasExplicitPort = closeBracket + 1 < value.Length;
            return true;
        }

        var colon = value.LastIndexOf(':');
        if (colon > 0 && value.IndexOf(':') == colon)
        {
            if (!TryParsePort(value[(colon + 1)..], out port)) return false;
            value = value[..colon];
            hasExplicitPort = true;
        }

        host = value;
        return host.Length is > 0 and <= 253;
    }

    private static bool TryParsePort(string value, out int port)
    {
        return int.TryParse(value, out port) && port is >= 1 and <= 65535;
    }

    private static MinecraftServerStatus Offline(string message) => new(false, null, null, null, null, string.Empty, message);

    private static void WriteString(List<byte> buffer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteVarInt(buffer, bytes.Length);
        buffer.AddRange(bytes);
    }

    private static void WriteVarInt(List<byte> buffer, int value)
    {
        var remaining = unchecked((uint)value);
        while ((remaining & ~0x7Fu) != 0)
        {
            buffer.Add((byte)((remaining & 0x7F) | 0x80));
            remaining >>= 7;
        }

        buffer.Add((byte)remaining);
    }

    private static async Task WritePacketAsync(NetworkStream stream, List<byte> payload, CancellationToken cancellationToken)
    {
        var packet = new List<byte>(payload.Count + 5);
        WriteVarInt(packet, payload.Count);
        packet.AddRange(payload);
        await stream.WriteAsync(packet.ToArray(), cancellationToken);
    }

    private static async Task<byte[]> ReadPacketAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var packetLength = await ReadVarIntAsync(stream, cancellationToken);
        if (packetLength is < 1 or > MaximumPacketBytes)
        {
            throw new IOException("Minecraft server status packet length is invalid.");
        }

        var packet = new byte[packetLength];
        await stream.ReadExactlyAsync(packet, cancellationToken);
        return packet;
    }

    private static async Task<int> ReadVarIntAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        uint result = 0;
        for (var index = 0; index < 5; index++)
        {
            var current = new byte[1];
            await stream.ReadExactlyAsync(current, cancellationToken);
            result |= (uint)(current[0] & 0x7F) << (index * 7);
            if ((current[0] & 0x80) == 0)
            {
                return unchecked((int)result);
            }
        }

        throw new IOException("Minecraft server sent an invalid VarInt.");
    }

    private static int ReadVarInt(byte[] buffer, ref int offset)
    {
        uint result = 0;
        for (var index = 0; index < 5; index++)
        {
            if (offset >= buffer.Length) throw new IOException("Minecraft server sent a truncated packet.");
            var current = buffer[offset++];
            result |= (uint)(current & 0x7F) << (index * 7);
            if ((current & 0x80) == 0) return unchecked((int)result);
        }

        throw new IOException("Minecraft server sent an invalid VarInt.");
    }
}
