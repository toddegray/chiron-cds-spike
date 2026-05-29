using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;

using Chiron.Cds.Shared;
using Chiron.Cds.Web.Configuration;
using Chiron.Cds.Web.SmartLaunch;
using Chiron.Cds.Web.Tenancy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Chiron.Cds.Web.IntegrationTests;

/// <summary>
/// Covers <see cref="AuthorizationService.ExchangeCodeAsync"/>'s branch on
/// the presence of <c>id_token</c> in the token response. The id_token JWS
/// validation step is observable via JWKS endpoint hits — fetching the JWKS
/// is the validator's first network side-effect, so a JWKS hit is sufficient
/// proof the validator ran. Spy is a hit-counter inside the stub
/// HttpMessageHandler.
/// </summary>
public class AuthorizationServiceTests
{
    private const string ClientId = "test-client";
    private const string FhirBase = "https://fhir.test/r4";
    private const string Issuer = "https://issuer.test";
    private const string JwksUri = "https://issuer.test/jwks";
    private const string AuthorizeEndpoint = "https://issuer.test/authorize";
    private const string TokenEndpoint = "https://issuer.test/token";

    [Fact]
    public async Task ExchangeCode_With_IdToken_Invokes_Validator()
    {
        var harness = BuildHarness(includeIdToken: true);

        var session = await harness.Service.ExchangeCodeAsync(
            code: "auth-code", state: harness.State, CancellationToken.None);

        session.AccessToken.Should().Be("access-token-value");
        harness.JwksHitCount.Should().BeGreaterThan(0,
            because: "the id_token-present branch must run the JWS validator, which fetches the JWKS");
    }

    [Fact]
    public async Task ExchangeCode_Without_IdToken_Skips_Validator()
    {
        var harness = BuildHarness(includeIdToken: false);

        var session = await harness.Service.ExchangeCodeAsync(
            code: "auth-code", state: harness.State, CancellationToken.None);

        session.AccessToken.Should().Be("access-token-value");
        harness.JwksHitCount.Should().Be(0,
            because: "the id_token-absent branch must bypass JWS validation entirely (no JWKS fetch)");
    }

    [Fact]
    public async Task BuildAuthorizeUri_Standalone_Swaps_Launch_For_LaunchPatient()
    {
        var harness = BuildHarness(includeIdToken: false);
        var tenant = new TenantConfig(
            Id: "test", DisplayName: "Test", ClientId: ClientId, ClientSecret: "secret",
            FhirBaseUrl: new Uri(FhirBase), FhirOpenBaseUrl: null,
            Scopes: "launch openid fhirUser user/Patient.read");

        var uri = await harness.Service.BuildAuthorizeUriAsync(
            tenant, launchToken: null, "https://localhost/cb", CancellationToken.None);

        var scope = ScopeOf(uri).Split(' ');
        scope.Should().Contain("launch/patient");
        scope.Should().NotContain("launch",
            because: "a standalone launch replaces bare 'launch' with 'launch/patient'");
        scope.Should().Contain("user/Patient.read");
    }

    [Fact]
    public async Task BuildAuthorizeUri_EhrLaunch_Swaps_LaunchPatient_For_Launch()
    {
        var harness = BuildHarness(includeIdToken: false);
        var tenant = new TenantConfig(
            Id: "test", DisplayName: "Test", ClientId: ClientId, ClientSecret: "secret",
            FhirBaseUrl: new Uri(FhirBase), FhirOpenBaseUrl: null,
            Scopes: "launch/patient openid fhirUser user/Patient.read");

        var uri = await harness.Service.BuildAuthorizeUriAsync(
            tenant, launchToken: "launch-xyz", "https://localhost/cb", CancellationToken.None);

        var query = QueryOf(uri);
        query["scope"].Split(' ').Should().Contain("launch");
        query["scope"].Should().NotContain("launch/patient",
            because: "an EHR launch (launch token present) uses bare 'launch'");
        query["launch"].Should().Be("launch-xyz");
    }

    private static string ScopeOf(Uri uri) => QueryOf(uri)["scope"];

