using System.Net;

using Chiron.Cds.Shared;
using Chiron.Cds.Web.Configuration;
using Chiron.Cds.Web.SmartLaunch;
using Chiron.Cds.Web.Tenancy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Chiron.Cds.Web.IntegrationTests;

/// <summary>
/// Tests for the dev-only <c>GET /smart/backend-session</c> route that mints a
/// session from a SMART Backend Services token and redirects into the chart
/// render path. The real <see cref="BackendAuthService"/> is replaced with a
/// fake so the controller's branches (dev-gating, validation, the 502 on token
/// failure, and the success redirect) are exercised without hitting Epic.
/// </summary>
public class BackendSessionControllerTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string PatientId = "test-patient-1";
    private readonly WebApplicationFactory<Program> _factory;

    public BackendSessionControllerTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Returns_NotFound_Outside_Development()
    {
        using var f = FactoryWith(configured: true, throws: false, environment: "Production");
        using var client = f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var resp = await client.GetAsync($"/smart/backend-session?patient={PatientId}");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound,
            because: "a system token can read any patient, so the route must be inert outside Development");
    }

    [Fact]
    public async Task Missing_Patient_Returns_BadRequest()
    {
        using var f = FactoryWith(configured: true, throws: false);
        using var client = f.CreateClient();

        var resp = await client.GetAsync("/smart/backend-session");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Not_Configured_Returns_BadRequest()
    {
        using var f = FactoryWith(configured: false, throws: false);
        using var client = f.CreateClient();

        var resp = await client.GetAsync($"/smart/backend-session?patient={PatientId}");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Token_Failure_Returns_BadGateway()
    {
        using var f = FactoryWith(configured: true, throws: true);
        using var client = f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var resp = await client.GetAsync($"/smart/backend-session?patient={PatientId}");

        resp.StatusCode.Should().Be(HttpStatusCode.BadGateway,
            because: "a token-endpoint rejection is an upstream failure, surfaced as 502");
    }

    [Fact]
    public async Task Success_Mints_Session_And_Redirects_To_Session_Aware_App_Route()
    {
        using var f = FactoryWith(configured: true, throws: false);
        using var client = f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var resp = await client.GetAsync($"/smart/backend-session?patient={PatientId}");

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = resp.Headers.Location!.OriginalString;
        location.Should().StartWith("/app?session=",
            because: "the /app route resolves tenant + token from the session; /app/patient ignores sessions");
        location.Should().Contain($"patient={PatientId}");

        var sessionId = SessionIdFromLocation(location);
        var session = f.Services.GetRequiredService<ITokenStore>().GetSession(sessionId);
        session.Should().NotBeNull();
        session!.AccessToken.Should().Be(FakeBackend.Token);
        session.PatientId.Should().Be(PatientId);
        session.TenantId.Should().Be("epic-sandbox");
    }

    private static string SessionIdFromLocation(string location)
    {
        var query = location[(location.IndexOf('?') + 1)..];
        var sessionPart = query.Split('&').First(p => p.StartsWith("session=", StringComparison.Ordinal));
        return Uri.UnescapeDataString(sessionPart["session=".Length..]);
    }

    private WebApplicationFactory<Program> FactoryWith(bool configured, bool throws, string environment = "Development") =>
        _factory.WithWebHostBuilder(b =>
        {
            b.UseEnvironment(environment);
            b.ConfigureTestServices(services =>
                services.AddSingleton<BackendAuthService>(sp =>
                    new FakeBackend(
                        sp.GetRequiredService<TenantRegistry>(),
                        sp.GetRequiredService<IOptions<ChironOptions>>(),
                        configured, throws)));
        });

    private sealed class FakeBackend : BackendAuthService
    {
        public const string Token = "backend-access-token";

        private readonly bool _configured;
        private readonly bool _throws;
        private readonly TenantConfig _tenant;

        public FakeBackend(TenantRegistry tenants, IOptions<ChironOptions> options, bool configured, bool throws)
            : base(tenants,
                   new SmartConfigurationClient(new HttpClient(), NullLogger<SmartConfigurationClient>.Instance),
                   new HttpClient(), options, NullLogger<BackendAuthService>.Instance)
        {
            _configured = configured;
            _throws = throws;
            _tenant = tenants.GetById("epic-sandbox");
        }

        public override bool IsConfigured => _configured;
        public override TenantConfig Tenant => _tenant;
        public override Task<BackendToken> GetAccessTokenAsync(CancellationToken ct) =>
            _throws
                ? throw new TokenExchangeException("simulated token-endpoint rejection")
                : Task.FromResult(new BackendToken(Token, 3600, "system/Patient.read"));
    }
}
