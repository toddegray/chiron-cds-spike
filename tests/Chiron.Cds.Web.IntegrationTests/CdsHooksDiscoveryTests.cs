using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Chiron.Cds.Web.CdsHooks.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Chiron.Cds.Web.IntegrationTests;

/// <summary>
/// Boots the app via <see cref="WebApplicationFactory{TEntryPoint}"/> and
/// hits the CDS Hooks discovery + boundary cases. The real-data assertion
/// (that the engine fires on a real Cerner patient) lives in
/// <see cref="RealCernerPatientTests"/>; this file only covers wire-shape
/// and the "no inputs" boundary.
/// </summary>
public class CdsHooksDiscoveryTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public CdsHooksDiscoveryTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Discovery_Returns_PatientView_Service()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/cds-services");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<CdsServicesResponse>();
        body.Should().NotBeNull();
        body!.Services.Should().ContainSingle();
        var svc = body.Services.Single();
        svc.Hook.Should().Be("patient-view");
        svc.Id.Should().Be("chiron-patient-view");
        svc.Prefetch.Should().NotBeNull();
        svc.Prefetch!.Should().ContainKey("patient");
        svc.Prefetch.Should().ContainKey("observations");
    }

    [Fact]
    public async Task PatientView_With_Empty_Prefetch_Returns_Empty_Cards()
    {
        using var client = _factory.CreateClient();
        var request = new
        {
            hook = "patient-view",
            hookInstance = Guid.NewGuid().ToString(),
            context = new { patientId = "no-prefetch-no-auth" },
        };
        var resp = await client.PostAsJsonAsync("/cds-services/chiron-patient-view", request);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("cards").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Health_Endpoint_Returns_Ok()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/health");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
