using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sakana.Application;
using Sakana.Domain;

namespace Sakana.Minecraft;

/// <summary>
/// Implements the Microsoft device-code, Xbox Live/XSTS, and Minecraft Services account exchange.
/// The Minecraft Services application registration must be authorized for the configured client ID.
/// </summary>
public sealed class MicrosoftMinecraftAccountService(
    HttpClient httpClient,
    IMicrosoftCredentialStore credentialStore) : IMinecraftAccountService
{
    private static readonly Uri DeviceCodeEndpoint = new("https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode");
    private static readonly Uri TokenEndpoint = new("https://login.microsoftonline.com/consumers/oauth2/v2.0/token");
    private static readonly Uri XboxUserEndpoint = new("https://user.auth.xboxlive.com/user/authenticate");
    private static readonly Uri XboxXstsEndpoint = new("https://xsts.auth.xboxlive.com/xsts/authorize");
    private static readonly Uri MinecraftLoginEndpoint = new("https://api.minecraftservices.com/authentication/login_with_xbox");
    private static readonly Uri MinecraftEntitlementsEndpoint = new("https://api.minecraftservices.com/entitlements/mcstore");
    private static readonly Uri MinecraftProfileEndpoint = new("https://api.minecraftservices.com/minecraft/profile");
    private static readonly Uri MojangSessionProfileEndpoint = new("https://sessionserver.mojang.com/session/minecraft/profile/");
    private static readonly JsonSerializerOptions ApiJsonOptions = new()
    {
        PropertyNamingPolicy = null,
        PropertyNameCaseInsensitive = true
    };

    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private MinecraftAccountSession? _currentSession;
    private DateTimeOffset _sessionExpiresAtUtc;
    private string? _currentClientId;
    private string? _currentPlayerUuid;

    public async Task<MicrosoftDeviceCodePrompt> BeginSignInAsync(
        string clientId,
        CancellationToken cancellationToken = default)
    {
        var normalizedClientId = ValidateClientId(clientId);
        using var response = await httpClient.PostAsync(
            DeviceCodeEndpoint,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = normalizedClientId,
                ["scope"] = "XboxLive.signin offline_access"
            }),
            cancellationToken);

        var payload = await ReadJsonAsync<DeviceCodeResponse>(response, "Microsoft device-code sign-in", cancellationToken);
        if (string.IsNullOrWhiteSpace(payload.DeviceCode) || string.IsNullOrWhiteSpace(payload.UserCode) ||
            string.IsNullOrWhiteSpace(payload.VerificationUri))
        {
            throw new InvalidDataException("Microsoft returned an incomplete device-code challenge.");
        }

        var expiresInSeconds = Math.Clamp(payload.ExpiresIn, 60, 1800);
        return new MicrosoftDeviceCodePrompt(
            payload.DeviceCode,
            payload.UserCode,
            payload.VerificationUri,
            payload.Message ?? "Open the verification page and enter the displayed code.",
            DateTimeOffset.UtcNow.AddSeconds(expiresInSeconds),
            Math.Clamp(payload.Interval, 1, 30));
    }

    public async Task<MinecraftAccountSession> CompleteSignInAsync(
        string clientId,
        MicrosoftDeviceCodePrompt prompt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        var normalizedClientId = ValidateClientId(clientId);
        var oauth = await PollForOAuthTokenAsync(normalizedClientId, prompt, cancellationToken);
        if (string.IsNullOrWhiteSpace(oauth.RefreshToken))
        {
            throw new InvalidDataException("Microsoft did not issue a refresh token for Sakana. Check the application's offline_access configuration.");
        }

        var account = await CreateMinecraftSessionAsync(normalizedClientId, oauth.AccessToken, oauth.ExpiresIn, cancellationToken);
        await _sessionLock.WaitAsync(cancellationToken);
        try
        {
            var credentialStoreState = await ReadCredentialStoreAsync(cancellationToken);
            UpsertCredential(credentialStoreState, normalizedClientId, oauth.RefreshToken, account.Session);
            credentialStoreState.ActivePlayerUuid = account.Session.PlayerUuid;
            await SaveCredentialStoreAsync(credentialStoreState, cancellationToken);
            _currentClientId = normalizedClientId;
            _currentSession = account.Session;
            _currentPlayerUuid = account.Session.PlayerUuid;
            _sessionExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(account.ExpiresInSeconds);
        }
        finally
        {
            _sessionLock.Release();
        }

        return account.Session;
    }

    public async Task<IReadOnlyList<MinecraftAccountInfo>> GetAccountsAsync(
        string clientId,
        CancellationToken cancellationToken = default)
    {
        _ = ValidateClientId(clientId);
        await _sessionLock.WaitAsync(cancellationToken);
        try
        {
            var state = await ReadCredentialStoreAsync(cancellationToken);
            var activeCredential = FindActiveCredential(state);
            return state.Accounts
                .Select(account => new MinecraftAccountInfo(
                    string.IsNullOrWhiteSpace(account.PlayerName) ? "待验证的账户" : account.PlayerName,
                    account.PlayerUuid ?? string.Empty,
                    ReferenceEquals(account, activeCredential),
                    string.IsNullOrWhiteSpace(account.PlayerUuid) || string.IsNullOrWhiteSpace(account.PlayerName)))
                .ToArray();
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    public async Task SetActiveAccountAsync(string playerUuid, CancellationToken cancellationToken = default)
    {
        var normalizedUuid = playerUuid?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedUuid))
        {
            throw new ArgumentException("Choose a verified Minecraft account.", nameof(playerUuid));
        }

        await _sessionLock.WaitAsync(cancellationToken);
        try
        {
            var state = await ReadCredentialStoreAsync(cancellationToken);
            if (!state.Accounts.Any(account => string.Equals(account.PlayerUuid, normalizedUuid, StringComparison.OrdinalIgnoreCase)))
            {
                throw new KeyNotFoundException("The selected Minecraft account is no longer saved.");
            }

            state.ActivePlayerUuid = normalizedUuid;
            await SaveCredentialStoreAsync(state, cancellationToken);
            _currentSession = null;
            _currentClientId = null;
            _currentPlayerUuid = null;
            _sessionExpiresAtUtc = DateTimeOffset.MinValue;
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    public async Task RemoveAccountAsync(string playerUuid, CancellationToken cancellationToken = default)
    {
        var normalizedUuid = playerUuid?.Trim() ?? string.Empty;
        await _sessionLock.WaitAsync(cancellationToken);
        try
        {
            var state = await ReadCredentialStoreAsync(cancellationToken);
            var removed = normalizedUuid.Length == 0
                ? state.Accounts.RemoveAll(account => string.IsNullOrWhiteSpace(account.PlayerUuid)) > 0
                : state.Accounts.RemoveAll(account => string.Equals(account.PlayerUuid, normalizedUuid, StringComparison.OrdinalIgnoreCase)) > 0;
            if (!removed)
            {
                throw new KeyNotFoundException("The selected Microsoft account is no longer saved.");
            }

            if (state.Accounts.Count == 0)
            {
                await credentialStore.ClearAsync(cancellationToken);
                state.ActivePlayerUuid = null;
            }
            else if (normalizedUuid.Length == 0 || string.Equals(state.ActivePlayerUuid, normalizedUuid, StringComparison.OrdinalIgnoreCase))
            {
                state.ActivePlayerUuid = state.Accounts[0].PlayerUuid;
                await SaveCredentialStoreAsync(state, cancellationToken);
            }
            else
            {
                await SaveCredentialStoreAsync(state, cancellationToken);
            }

            if (normalizedUuid.Length == 0 || string.Equals(_currentPlayerUuid, normalizedUuid, StringComparison.OrdinalIgnoreCase))
            {
                _currentSession = null;
                _currentClientId = null;
                _currentPlayerUuid = null;
                _sessionExpiresAtUtc = DateTimeOffset.MinValue;
            }
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    public Task<MinecraftAccountSession?> GetCurrentSessionAsync(
        string clientId,
        CancellationToken cancellationToken = default) =>
        GetSessionForAccountAsync(clientId, playerUuid: null, cancellationToken: cancellationToken);

    public async Task<MinecraftAccountSession?> GetSessionForAccountAsync(
        string clientId,
        string? playerUuid,
        CancellationToken cancellationToken = default)
    {
        var normalizedClientId = ValidateClientId(clientId);
        var normalizedPlayerUuid = string.IsNullOrWhiteSpace(playerUuid) ? null : playerUuid.Trim();
        await _sessionLock.WaitAsync(cancellationToken);
        try
        {
            var state = await ReadCredentialStoreAsync(cancellationToken);
            var savedCredential = normalizedPlayerUuid is null
                ? FindActiveCredential(state)
                : state.Accounts.FirstOrDefault(account => string.Equals(
                    account.PlayerUuid,
                    normalizedPlayerUuid,
                    StringComparison.OrdinalIgnoreCase));
            if (savedCredential is null)
            {
                if (normalizedPlayerUuid is not null)
                {
                    throw new KeyNotFoundException("The Microsoft account selected for this instance is no longer saved. Choose another account in the instance launch settings.");
                }

                _currentSession = null;
                _currentClientId = null;
                _currentPlayerUuid = null;
                _sessionExpiresAtUtc = DateTimeOffset.MinValue;
                return null;
            }

            if (_currentSession is not null &&
                string.Equals(_currentPlayerUuid, savedCredential.PlayerUuid, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_currentClientId, normalizedClientId, StringComparison.OrdinalIgnoreCase) &&
                _sessionExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(2))
            {
                return _currentSession;
            }

            if (!string.Equals(savedCredential.ClientId, normalizedClientId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The configured Microsoft client ID changed. Sign in again with the current ID.");
            }

            OAuthTokenResponse oauth;
            try
            {
                oauth = await RefreshOAuthTokenAsync(normalizedClientId, savedCredential.RefreshToken, cancellationToken);
            }
            catch (MicrosoftOAuthException exception) when (exception.Error == "invalid_grant")
            {
                var removedAccountWasActive = string.Equals(
                    state.ActivePlayerUuid,
                    savedCredential.PlayerUuid,
                    StringComparison.OrdinalIgnoreCase);
                state.Accounts.Remove(savedCredential);
                if (state.Accounts.Count == 0)
                {
                    await credentialStore.ClearAsync(cancellationToken);
                }
                else if (removedAccountWasActive)
                {
                    state.ActivePlayerUuid = state.Accounts[0].PlayerUuid;
                    await SaveCredentialStoreAsync(state, cancellationToken);
                }
                else
                {
                    await SaveCredentialStoreAsync(state, cancellationToken);
                }
                _currentSession = null;
                _currentClientId = null;
                _currentPlayerUuid = null;
                return null;
            }

            var rotatedRefreshToken = string.IsNullOrWhiteSpace(oauth.RefreshToken)
                ? savedCredential.RefreshToken
                : oauth.RefreshToken;
            var account = await CreateMinecraftSessionAsync(normalizedClientId, oauth.AccessToken, oauth.ExpiresIn, cancellationToken);
            UpsertCredential(state, normalizedClientId, rotatedRefreshToken, account.Session);
            if (normalizedPlayerUuid is null)
            {
                state.ActivePlayerUuid = account.Session.PlayerUuid;
            }
            await SaveCredentialStoreAsync(state, cancellationToken);
            _currentClientId = normalizedClientId;
            _currentSession = account.Session;
            _currentPlayerUuid = account.Session.PlayerUuid;
            _sessionExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(account.ExpiresInSeconds);
            return _currentSession;
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        await _sessionLock.WaitAsync(cancellationToken);
        try
        {
            var state = await ReadCredentialStoreAsync(cancellationToken);
            var activeCredential = FindActiveCredential(state);
            if (activeCredential is not null)
            {
                state.Accounts.Remove(activeCredential);
            }

            if (state.Accounts.Count == 0)
            {
                await credentialStore.ClearAsync(cancellationToken);
                state.ActivePlayerUuid = null;
            }
            else
            {
                state.ActivePlayerUuid = state.Accounts[0].PlayerUuid;
                await SaveCredentialStoreAsync(state, cancellationToken);
            }

            _currentSession = null;
            _currentClientId = null;
            _currentPlayerUuid = null;
            _sessionExpiresAtUtc = DateTimeOffset.MinValue;
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private async Task<OAuthTokenResponse> PollForOAuthTokenAsync(
        string clientId,
        MicrosoftDeviceCodePrompt prompt,
        CancellationToken cancellationToken)
    {
        var deadline = prompt.ExpiresAtUtc;
        var interval = prompt.PollingIntervalSeconds;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), cancellationToken);
            using var response = await httpClient.PostAsync(
                TokenEndpoint,
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                    ["client_id"] = clientId,
                    ["device_code"] = prompt.DeviceCode
                }),
                cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return await ReadJsonAsync<OAuthTokenResponse>(response, "Microsoft token exchange", cancellationToken);
            }

            var oauthError = await ReadOAuthErrorAsync(response, cancellationToken);
            switch (oauthError.Error)
            {
                case "authorization_pending":
                    continue;
                case "slow_down":
                    interval = Math.Min(interval + 5, 60);
                    continue;
                case "authorization_declined":
                    throw new InvalidOperationException("Microsoft account sign-in was declined.");
                case "expired_token":
                case "bad_verification_code":
                    throw new InvalidOperationException("The Microsoft sign-in code expired. Start sign-in again.");
                default:
                    throw new MicrosoftOAuthException(oauthError.Error ?? "oauth_error", "Microsoft account sign-in could not be completed.");
            }
        }

        throw new TimeoutException("The Microsoft sign-in code expired. Start sign-in again.");
    }

    private async Task<OAuthTokenResponse> RefreshOAuthTokenAsync(
        string clientId,
        string refreshToken,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync(
            TokenEndpoint,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["scope"] = "XboxLive.signin offline_access"
            }),
            cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            var token = await ReadJsonAsync<OAuthTokenResponse>(response, "Microsoft token refresh", cancellationToken);
            if (string.IsNullOrWhiteSpace(token.AccessToken))
            {
                throw new InvalidDataException("Microsoft returned an empty access token during refresh.");
            }

            return token;
        }

        var error = await ReadOAuthErrorAsync(response, cancellationToken);
        throw new MicrosoftOAuthException(error.Error ?? "oauth_error", "Microsoft account access needs to be renewed.");
    }

    private async Task<(MinecraftAccountSession Session, int ExpiresInSeconds)> CreateMinecraftSessionAsync(
        string clientId,
        string microsoftAccessToken,
        int microsoftExpiresInSeconds,
        CancellationToken cancellationToken)
    {
        var xboxUser = await PostJsonAsync<XboxTokenResponse>(XboxUserEndpoint, new
        {
            Properties = new
            {
                AuthMethod = "RPS",
                SiteName = "user.auth.xboxlive.com",
                RpsTicket = $"d={microsoftAccessToken}"
            },
            RelyingParty = "http://auth.xboxlive.com",
            TokenType = "JWT"
        }, "Xbox Live authentication", cancellationToken);

        var xsts = await PostJsonAsync<XboxTokenResponse>(XboxXstsEndpoint, new
        {
            Properties = new
            {
                SandboxId = "RETAIL",
                UserTokens = new[] { xboxUser.Token }
            },
            RelyingParty = "rp://api.minecraftservices.com/",
            TokenType = "JWT"
        }, "Xbox XSTS authorization", cancellationToken);

        var claims = xsts.DisplayClaims?.Xui?.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(claims?.UserHash))
        {
            throw new InvalidDataException("Xbox XSTS did not return a user identity claim.");
        }

        var minecraftToken = await PostJsonAsync<MinecraftTokenResponse>(MinecraftLoginEndpoint, new
        {
            identityToken = $"XBL3.0 x={claims.UserHash};{xsts.Token}",
            ensureLegacyEnabled = true
        }, "Minecraft Services account exchange", cancellationToken);

        using var entitlementRequest = new HttpRequestMessage(HttpMethod.Get, MinecraftEntitlementsEndpoint);
        entitlementRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", minecraftToken.AccessToken);
        using var entitlementResponse = await httpClient.SendAsync(entitlementRequest, cancellationToken);
        var entitlements = await ReadJsonAsync<MinecraftEntitlementsResponse>(entitlementResponse, "Minecraft ownership check", cancellationToken);
        if (entitlements.Items is null || !entitlements.Items.Any(item =>
                item.Name is "game_minecraft" or "product_minecraft"))
        {
            throw new InvalidOperationException("This Microsoft account does not have a Minecraft Java Edition entitlement.");
        }

        using var profileRequest = new HttpRequestMessage(HttpMethod.Get, MinecraftProfileEndpoint);
        profileRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", minecraftToken.AccessToken);
        using var profileResponse = await httpClient.SendAsync(profileRequest, cancellationToken);
        var profile = await ReadJsonAsync<MinecraftProfileResponse>(profileResponse, "Minecraft profile lookup", cancellationToken);
        if (string.IsNullOrWhiteSpace(profile.Id) || string.IsNullOrWhiteSpace(profile.Name))
        {
            throw new InvalidDataException("Minecraft Services returned an incomplete player profile.");
        }

        var activeSkin = profile.Skins?.FirstOrDefault(skin =>
            string.Equals(skin.State, "ACTIVE", StringComparison.OrdinalIgnoreCase));
        var skinTextureUrl = TryGetOfficialSkinTextureUrl(activeSkin?.Url);
        var skinVariant = activeSkin?.Variant;
        if (skinTextureUrl is null)
        {
            var sessionSkin = await TryGetSkinFromSessionProfileAsync(profile.Id, cancellationToken);
            skinTextureUrl = sessionSkin.TextureUrl;
            skinVariant ??= sessionSkin.Variant;
        }

        var session = new MinecraftAccountSession(
            profile.Name,
            profile.Id,
            minecraftToken.AccessToken,
            clientId,
            "msa",
            claims.XboxId,
            userPropertiesJson: "{}",
            skinTextureUrl: skinTextureUrl,
            skinVariant: skinVariant);
        return (session, Math.Min(minecraftToken.ExpiresIn, microsoftExpiresInSeconds));
    }

    private async Task<(string? TextureUrl, string? Variant)> TryGetSkinFromSessionProfileAsync(
        string playerUuid,
        CancellationToken cancellationToken)
    {
        var normalizedUuid = playerUuid.Replace("-", string.Empty, StringComparison.Ordinal);
        if (normalizedUuid.Length != 32 || !normalizedUuid.All(Uri.IsHexDigit))
        {
            return (null, null);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var response = await httpClient.GetAsync(
                new Uri(MojangSessionProfileEndpoint, normalizedUuid),
                timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return (null, null);
            }

            var profileJson = await response.Content.ReadAsStringAsync(timeout.Token);
            var sessionProfile = JsonSerializer.Deserialize<MojangSessionProfileResponse>(profileJson, ApiJsonOptions);
            var encodedTextures = sessionProfile?.Properties?.FirstOrDefault(property =>
                string.Equals(property.Name, "textures", StringComparison.OrdinalIgnoreCase))?.Value;
            if (string.IsNullOrWhiteSpace(encodedTextures))
            {
                return (null, null);
            }

            var textureJson = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encodedTextures));
            var texturePayload = JsonSerializer.Deserialize<MojangTexturePayload>(textureJson, ApiJsonOptions);
            var skin = texturePayload?.Textures?.Skin;
            var textureUrl = TryGetOfficialSkinTextureUrl(skin?.Url);
            if (textureUrl is null)
            {
                return (null, null);
            }

            var variant = string.Equals(skin?.Metadata?.Model, "slim", StringComparison.OrdinalIgnoreCase)
                ? "SLIM"
                : "CLASSIC";
            return (textureUrl, variant);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, null);
        }
        catch (HttpRequestException)
        {
            return (null, null);
        }
        catch (JsonException)
        {
            return (null, null);
        }
        catch (FormatException)
        {
            return (null, null);
        }
    }

    private static string? TryGetOfficialSkinTextureUrl(string? rawUrl)
    {
        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
            !uri.IsDefaultPort ||
            !string.Equals(uri.Host, "textures.minecraft.net", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Minecraft profile APIs commonly return the texture host over HTTP.
        // Keep the host allowlist and upgrade it before exposing the URL to the UI.
        return new UriBuilder(uri)
        {
            Scheme = Uri.UriSchemeHttps,
            Port = -1
        }.Uri.AbsoluteUri;
    }

    private async Task<T> PostJsonAsync<T>(Uri endpoint, object body, string operation, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync(endpoint, JsonContent.Create(body, options: ApiJsonOptions), cancellationToken);
        return await ReadJsonAsync<T>(response, operation, cancellationToken);
    }

    private static async Task<T> ReadJsonAsync<T>(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var apiError = TryDeserialize<ApiErrorResponse>(content);
            if (response.StatusCode == HttpStatusCode.Forbidden &&
                operation.Contains("Minecraft Services", StringComparison.OrdinalIgnoreCase))
            {
                throw new MinecraftClientIdNotAuthorizedException();
            }

            var detail = apiError?.ErrorMessage ?? apiError?.Message;
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
                ? $"{operation} failed with HTTP {(int)response.StatusCode}."
                : $"{operation} failed: {detail}");
        }

        return JsonSerializer.Deserialize<T>(content, ApiJsonOptions)
            ?? throw new InvalidDataException($"{operation} returned an empty response.");
    }

    private static async Task<OAuthErrorResponse> ReadOAuthErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        return TryDeserialize<OAuthErrorResponse>(content) ?? new OAuthErrorResponse("oauth_error", null);
    }

    private async Task<SavedMicrosoftCredentialStore> ReadCredentialStoreAsync(CancellationToken cancellationToken)
    {
        var value = await credentialStore.ReadAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(value))
        {
            return new SavedMicrosoftCredentialStore();
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("accounts", out _))
            {
                return JsonSerializer.Deserialize<SavedMicrosoftCredentialStore>(value, ApiJsonOptions)
                    ?? throw new InvalidDataException("Saved Microsoft account data is empty.");
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Saved Microsoft account data could not be read.", exception);
        }

        var legacyCredential = TryDeserialize<SavedMicrosoftCredential>(value)
            ?? throw new InvalidDataException("Saved Microsoft account data has an unsupported format.");
        var migrated = new SavedMicrosoftCredentialStore();
        migrated.Accounts.Add(legacyCredential);
        migrated.ActivePlayerUuid = legacyCredential.PlayerUuid;
        return migrated;
    }

    private Task SaveCredentialStoreAsync(SavedMicrosoftCredentialStore state, CancellationToken cancellationToken) =>
        credentialStore.WriteAsync(JsonSerializer.Serialize(state, ApiJsonOptions), cancellationToken);

    private static SavedMicrosoftCredential? FindActiveCredential(SavedMicrosoftCredentialStore state)
    {
        if (state.Accounts.Count == 0)
        {
            return null;
        }

        return state.Accounts.FirstOrDefault(account =>
                   !string.IsNullOrWhiteSpace(state.ActivePlayerUuid) &&
                   string.Equals(account.PlayerUuid, state.ActivePlayerUuid, StringComparison.OrdinalIgnoreCase))
               ?? state.Accounts[0];
    }

    private static void UpsertCredential(
        SavedMicrosoftCredentialStore state,
        string clientId,
        string refreshToken,
        MinecraftAccountSession session)
    {
        state.Accounts.RemoveAll(account => string.Equals(account.PlayerUuid, session.PlayerUuid, StringComparison.OrdinalIgnoreCase));
        state.Accounts.Add(new SavedMicrosoftCredential(clientId, refreshToken, session.PlayerUuid, session.PlayerName));
    }

    private static T? TryDeserialize<T>(string content)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(content, ApiJsonOptions);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static string ValidateClientId(string clientId)
    {
        if (!Guid.TryParse(clientId?.Trim(), out var parsed))
        {
            throw new ArgumentException("Enter Sakana's Microsoft application (client) ID in Settings first.", nameof(clientId));
        }

        return parsed.ToString("D");
    }

    private sealed class SavedMicrosoftCredentialStore
    {
        [JsonPropertyName("accounts")]
        public List<SavedMicrosoftCredential> Accounts { get; set; } = [];

        [JsonPropertyName("activePlayerUuid")]
        public string? ActivePlayerUuid { get; set; }
    }

    private sealed record SavedMicrosoftCredential(
        [property: JsonPropertyName("clientId")] string ClientId,
        [property: JsonPropertyName("refreshToken")] string RefreshToken,
        [property: JsonPropertyName("playerUuid")] string? PlayerUuid = null,
        [property: JsonPropertyName("playerName")] string? PlayerName = null)
    {
        public override string ToString() => "SavedMicrosoftCredential(RefreshToken=[redacted])";
    }

    private sealed record DeviceCodeResponse(
        [property: JsonPropertyName("device_code")] string DeviceCode,
        [property: JsonPropertyName("user_code")] string UserCode,
        [property: JsonPropertyName("verification_uri")] string VerificationUri,
        [property: JsonPropertyName("message")] string? Message,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("interval")] int Interval);

    private sealed record OAuthTokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("refresh_token")] string RefreshToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn)
    {
        public override string ToString() => "OAuthTokenResponse(Tokens=[redacted])";
    }

    private sealed record OAuthErrorResponse(
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("error_description")] string? Description);

    private sealed record XboxTokenResponse(
        [property: JsonPropertyName("Token")] string Token,
        [property: JsonPropertyName("DisplayClaims")] XboxDisplayClaims? DisplayClaims)
    {
        public override string ToString() => "XboxTokenResponse(Token=[redacted])";
    }

    private sealed record XboxDisplayClaims(
        [property: JsonPropertyName("xui")] IReadOnlyList<XboxUserClaim>? Xui);

    private sealed record XboxUserClaim(
        [property: JsonPropertyName("uhs")] string? UserHash,
        [property: JsonPropertyName("xid")] string? XboxId);

    private sealed record MinecraftTokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn)
    {
        public override string ToString() => "MinecraftTokenResponse(AccessToken=[redacted])";
    }

    private sealed record MinecraftEntitlementsResponse(
        [property: JsonPropertyName("items")] IReadOnlyList<MinecraftEntitlement>? Items);

    private sealed record MinecraftEntitlement(
        [property: JsonPropertyName("name")] string? Name);

    private sealed record MinecraftProfileResponse(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("skins")] IReadOnlyList<MinecraftProfileSkin>? Skins);

    private sealed record MinecraftProfileSkin(
        [property: JsonPropertyName("state")] string? State,
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("variant")] string? Variant);

    private sealed record MojangSessionProfileResponse(
        [property: JsonPropertyName("properties")] IReadOnlyList<MojangProfileProperty>? Properties);

    private sealed record MojangProfileProperty(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("value")] string? Value);

    private sealed record MojangTexturePayload(
        [property: JsonPropertyName("textures")] MojangTextureCollection? Textures);

    private sealed record MojangTextureCollection(
        [property: JsonPropertyName("SKIN")] MojangSkinTexture? Skin);

    private sealed record MojangSkinTexture(
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("metadata")] MojangSkinMetadata? Metadata);

    private sealed record MojangSkinMetadata(
        [property: JsonPropertyName("model")] string? Model);

    private sealed record ApiErrorResponse(
        [property: JsonPropertyName("errorMessage")] string? ErrorMessage,
        [property: JsonPropertyName("message")] string? Message);

    private sealed class MicrosoftOAuthException(string error, string message) : InvalidOperationException(message)
    {
        public string Error { get; } = error;
    }
}
