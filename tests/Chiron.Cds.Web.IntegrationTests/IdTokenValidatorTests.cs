using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;

using Chiron.Cds.Shared;
using Chiron.Cds.Web.SmartLaunch;
using Chiron.Cds.Web.Tenancy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;

namespace Chiron.Cds.Web.IntegrationTests;

/// <summary>
/// Tests for <see cref="IdTokenValidator"/>. Builds a self-signed
/// id_token with a generated RSA key, publishes the public key via a
/// stub JWKS endpoint, and asserts the validator accepts the token when
/// claims line up and rejects every meaningful tampering.
/// </summary>
public class IdTokenValidatorTests
{
    private const string ClientId = "5df0b845-test";
    private const string FhirBase = "https://fhir-ehr-code.cerner.com/r4/test-tenant";
    private const string Issuer = "https://issuer.test";
    private const string JwksUri = "https://issuer.test/jwks";

    private static (IdTokenValidator validator, RSA rsa, string kid) BuildValidator(
        string? tokenEndpoint = null,
        string? jwksUri = JwksUri,
        bool overrideJwksWithNull = false)
    {
        var rsa = RSA.Create(2048);
        var parameters = rsa.ExportParameters(false);
        var kid = Guid.NewGuid().ToString("N");

        // Stub SmartConfigurationClient by intercepting via the HttpClient
        // it would call. The validator goes through SmartConfigurationClient,
        // which fetches the well-known doc. We wire both to a single stub.
        var smartConfig = new SmartConfiguration(
            Issuer: Issuer,
            AuthorizationEndpoint: "https://issuer.test/authorize",
            TokenEndpoint: tokenEndpoint ?? "https://issuer.test/token",
            JwksUri: overrideJwksWithNull ? null : jwksUri,
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

        var handler = new StubHandler((req, _) =>
        {
            if (req.RequestUri!.AbsoluteUri.EndsWith("/.well-known/smart-configuration", StringComparison.Ordinal))
                return JsonResponse(smartConfig);
            if (req.RequestUri.AbsoluteUri == (jwksUri ?? JwksUri))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(jwksBody) };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var http = new HttpClient(handler);
        var smartConfigClient = new SmartConfigurationClient(http, NullLogger<SmartConfigurationClient>.Instance);
        var validator = new IdTokenValidator(smartConfigClient, http, NullLogger<IdTokenValidator>.Instance);
        return (validator, rsa, kid);
    }

    private static HttpResponseMessage JsonResponse<T>(T body) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(body),
    };

    private static string BuildToken(RSA rsa, string kid, string audience, string issuer,
        DateTime? expires = null, string? signingAlg = SecurityAlgorithms.RsaSha256)
    {
        var key = new RsaSecurityKey(rsa) { KeyId = kid };
        var creds = new SigningCredentials(key, signingAlg);
        var handler = new JwtSecurityTokenHandler();
        var exp = expires ?? DateTime.UtcNow.AddMinutes(5);
        // notBefore always trails expires by at least an hour so callers
        // can produce already-expired tokens without violating nbf < exp.
        var nbf = exp.AddHours(-1);
        var token = handler.CreateJwtSecurityToken(
            issuer: issuer,
            audience: audience,
            subject: new ClaimsIdentity(new[] { new Claim("sub", "test-user") }),
            notBefore: nbf,
            expires: exp,
            issuedAt: nbf,
            signingCredentials: creds);
        return handler.WriteToken(token);
    }

    private static TenantConfig BuildTenant() => new(
        Id: "test",
        DisplayName: "Test",
        ClientId: ClientId,
        ClientSecret: "secret",
        FhirBaseUrl: new Uri(FhirBase),
        FhirOpenBaseUrl: null,
        Scopes: "launch openid");

    [Fact]
    public async Task Valid_Token_Is_Accepted()
    {
        var (validator, rsa, kid) = BuildValidator();
        var token = BuildToken(rsa, kid, audience: ClientId, issuer: Issuer);
        var result = await validator.ValidateAsync(token, BuildTenant(), CancellationToken.None);
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Empty_Id_Token_Is_Rejected()
    {
        var (validator, _, _) = BuildValidator();
        var act = async () => await validator.ValidateAsync("", BuildTenant(), CancellationToken.None);
        (await act.Should().ThrowAsync<InvalidLaunchStateException>())
            .Which.Message.Should().Contain("id_token");
    }

    [Fact]
    public async Task Wrong_Audience_Is_Rejected()
    {
        var (validator, rsa, kid) = BuildValidator();
        var token = BuildToken(rsa, kid, audience: "different-client-id", issuer: Issuer);
        var act = async () => await validator.ValidateAsync(token, BuildTenant(), CancellationToken.None);
        await act.Should().ThrowAsync<InvalidLaunchStateException>();
    }

    [Fact]
    public async Task Wrong_Issuer_Is_Rejected()
    {
        var (validator, rsa, kid) = BuildValidator();
        var token = BuildToken(rsa, kid, audience: ClientId, issuer: "https://impostor.test");
        var act = async () => await validator.ValidateAsync(token, BuildTenant(), CancellationToken.None);
        await act.Should().ThrowAsync<InvalidLaunchStateException>();
    }

    [Fact]
    public async Task Expired_Token_Is_Rejected()
    {
        var (validator, rsa, kid) = BuildValidator();
        var token = BuildToken(rsa, kid, audience: ClientId, issuer: Issuer,
            expires: DateTime.UtcNow.AddMinutes(-10));
        var act = async () => await validator.ValidateAsync(token, BuildTenant(), CancellationToken.None);
        await act.Should().ThrowAsync<InvalidLaunchStateException>();
    }

    [Fact]
    public async Task Missing_JwksUri_In_Smart_Configuration_Is_Rejected()
    {
        // SmartConfiguration with JwksUri = null must short-circuit before
        // the validator tries to fetch keys — otherwise it would NRE on the
        // JWKS HTTP call. This branch exists for production tenants that
        // advertise a partial well-known document.
        var (validator, rsa, kid) = BuildValidator(overrideJwksWithNull: true);
        var token = BuildToken(rsa, kid, audience: ClientId, issuer: Issuer);
        var act = async () => await validator.ValidateAsync(token, BuildTenant(), CancellationToken.None);
        (await act.Should().ThrowAsync<InvalidLaunchStateException>())
            .Which.Message.Should().Contain("jwks_uri");
    }

    [Fact]
    public async Task Token_Signed_By_Wrong_Key_Is_Rejected()
    {
        var (validator, _, kid) = BuildValidator();
        using var attackerRsa = RSA.Create(2048);
        // Build a token signed by an attacker's key but with the same kid the
        // legitimate JWKS publishes; the signature won't match the public key
        // the validator fetches.
        var token = BuildToken(attackerRsa, kid, audience: ClientId, issuer: Issuer);
        var act = async () => await validator.ValidateAsync(token, BuildTenant(), CancellationToken.None);
        await act.Should().ThrowAsync<InvalidLaunchStateException>();
    }

    /// <summary>Test HttpMessageHandler that intercepts every outbound request.</summary>
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
