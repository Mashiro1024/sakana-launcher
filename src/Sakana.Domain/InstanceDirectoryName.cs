namespace Sakana.Domain;

/// <summary>
/// Resolves and validates the on-disk directory name for a Minecraft instance.
/// Instance IDs remain stable database keys; the visible profile name is the folder name.
/// </summary>
public static class InstanceDirectoryName
{
    private static readonly char[] InvalidCharacters = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    public static string Validate(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length is < 1 or > 64 ||
            name is "." or ".." ||
            name.Equals(".deleting", StringComparison.OrdinalIgnoreCase) ||
            name.Any(character => char.IsControl(character) || InvalidCharacters.Contains(character)) ||
            name.EndsWith(' ') || name.EndsWith('.'))
        {
            throw new ArgumentException("实例名称会直接作为文件夹名称，请使用 1–64 个字符，并避免 Windows 不允许的字符、末尾空格或句点。", nameof(name));
        }

        var deviceName = name.Split('.', 2)[0];
        if (deviceName.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            deviceName.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            deviceName.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            deviceName.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            IsNumberedDeviceName(deviceName, "COM") ||
            IsNumberedDeviceName(deviceName, "LPT"))
        {
            throw new ArgumentException("该名称是 Windows 保留的设备名称，不能用作实例文件夹。", nameof(name));
        }

        return name;
    }

    public static string ForProfile(InstanceProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return ForName(profile.Name, profile.Id);
    }

    public static string ForName(string name, Guid fallbackId)
    {
        try
        {
            return Validate(name);
        }
        catch (ArgumentException)
        {
            // Keep older profiles with names Windows cannot use accessible until they are renamed.
            return fallbackId.ToString("N");
        }
    }

    private static bool IsNumberedDeviceName(string value, string prefix) =>
        value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
        value.Length == prefix.Length + 1 &&
        value[^1] is >= '1' and <= '9';
}
