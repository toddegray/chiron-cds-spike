using System.Net;

using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Chiron.Cds.Web.IntegrationTests;

/// <summary>
/// Hits the /app/demo endpoints via <see cref="WebApplicationFactory{TEntryPoint}"/>.
/// Verifies the index, the happy-path scenario render against the
/// committed docs/sample-patient-view-request.json fixture, and the 404
/// branch for unknown scenarios.
/// </summary>
public class DemoControllerTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public DemoControllerTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Index_Lists_Annie_Smith_Scenario()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/app/demo");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("annie-smith");
        body.Should().Contain("12674028");
        body.Should().Contain("/cds-services",
            because: "the index links to the live CDS Hooks discovery endpoint");
    }

    [Fact]
    public async Task Render_Annie_Smith_Produces_Card_From_Real_Cerner_Prefetch()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/app/demo/annie-smith");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();

        body.Should().Contain("CHA",
            because: "Annie Smith's real chart (female + diabetes) fires CHA₂DS₂-VASc");
        body.Should().Contain("12674028",
            because: "the subline shows the real patient id");
        body.Should().Contain("Demo mode",
            because: "the demo banner identifies the page as demo-mode");
        body.Should().Contain("Fingerprint:",
            because: "the card renders the alert fingerprint prominently");
    }

    [Fact]
    public async Task Unknown_Scenario_Returns_404()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/app/demo/no-such-scenario");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
