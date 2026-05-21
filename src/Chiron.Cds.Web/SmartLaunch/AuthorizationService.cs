using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Chiron.Cds.Shared;
using Chiron.Cds.Web.Tenancy;

namespace Chiron.Cds.Web.SmartLaunch;

/// <summary>
/// Builds authorize URLs and exchanges authorization codes for SMART
/// sessions. Confidential client with PKCE per SMART App Launch v2.
/// </summary>
public sealed class AuthorizationService
{
    private readonly TenantRegistry _tenants;
    private readonly SmartConfigurationClient _smartConfig;
    private readonly HttpClient _http;
    private readonly ITokenStore _store;
    private readonly ILogger<AuthorizationService> _log;

    public AuthorizationService(
        TenantRegistry tenants,
        SmartConfigurationClient smartConfig,
        HttpClient http,
        ITokenStore store,
        ILogger<AuthorizationService> log)
    {
        _tenants = tenants;
        _smartConfig = smartConfig;
        _http = http;
        _store = store;
        _log = log;
    }

    /// <summary>
    /// Returns the absolute authorize URL the user-agent should be redirected
    /// to, and persists the <see cref="PendingLaunch"/> so the callback can
    /// resume.
    /// </summary>
    public async Task<Uri> BuildAuthorizeUriAsync(
        TenantConfig tenant,
        string? launchToken,
        string redirectUri,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentException.ThrowIfNullOrEmpty(redirectUri);

        var smart = await _smartConfig.GetAsync(tenant.FhirBaseUrl, ct).ConfigureAwait(false);

        var state = RandomBase64Url(32);
        var verifier = RandomBase64Url(64);
        var challenge = ComputePkceChallenge(verifier);

        _store.SavePending(new PendingLaunch(
            State: state,
            TenantId: tenant.Id,
            CodeVerifier: verifier,
            LaunchToken: launchToken,
            RedirectUri: redirectUri,
            CreatedAt: DateTimeOffset.UtcNow));

        // SMART spec: the "launch" scope pairs with a "launch" parameter.
        // For standalone launches we must drop "launch" from the scope set,
        // otherwise some servers (e.g. Cerner) reject with invalid_request.
        var effectiveScopes = string.IsNullOrEmpty(launchToken)
            ? string.Join(' ', tenant.Scopes
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(s => !string.Equals(s, "launch", StringComparison.Ordinal)))
            : tenant.Scopes;

        var query = new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = tenant.ClientId,
            ["redirect_uri"] = redirectUri,
            ["scope"] = effectiveScopes,
            ["state"] = state,
            ["aud"] = tenant.FhirBaseUrl.AbsoluteUri.TrimEnd('/'),
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        };
        if (!string.IsNullOrEmpty(launchToken))
            query["launch"] = launchToken;

        var uri = AppendQuery(smart.AuthorizationEndpoint, query);
        _log.LogInformation(
            "Built authorize URL for tenant {Tenant} (endpoint {Endpoint}).",
            tenant.Id, smart.AuthorizationEndpoint);
        return uri;
    }

    /// <summary>
    /// Exchange an authorization code for a SMART session. Validates the
    /// <c>state</c>, supplies the PKCE verifier, and persists the resulting
    /// <see cref="SmartSession"/>.
    /// </summary>
    public async Task<SmartSession> ExchangeCodeAsync(
        string code,
        string state,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(code)) throw new InvalidLaunchStateException("Authorization code missing.");
        if (string.IsNullOrEmpty(state)) throw new InvalidLaunchStateException("State parameter missing.");

        var pending = _store.TakePending(state) ?? throw new InvalidLaunchStateException(
            "Unknown or expired state — the launch may have timed out.");

        var tenant = _tenants.GetById(pending.TenantId);
        var smart = await _smartConfig.GetAsync(tenant.FhirBaseUrl, ct).ConfigureAwait(false);

        var token = await PostTokenAsync(smart.TokenEndpoint, tenant, code, pending, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(token.Patient))
            _log.LogWarning("Token response did not include a patient context for tenant {Tenant}.", tenant.Id);

        var session = new SmartSession(
            SessionId: RandomBase64Url(24),
            TenantId: tenant.Id,
            AccessToken: token.AccessToken,
            RefreshToken: token.RefreshToken,
            PatientId: token.Patient ?? string.Empty,
            EncounterId: token.Encounter,
            IdToken: token.IdToken,
            ExpiresAt: DateTimeOffset.UtcNow.AddSeconds(Math.Max(token.ExpiresInSeconds - 30, 60)),
            GrantedScopes: (token.Scope ?? string.Empty)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .ToArray());

        _store.SaveSession(session);
        return session;
    }

    private async Task<TokenResponse> PostTokenAsync(
        string tokenEndpoint,
        TenantConfig tenant,
        string code,
        PendingLaunch pending,
        CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = pending.RedirectUri,
            ["client_id"] = tenant.ClientId,
            ["code_verifier"] = pending.CodeVerifier,
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form),
        };
        // Confidential client: HTTP Basic per SMART v2 recommendation.
        if (!string.IsNullOrEmpty(tenant.ClientSecret))
        {
            var creds = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                $"{Uri.EscapeDataString(tenant.ClientId)}:{Uri.EscapeDataString(tenant.ClientSecret)}"));
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", creds);
        }

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            _log.LogError("Token endpoint returned {Status}: {Body}", (int)resp.StatusCode, body);
            throw new TokenExchangeException(
                $"Token endpoint returned {(int)resp.StatusCode} ({resp.ReasonPhrase}).");
        }
        // Log a redacted version of the token response body so we can see
        // exactly what scopes / fields Cerner returned (access_token + refresh_token
        // redacted so they don't leak into log scrapes).
        _log.LogInformation("Token response body (redacted): {Body}", RedactTokens(body));

        try
        {
            return JsonSerializer.Deserialize<TokenResponse>(body)
                ?? throw new TokenExchangeException("Token endpoint returned an empty body.");
        }
        catch (JsonException ex)
        {
            throw new TokenExchangeException("Token endpoint returned an unparseable body.", ex);
        }
    }

    private static string RedactTokens(string json)
    {
        return System.Text.RegularExpressions.Regex.Replace(
            json,
            "\"(access_token|refresh_token|id_token)\"\\s*:\\s*\"[^\"]*\"",
            "\"$1\":\"<redacted>\"");
    }

    private static string RandomBase64Url(int byteLength)
    {
        Span<byte> buf = stackalloc byte[byteLength];
        RandomNumberGenerator.Fill(buf);
        return Base64Url(buf);
    }

    private static string ComputePkceChallenge(string verifier)
    {
        var bytes = Encoding.ASCII.GetBytes(verifier);
        var hash = SHA256.HashData(bytes);
        return Base64Url(hash);
    }

    private static string Base64Url(ReadOnlySpan<byte> bytes)
    {
        var s = Convert.ToBase64String(bytes);
        return s.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static Uri AppendQuery(string baseUrl, IReadOnlyDictionary<string, string?> query)
    {
        var sb = new StringBuilder(baseUrl);
        sb.Append(baseUrl.Contains('?') ? '&' : '?');
        var first = true;
        foreach (var kv in query)
        {
            if (kv.Value is null) continue;
            if (!first) sb.Append('&');
            first = false;
            sb.Append(Uri.EscapeDataString(kv.Key))
              .Append('=')
              .Append(Uri.EscapeDataString(kv.Value));
        }
        return new Uri(sb.ToString(), UriKind.Absolute);
    }
}