    private static IReadOnlyDictionary<string, string> QueryOf(Uri uri) =>
        uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p.Length > 1 ? p[1] : ""));

    private static Harness BuildHarness(bool includeIdToken)
    {
        var rsa = RSA.Create(2048);
        var kid = Guid.NewGuid().ToString("N");
        var parameters = rsa.ExportParameters(false);

        var smartConfig = new SmartConfiguration(
            Issuer: Issuer,
            AuthorizationEndpoint: AuthorizeEndpoint,
            TokenEndpoint: TokenEndpoint,
            JwksUri: JwksUri,
            IntrospectionEndpoint: null,
            RevocationEndpoint: null,
            Capabilities: null,
            ScopesSupported: null,
            CodeChallengeMethodsSupported: null);

        var jwksBody = JsonSerializer.Serialize(new
        {
            keys = new[]
            {
                new
                {
                    kty = "RSA",
                    kid,
                    use = "sig",
                    alg = "RS256",
                    n = Base64UrlEncoder.Encode(parameters.Modulus),
                    e = Base64UrlEncoder.Encode(parameters.Exponent),
                },
            },
        });

        var idToken = includeIdToken
            ? BuildIdToken(rsa, kid, audience: ClientId, issuer: Issuer)
            : null;

        var jwksHits = 0;
        var handler = new StubHandler((req, _) =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.EndsWith("/.well-known/smart-configuration", StringComparison.Ordinal))
                return Json(smartConfig);
            if (url == TokenEndpoint)
                return Json(BuildTokenResponse(idToken));
            if (url == JwksUri)
            {
                Interlocked.Increment(ref jwksHits);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(jwksBody) };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var http = new HttpClient(handler);
        var smartConfigClient = new SmartConfigurationClient(http, NullLogger<SmartConfigurationClient>.Instance);
        var validator = new IdTokenValidator(smartConfigClient, http, NullLogger<IdTokenValidator>.Instance);
        var tenants = BuildTenantRegistry();
        var store = new InMemoryTokenStore();
        var service = new AuthorizationService(
            tenants, smartConfigClient, http, store, validator,
            NullLogger<AuthorizationService>.Instance);

        var state = Guid.NewGuid().ToString("N");
        store.SavePending(new PendingLaunch(
            State: state,
            TenantId: "test",
            CodeVerifier: "verifier-irrelevant-the-stub-ignores-it",
            LaunchToken: null,
            RedirectUri: "https://localhost/callback",
            CreatedAt: DateTimeOffset.UtcNow));

        return new Harness(service, state, () => Volatile.Read(ref jwksHits));
    }

    private static TenantRegistry BuildTenantRegistry()
    {
        var options = new ChironOptions
        {
            DefaultTenant = "test",
            Tenants = new(StringComparer.OrdinalIgnoreCase)
            {
                ["test"] = new TenantOptions
                {
                    DisplayName = "Test",
                    ClientId = ClientId,
                    ClientSecret = "secret",
                    FhirBaseUrl = FhirBase,
                    Scopes = "launch openid",
                },
            },
        };
        return new TenantRegistry(Options.Create(options));
    }

    private static string BuildIdToken(RSA rsa, string kid, string audience, string issuer)
    {
        var key = new RsaSecurityKey(rsa) { KeyId = kid };
        var creds = new SigningCredentials(key, SecurityAlgorithms.RsaSha256);
        var handler = new JwtSecurityTokenHandler();
        var token = handler.CreateJwtSecurityToken(
            issuer: issuer,
            audience: audience,
            subject: new ClaimsIdentity(new[] { new Claim("sub", "test-user") }),
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(5),
            issuedAt: DateTime.UtcNow,
            signingCredentials: creds);
        return handler.WriteToken(token);
    }

    private static object BuildTokenResponse(string? idToken)
    {
        return new
        {
            access_token = "access-token-value",
            token_type = "Bearer",
            expires_in = 3600,
            scope = "launch openid",
            refresh_token = (string?)null,
            id_token = idToken,
            patient = "patient-123",
            encounter = (string?)null,
            need_patient_banner = (bool?)null,
            smart_style_url = (string?)null,
        };
    }

    private static HttpResponseMessage Json<T>(T body) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(body),
    };

    private sealed record Harness(AuthorizationService Service, string State, Func<int> JwksHits)
    {
        public int JwksHitCount => JwksHits();
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _respond;
        public StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond)
        {
            _respond = respond;
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(_respond(request, ct));
    }
}
