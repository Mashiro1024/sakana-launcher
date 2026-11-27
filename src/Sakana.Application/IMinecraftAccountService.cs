using Sakana.Domain;

namespace Sakana.Application;

public interface IMinecraftAccountService
{
    Task<MicrosoftDeviceCodePrompt> BeginSignInAsync(string clientId, CancellationToken cancellationToken = default);
    Task<MinecraftAccountSession> CompleteSignInAsync(string clientId, MicrosoftDeviceCodePrompt prompt, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MinecraftAccountInfo>> GetAccountsAsync(string clientId, CancellationToken cancellationToken = default);
    Task SetActiveAccountAsync(string playerUuid, CancellationToken cancellationToken = default);
    Task RemoveAccountAsync(string playerUuid, CancellationToken cancellationToken = default);
    Task<MinecraftAccountSession?> GetCurrentSessionAsync(string clientId, CancellationToken cancellationToken = default);
    Task<MinecraftAccountSession?> GetSessionForAccountAsync(string clientId, string? playerUuid, CancellationToken cancellationToken = default);
    Task SignOutAsync(CancellationToken cancellationToken = default);
}

public interface IMicrosoftCredentialStore
{
    Task<string?> ReadAsync(CancellationToken cancellationToken = default);
    Task WriteAsync(string value, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}
