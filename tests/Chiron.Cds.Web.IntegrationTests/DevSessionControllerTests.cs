using System.Net;
using Chiron.Cds.Web.SmartLaunch;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Chiron.Cds.Web.IntegrationTests;

/// <summary>
/// Tests for the dev-only <c>GET /smart/dev-session</c> escape hatch that mints
/// a SMART session from a provided access token (e.g. from Epic's LaunchPad) and
/// hands off to the normal <c>/app</c> render path.
/// </summary>
public class DevSessionControllerTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public DevSessionControllerTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Valid_Request_Redirects_To_App_And_Persists_Session()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var resp = await client.GetAsync(
            "/smart/dev-session?access_token=tok-123&patient=epic-patient-1&tenant=epic-sandbox");

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = resp.Headers.Location!.OriginalString;
        location.Should().StartWith("/app?session=");

        var sessionId = Uri.UnescapeDataString(location["/app?session=".Length..]);
        var store = _factory.Services.GetRequiredService<ITokenStore>();
        var session = store.GetSession(sessionId);
        session.Should().NotBeNull();
        session!.AccessToken.Should().Be("tok-123");
        session.PatientId.Should().Be("epic-patient-1");
        session.TenantId.Should().Be("epic-sandbox");
        session.GrantedScopes.Should().NotBeEmpty(
            because: "the minted session adopts the tenant's configured scope set");
    }

    [Fact]
    public async Task Missing_Access_Token_Returns_BadRequest()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/smart/dev-session?patient=epic-patient-1");
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Missing_Patient_Returns_BadRequest()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/smart/dev-session?access_token=tok-123");
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Unknown_Tenant_Returns_BadRequest()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync(
            "/smart/dev-session?access_token=tok-123&patient=epic-patient-1&tenant=does-not-exist");
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Returns_NotFound_Outside_Development()
    {
        using var prodFactory = _factory.WithWebHostBuilder(b => b.UseEnvironment("Production"));
        using var client = prodFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var resp = await client.GetAsync(
            "/smart/dev-session?access_token=tok-123&patient=epic-patient-1&tenant=epic-sandbox");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound,
            because: "the token-injection route must be inert outside Development");
    }
}
