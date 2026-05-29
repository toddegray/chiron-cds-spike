using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

using Chiron.Cds.Shared;
using Chiron.Cds.Web.Configuration;
using Chiron.Cds.Web.Tenancy;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Chiron.Cds.Web.SmartLaunch;

/// <summary>
/// SMART Backend Services authentication: mints a system access token via the
/// <c>client_credentials</c> grant with a <c>private_key_jwt</c> client
/// assertion (RS384-signed, verified by the server against the app's published
/// JWK Set). No user, no patient context, no interactive login — used for
/// server-to-server FHIR reads with <c>system/*</c> scopes.
/// </summary>
public class BackendAuthService
{
    private readonly TenantRegistry _tenants;
    private readonly SmartConfigurationClient _smartConfig;
    private readonly HttpClient _http;
    private readonly ChironOptions _options;
    private readonly ILogger<BackendAuthService> _log;

    private static readonly JwtSecurityTokenHandler Handler = new();

    public BackendAuthService(
        TenantRegistry tenants,
        SmartConfigurationClient smartConfig,
        HttpClient http,
        IOptions<ChironOptions> options,
        ILogger<BackendAuthService> log)
    {
        _tenants = tenants;
        _smartConfig = smartConfig;
        _http = http;
        _options = options.Value;
        _log = log;
    }

    /// <summary>True when both the backend settings and the signing key are present.</summary>
    public virtual bool IsConfigured =>
        _options.EpicBackend is { ClientId.Length: > 0 }
        && !string.IsNullOrWhiteSpace(_options.EpicBackendPrivateKeyPem);

    /// <summary>The tenant the backend flow reads FHIR data from.</summary>
    public virtual TenantConfig Tenant => _tenants.GetById(RequireConfig().TenantId);

    /// <summary>
    /// Acquire a fresh system access token. Throws
    /// <see cref="TokenExchangeException"/> if the token endpoint rejects the
    /// assertion or returns an unparseable body.
    /// </summary>
    public virtual async Task<BackendToken> GetAccessTokenAsync(CancellationToken ct)
    {
        var cfg = RequireConfig();
        var tenant = _tenants.GetById(cfg.TenantId);
        var smart = await _smartConfig.GetAsync(tenant.FhirBaseUrl, ct).ConfigureAwait(false);

        var assertion = BuildClientAssertion(cfg, smart.TokenEndpoint);
        return await PostClientCredentialsAsync(smart.TokenEndpoint, assertion, cfg.Scopes, ct).ConfigureAwait(false);
    }

    private BackendServiceOptions RequireConfig() =>
        IsConfigured
            ? _options.EpicBackend!
            : throw new InvalidOperationException(
                "SMART Backend Services is not configured. Set Chiron:EpicBackend and Chiron:EpicBackendPrivateKeyPem.");

    private string BuildClientAssertion(BackendServiceOptions cfg, string tokenEndpoint)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(_options.EpicBackendPrivateKeyPem);
        var signingKey = new RsaSecurityKey(rsa) { KeyId = cfg.KeyId };
        // The RSA is disposed when this method returns. The global signature-provider
        // cache would otherwise retain a provider bound to the disposed key and reuse
        // it on a later call, throwing ObjectDisposedException under concurrency — so
        // opt this short-lived key out of caching.
        signingKey.CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false };
        var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha384);

        var now = DateTime.UtcNow;
        var assertion = Handler.CreateJwtSecurityToken(
            issuer: cfg.ClientId,
            audience: tokenEndpoint,
            subject: new ClaimsIdentity(new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, cfg.ClientId),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            }),
            notBefore: now,
            expires: now.AddMinutes(4),
            issuedAt: now,
            signingCredentials: credentials);
        return Handler.WriteToken(assertion);
    }

    private async Task<BackendToken> PostClientCredentialsAsync(
        string tokenEndpoint, string assertion, string scopes, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
            ["client_assertion"] = assertion,
        };
        if (!string.IsNullOrWhiteSpace(scopes))
            form["scope"] = scopes;

        using var req = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form),
        };
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            _log.LogError("Backend token endpoint returned {Status}: {Body}", (int)resp.StatusCode, body);
            throw new TokenExchangeException(
                $"Backend token endpoint returned {(int)resp.StatusCode} ({resp.ReasonPhrase}).");
        }

        BackendTokenResponse? dto;
        try
        {
            dto = JsonSerializer.Deserialize<BackendTokenResponse>(body);
        }
        catch (JsonException ex)
        {
            throw new TokenExchangeException("Backend token endpoint returned an unparseable body.", ex);
        }
        if (dto is null || string.IsNullOrEmpty(dto.AccessToken))
            throw new TokenExchangeException("Backend token endpoint returned no access_token.");

        _log.LogInformation(
            "Backend token acquired (expires_in {Expiry}s; scopes: {Scope}).",
            dto.ExpiresIn, dto.Scope);
        return new BackendToken(dto.AccessToken, dto.ExpiresIn, dto.Scope);
    }

    /// <summary>A minted system access token and its lifetime/scope metadata.</summary>
    public sealed record BackendToken(string AccessToken, int ExpiresInSeconds, string? Scope);

    private sealed class BackendTokenResponse
    {
        [JsonPropertyName("access_token")] public string AccessToken { get; init; } = string.Empty;
        [JsonPropertyName("token_type")] public string? TokenType { get; init; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; init; }
        [JsonPropertyName("scope")] public string? Scope { get; init; }
    }
}
