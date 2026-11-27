using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Sakana.Application;
using Sakana.Domain;

namespace Sakana.Minecraft;

public sealed class LocalMinecraftServerService(string dataDirectory) : ILocalMinecraftServerService
{
    private static readonly Regex HostAndPortPattern = new(
        "^(?<host>\\[[0-9A-Fa-f:.%]+\\]|[A-Za-z0-9.-]+)(?::(?<port>[0-9]{1,5}))?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<IReadOnlyList<MinecraftServerBookmark>> ListAsync(string instanceDirectoryName, CancellationToken cancellationToken = default)
    {
        var path = GetStorePath(instanceDirectoryName);
        if (!File.Exists(path))
        {
            return [];
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, useAsync: true);
        var items = await JsonSerializer.DeserializeAsync<List<MinecraftServerBookmark>>(stream, JsonOptions, cancellationToken) ?? [];
        return items
            .Where(item => item.Id != Guid.Empty && !string.IsNullOrWhiteSpace(item.Name) && TryNormalizeAddress(item.Address, out _))
            .OrderByDescending(item => item.IsPinned)
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public async Task<MinecraftServerBookmark> AddAsync(string instanceDirectoryName, string name, string address, CancellationToken cancellationToken = default)
    {
        var (normalizedName, normalizedAddress) = NormalizeBookmark(name, address);

        var path = GetStorePath(instanceDirectoryName);
        var items = (await ListAsync(instanceDirectoryName, cancellationToken)).ToList();
        if (items.Any(item => string.Equals(item.Address, normalizedAddress, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("这个服务器地址已经收藏。可在列表中直接快速启动。 ");
        }

        var bookmark = new MinecraftServerBookmark(Guid.NewGuid(), normalizedName, normalizedAddress);
        items.Add(bookmark);
        await SaveAsync(path, items, cancellationToken);
        return bookmark;
    }

    public async Task<MinecraftServerBookmark> UpdateAsync(
        string instanceDirectoryName,
        Guid bookmarkId,
        string name,
        string address,
        CancellationToken cancellationToken = default)
    {
        if (bookmarkId == Guid.Empty)
        {
            throw new ArgumentException("Choose a valid saved server.", nameof(bookmarkId));
        }

        var (normalizedName, normalizedAddress) = NormalizeBookmark(name, address);
        var path = GetStorePath(instanceDirectoryName);
        var items = (await ListAsync(instanceDirectoryName, cancellationToken)).ToList();
        var index = items.FindIndex(item => item.Id == bookmarkId);
        if (index < 0)
        {
            throw new KeyNotFoundException("This saved server no longer exists. Refresh the list and try again.");
        }

        if (items.Any(item => item.Id != bookmarkId && string.Equals(item.Address, normalizedAddress, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("这个服务器地址已经收藏。可在列表中直接快速启动。");
        }

        var updated = items[index] with { Name = normalizedName, Address = normalizedAddress };
        items[index] = updated;
        await SaveAsync(path, items, cancellationToken);
        return updated;
    }

    public async Task SetPinnedAsync(string instanceDirectoryName, Guid bookmarkId, bool isPinned, CancellationToken cancellationToken = default)
    {
        var path = GetStorePath(instanceDirectoryName);
        var items = (await ListAsync(instanceDirectoryName, cancellationToken)).ToList();
        var index = items.FindIndex(item => item.Id == bookmarkId);
        if (index < 0)
        {
            throw new KeyNotFoundException("This saved server no longer exists. Refresh the list and try again.");
        }

        if (items[index].IsPinned == isPinned) return;

        items[index] = items[index] with { IsPinned = isPinned };
        await SaveAsync(path, items, cancellationToken);
    }

    public async Task RemoveAsync(string instanceDirectoryName, Guid bookmarkId, CancellationToken cancellationToken = default)
    {
        var path = GetStorePath(instanceDirectoryName);
        var items = (await ListAsync(instanceDirectoryName, cancellationToken)).ToList();
        if (items.RemoveAll(item => item.Id == bookmarkId) == 0)
        {
            return;
        }

        await SaveAsync(path, items, cancellationToken);
    }

    private string GetStorePath(string instanceDirectoryName)
    {
        var safeName = InstanceDirectoryName.Validate(instanceDirectoryName);
        var instanceRoot = Path.Combine(dataDirectory, "instances", safeName);
        return Path.Combine(instanceRoot, "sakana-servers.json");
    }

    private static async Task SaveAsync(string path, IReadOnlyList<MinecraftServerBookmark> items, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp";
        await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 16 * 1024, useAsync: true))
        {
            await JsonSerializer.SerializeAsync(stream, items, JsonOptions, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        File.Move(temporaryPath, path, overwrite: true);
    }

    private static (string Name, string Address) NormalizeBookmark(string name, string address)
    {
        var normalizedName = (name ?? string.Empty).Trim();
        if (normalizedName.Length is < 1 or > 80 || normalizedName.Any(char.IsControl))
        {
            throw new ArgumentException("服务器名称需为 1–80 个字符。", nameof(name));
        }

        if (!TryNormalizeAddress(address, out var normalizedAddress))
        {
            throw new ArgumentException("输入服务器域名或 IP，可选填写端口，例如 play.example.net:25565。", nameof(address));
        }

        return (normalizedName, normalizedAddress);
    }

    private static bool TryNormalizeAddress(string? address, out string normalizedAddress)
    {
        normalizedAddress = string.Empty;
        var value = address?.Trim();
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace))
        {
            return false;
        }

        var match = HostAndPortPattern.Match(value);
        if (!match.Success)
        {
            return false;
        }

        var host = match.Groups["host"].Value;
        if (host.StartsWith("[", StringComparison.Ordinal))
        {
            if (!IPAddress.TryParse(host[1..^1], out var ipv6) || ipv6.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                return false;
            }
        }
        else if (!IPAddress.TryParse(host, out _) &&
                 (host.StartsWith('.') || host.EndsWith('.') || host.Contains("..", StringComparison.Ordinal) || host.Length > 253))
        {
            return false;
        }

        if (match.Groups["port"].Success &&
            (!int.TryParse(match.Groups["port"].Value, out var port) || port is < 1 or > 65535))
        {
            return false;
        }

        normalizedAddress = value;
        return true;
    }
}
