using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Sakana.Application;

namespace Sakana.Infrastructure;

/// <summary>Stores the developer's CurseForge API key encrypted for the current Windows user.</summary>
public sealed class WindowsDpapiCurseForgeApiKeyStore : ICurseForgeApiKeyStore
{
    private const int CryptProtectUiForbidden = 0x1;
    private readonly string _path;

    public WindowsDpapiCurseForgeApiKeyStore(string dataDirectory)
    {
        var credentialDirectory = Path.Combine(dataDirectory, "credentials");
        Directory.CreateDirectory(credentialDirectory);
        _path = Path.Combine(credentialDirectory, "curseforge-api-key.bin");
    }

    public async Task<string?> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path)) return null;
        var encrypted = await File.ReadAllBytesAsync(_path, cancellationToken);
        var plaintext = Unprotect(encrypted);
        try
        {
            var value = Encoding.UTF8.GetString(plaintext).Trim();
            return value.Length == 0 ? null : value;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(encrypted);
        }
    }

    public async Task WriteAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        var plaintext = Encoding.UTF8.GetBytes(apiKey.Trim());
        byte[]? encrypted = null;
        var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            encrypted = Protect(plaintext);
            await File.WriteAllBytesAsync(temporaryPath, encrypted, cancellationToken);
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (encrypted is not null) CryptographicOperations.ZeroMemory(encrypted);
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(_path)) File.Delete(_path);
        return Task.CompletedTask;
    }

    private static byte[] Protect(byte[] plaintext)
    {
        var input = CreateBlob(plaintext);
        try
        {
            if (!CryptProtectData(ref input, "Sakana Launcher CurseForge API key", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out var output))
            {
                throw new CryptographicException(Marshal.GetLastWin32Error());
            }

            return CopyAndFree(output);
        }
        finally
        {
            FreeInput(input);
        }
    }

    private static byte[] Unprotect(byte[] encrypted)
    {
        var input = CreateBlob(encrypted);
        try
        {
            if (!CryptUnprotectData(ref input, out var description, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out var output))
            {
                throw new CryptographicException(Marshal.GetLastWin32Error());
            }

            if (description != IntPtr.Zero) LocalFree(description);
            return CopyAndFree(output);
        }
        finally
        {
            FreeInput(input);
        }
    }

    private static DataBlob CreateBlob(byte[] bytes)
    {
        var memory = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, memory, bytes.Length);
        return new DataBlob { Size = bytes.Length, Data = memory };
    }

    private static byte[] CopyAndFree(DataBlob blob)
    {
        try
        {
            var bytes = new byte[blob.Size];
            Marshal.Copy(blob.Data, bytes, 0, blob.Size);
            return bytes;
        }
        finally
        {
            if (blob.Data != IntPtr.Zero)
            {
                if (blob.Size > 0) Marshal.Copy(new byte[blob.Size], 0, blob.Data, blob.Size);
                LocalFree(blob.Data);
            }
        }
    }

    private static void FreeInput(DataBlob blob)
    {
        if (blob.Data == IntPtr.Zero) return;
        if (blob.Size > 0) Marshal.Copy(new byte[blob.Size], 0, blob.Data, blob.Size);
        Marshal.FreeHGlobal(blob.Data);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob input,
        string description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob input,
        out IntPtr description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
