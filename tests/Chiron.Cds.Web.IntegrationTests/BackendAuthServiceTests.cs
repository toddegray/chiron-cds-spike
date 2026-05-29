using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
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
/// Covers <see cref="BackendAuthService.GetAccessTokenAsync"/> — the SMART
/// Backend Services <c>client_credentials</c> + <c>private_key_jwt</c> flow.
/// The decisive contract is the client assertion: it must be an RS384 JWS
/// signed by the configured key (<c>kid</c>), with <c>iss</c>/<c>sub</c> = the
/// client id and <c>aud</c> = the token endpoint. The stub token endpoint
/// captures the posted assertion so the test can verify it against the public
/// key — the same check the real server runs against the published JWK Set.
/// </summary>
public class BackendAuthServiceTests
{
    private const string ClientId = "backend-client";
    private const string FhirBase = "https://fhir.test/r4";
    private const string TokenEndpoint = "https://issuer.test/token";
    private const string Kid = "chiron-epic-backend-1";
    private const string Scopes = "system/Patient.read system/Condition.read";

    [Fact]
    public async Task GetAccessToken_Posts_Client_Credentials_And_Returns_Token()
    {
        var harness = BuildHarness();

        var token = await harness.Service.GetAccessTokenAsync(CancellationToken.None);

        token.AccessToken.Should().Be("backend-access-token");
        token.Scope.Should().Be(Scopes);

        var form = harness.CapturedForm;
        form["grant_type"].Should().Be("client_credentials");
        form["client_assertion_type"].Should().Be("urn:ietf:params:oauth:client-assertion-type:jwt-bearer");
        form["scope"].Should().Be(Scopes);
        form.Should().ContainKey("client_assertion");
    }

    [Fact]
    public async Task ClientAssertion_Is_RS384_Signed_With_Expected_Claims()
    {
        var harness = BuildHarness();

        await harness.Service.GetAccessTokenAsync(CancellationToken.None);

        var assertion = harness.CapturedForm["client_assertion"];
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(assertion);

        jwt.Header.Alg.Should().Be("RS384", because: "SMART Backend Services requires an asymmetric JWS; we sign RS384");
        jwt.Header.Kid.Should().Be(Kid, because: "the server selects the verifying JWK by kid");
        jwt.Issuer.Should().Be(ClientId);
        jwt.Subject.Should().Be(ClientId, because: "iss and sub are both the client id per the spec");
        jwt.Audiences.Should().Contain(TokenEndpoint, because: "aud must be the token endpoint");
        jwt.Claims.Should().Contain(c => c.Type == "jti", because: "a unique jti prevents assertion replay");

        // The signature must verify against the public half of the signing key —
        // exactly what the server does against the published JWK Set.
        var result = await new JwtSecurityTokenHandler().ValidateTokenAsync(assertion, new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = ClientId,
            ValidateAudience = true,
            ValidAudience = TokenEndpoint,
            ValidateLifetime = true,
            ValidAlgorithms = new[] { SecurityAlgorithms.RsaSha384 },
            IssuerSigningKey = harness.PublicKey,
        });
        result.IsValid.Should().BeTrue(because: result.Exception?.Message);
    }

    [Fact]
    public void IsConfigured_False_When_Private_Key_Missing()
    {
        var harness = BuildHarness(includePrivateKey: false);

        harness.Service.IsConfigured.Should().BeFalse();
        var act = () => harness.Service.GetAccessTokenAsync(CancellationToken.None);
        act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task NonSuccess_Token_Response_Throws_TokenExchange()
    {
        var harness = BuildHarness(tokenResponse: () => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"error\":\"invalid_client\"}"),
        });

        var act = () => harness.Service.GetAccessTokenAsync(CancellationToken.None);
        await act.Should().ThrowAsync<TokenExchangeException>(
            because: "a non-2xx token response must surface as a TokenExchangeException, not a silent null");
    }

    [Fact]
    public async Task Unparseable_Token_Body_Throws_TokenExchange()
    {
        var harness = BuildHarness(tokenResponse: () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("this is not json"),
        });

        var act = () => harness.Service.GetAccessTokenAsync(CancellationToken.None);
        await act.Should().ThrowAsync<TokenExchangeException>(
            because: "a 200 with an unparseable body must throw rather than NRE downstream");
    }

    [Fact]
    public async Task Missing_AccessToken_Throws_TokenExchange()
    {
        var harness = BuildHarness(tokenResponse: () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { token_type = "Bearer", expires_in = 3600 }),
        });

        var act = () => harness.Service.GetAccessTokenAsync(CancellationToken.None);
        await act.Should().ThrowAsync<TokenExchangeException>(
            because: "a 200 that omits access_token is not a usable token");
    }

    private static Harness BuildHarness(bool includePrivateKey = true, Func<HttpResponseMessage>? tokenResponse = null)
    {
        var rsa = RSA.Create(2048);
        var privatePem = PemEncoding.WriteString("PRIVATE KEY", rsa.ExportPkcs8PrivateKey());
        var publicKey = new RsaSecurityKey(rsa.ExportParameters(false)) { KeyId = Kid };

        var smartConfig = new SmartConfiguration(
            Issuer: "https://issuer.test",
            AuthorizationEndpoint: "https://issuer.test/authorize",
            TokenEndpoint: TokenEndpoint,
            JwksUri: "https://issuer.test/jwks",
            IntrospectionEndpoint: null,
            RevocationEndpoint: null,
            Capabilities: null,
            ScopesSupported: null,
            CodeChallengeMethodsSupported: null);

        var captured = new Dictionary<string, string>();
        var handler = new StubHandler((req, _) =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.EndsWith("/.well-known/smart-configuration", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(smartConfig) };
            if (url == TokenEndpoint)
            {
                var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var kv = pair.Split('=', 2);
                    // application/x-www-form-urlencoded encodes spaces as '+'; decode that before percent-unescaping.
                    static string Decode(string s) => Uri.UnescapeDataString(s.Replace('+', ' '));
                    captured[Decode(kv[0])] = Decode(kv.Length > 1 ? kv[1] : "");
                }
                return tokenResponse?.Invoke() ?? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        access_token = "backend-access-token",
                        token_type = "Bearer",
                        expires_in = 3600,
                        scope = Scopes,
                    }),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var http = new HttpClient(handler);
        var smartConfigClient = new SmartConfigurationClient(http, NullLogger<SmartConfigurationClient>.Instance);

        var options = new ChironOptions
        {
            DefaultTenant = "test",
            Tenants = new(StringComparer.OrdinalIgnoreCase)
            {
                ["test"] = new TenantOptions
                {
                    DisplayName = "Test",
                    ClientId = ClientId,
                    FhirBaseUrl = FhirBase,
                    Scopes = "system/*.read",
                },
            },
            EpicBackend = new BackendServiceOptions
            {
                TenantId = "test",
                ClientId = ClientId,
                KeyId = Kid,
                Scopes = Scopes,
            },
            EpicBackendPrivateKeyPem = includePrivateKey ? privatePem : null,
        };

        var tenants = new TenantRegistry(Options.Create(options));
        var service = new BackendAuthService(
            tenants, smartConfigClient, http, Options.Create(options),
            NullLogger<BackendAuthService>.Instance);

        return new Harness(service, captured, publicKey);
    }

    private sealed record Harness(BackendAuthService Service, IReadOnlyDictionary<string, string> CapturedForm, RsaSecurityKey PublicKey);

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _respond;
        public StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond) => _respond = respond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(_respond(request, ct));
    }
}
