using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;

using Chiron.Cds.Shared;
using Chiron.Cds.Web.Tenancy;
using Microsoft.IdentityModel.Tokens;

namespace Chiron.Cds.Web.SmartLaunch;

/// <summary>
/// Validates the <c>id_token</c> SMART servers include in the token
/// response. The token's JWS signature must verify against the keys
/// published at the tenant's <c>jwks_uri</c>, and its <c>aud</c> must
/// equal the app's <c>client_id</c>. Issuer is validated against the
/// FHIR base URL (the SMART <c>iss</c> equivalent).
/// </summary>
/// <remarks>
/// SMART App Launch v2 § Token-Response-Validation requires <c>aud</c>
/// and signature checks but does not strictly require <c>iss</c>
/// validation. We do it anyway as defense-in-depth: a stolen id_token
/// from another tenant should not be reusable here.
/// </remarks>
public sealed class IdTokenValidator
{
    private readonly SmartConfigurationClient _smartConfig;
    private readonly HttpClient _http;
    private readonly ILogger<IdTokenValidator> _log;
    private readonly ConcurrentDictionary<string, JwksCacheEntry> _jwksCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan JwksLifetime = TimeSpan.FromHours(1);
    private static readonly JwtSecurityTokenHandler Handler = new();

    public IdTokenValidator(SmartConfigurationClient smartConfig, HttpClient http, ILogger<IdTokenValidator> log)
    {
        _smartConfig = smartConfig;
        _http = http;
        _log = log;
    }

    /// <summary>
    /// Throws <see cref="InvalidLaunchStateException"/> when the token is
    /// missing, has the wrong audience, the issuer doesn't match the
    /// tenant FHIR base, the signature doesn't verify, or the token has
    /// expired. On success returns the validated principal.
    /// </summary>
    public async Task<TokenValidationResult> ValidateAsync(
        string idToken,
        TenantConfig tenant,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(idToken))
            throw new InvalidLaunchStateException("Token response did not include an id_token.");

        var smart = await _smartConfig.GetAsync(tenant.FhirBaseUrl, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(smart.JwksUri))
            throw new InvalidLaunchStateException("SMART configuration does not advertise a jwks_uri.");

        var keys = await GetJwksAsync(smart.JwksUri, ct).ConfigureAwait(false);

        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = smart.Issuer ?? tenant.FhirBaseUrl.AbsoluteUri.TrimEnd('/'),
            ValidateAudience = true,
            ValidAudience = tenant.ClientId,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = keys,
            ClockSkew = TimeSpan.FromMinutes(2),
        };

        try
        {
            var result = await Handler.ValidateTokenAsync(idToken, parameters).ConfigureAwait(false);
            if (!result.IsValid)
            {
                _log.LogWarning("id_token validation failed: {Exception}", result.Exception?.Message);
                throw new InvalidLaunchStateException(
                    "id_token validation failed: " + (result.Exception?.Message ?? "unknown reason"));
            }
            return result;
        }
        catch (SecurityTokenException ex)
        {
            _log.LogWarning(ex, "id_token signature validation failed.");
            throw new InvalidLaunchStateException("id_token signature did not verify against the tenant JWKS.");
        }
    }

    private async Task<IReadOnlyList<SecurityKey>> GetJwksAsync(string jwksUri, CancellationToken ct)
    {
        if (_jwksCache.TryGetValue(jwksUri, out var entry)
            && entry.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return entry.Keys;
        }
        _log.LogInformation("Fetching JWKS from {Uri}", jwksUri);
        var jwks = await _http.GetFromJsonAsync<JwksDocument>(jwksUri, ct).ConfigureAwait(false)
            ?? throw new InvalidLaunchStateException("JWKS endpoint returned an empty body.");
        var keys = jwks.Keys
            .Select(k => new JsonWebKey
            {
                Kty = k.Kty,
                Kid = k.Kid,
                Alg = k.Alg,
                Use = k.Use,
                N = k.N,
                E = k.E,
                Crv = k.Crv,
                X = k.X,
                Y = k.Y,
            })
            .Cast<SecurityKey>()
            .ToArray();
        _jwksCache[jwksUri] = new JwksCacheEntry(keys, DateTimeOffset.UtcNow + JwksLifetime);
        return keys;
    }

    private sealed record JwksCacheEntry(IReadOnlyList<SecurityKey> Keys, DateTimeOffset ExpiresAt);

    /// <summary>Minimal JWKS DTO. We only consume RSA / EC public-key fields.</summary>
    private sealed class JwksDocument
    {
        public IReadOnlyList<JwkEntry> Keys { get; init; } = Array.Empty<JwkEntry>();
    }

    private sealed class JwkEntry
    {
        public string Kty { get; init; } = "";
        public string? Kid { get; init; }
        public string? Alg { get; init; }
        public string? Use { get; init; }
        public string? N { get; init; }
        public string? E { get; init; }
        public string? Crv { get; init; }
        public string? X { get; init; }
        public string? Y { get; init; }
    }
}
