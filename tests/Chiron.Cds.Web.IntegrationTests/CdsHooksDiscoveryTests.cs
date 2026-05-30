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
    public async Task Discovery_Returns_All_Four_Hook_Services()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/cds-services");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<CdsServicesResponse>();
        body.Should().NotBeNull();
        body!.Services.Should().HaveCount(4);
        body.Services.Select(s => s.Hook).Should().BeEquivalentTo(new[]
        {
            "patient-view", "order-select", "order-sign", "medication-prescribe",
        });
        body.Services.Select(s => s.Id).Should().BeEquivalentTo(new[]
        {
            "chiron-patient-view", "chiron-order-select", "chiron-order-sign", "chiron-medication-prescribe",
        });
        foreach (var svc in body.Services)
        {
            svc.Prefetch.Should().NotBeNull();
            svc.Prefetch!.Should().ContainKey("patient");
            svc.Prefetch.Should().ContainKey("allergies",
                because: "every CDS service prefetches allergies — drug-allergy is on the hot path");
        }
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

    [Theory]
    [InlineData("chiron-patient-view")]
    [InlineData("chiron-order-select")]
    [InlineData("chiron-order-sign")]
    [InlineData("chiron-medication-prescribe")]
    public async Task Hook_Services_Accept_Posts_And_Return_Empty_Cards_Without_Prefetch(string serviceId)
    {
        using var client = _factory.CreateClient();
        var request = new
        {
            hook = serviceId.Replace("chiron-", ""),
            hookInstance = Guid.NewGuid().ToString(),
            context = new { patientId = "no-prefetch-test" },
        };
        var resp = await client.PostAsJsonAsync($"/cds-services/{serviceId}", request);
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

    [Fact]
    public async Task Root_Redirects_To_Panel()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        var resp = await client.GetAsync("/");
        resp.StatusCode.Should().Be(HttpStatusCode.Found);
        resp.Headers.Location!.OriginalString.Should().Be("/app/panel",
            because: "the bare root has no content of its own and sends the user to the worklist");
    }
}
