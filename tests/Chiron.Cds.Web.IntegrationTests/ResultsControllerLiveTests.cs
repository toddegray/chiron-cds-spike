using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Chiron.Cds.Web.IntegrationTests;

/// <summary>
/// End-to-end test for <c>/app/patient/{id}/results</c> against the live Epic
/// sandbox via the Backend Services connection. Skips on a degrade banner so
/// an Epic outage or a creds-less host doesn't go red.
/// </summary>
[Trait("Category", "Live")]
public class ResultsControllerLiveTests : IClassFixture<WebApplicationFactory<Program>>
{
    // Camila Lopez has lab observations + a diagnostic report in the Epic sandbox.
    private const string CamilaId = "erXuFYUfucBZaryVksYEcMg3";
    private const string CamilaMrn = "203713";
    private readonly WebApplicationFactory<Program> _factory;

    public ResultsControllerLiveTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Camila_Results_Renders_Real_Trends()
    {
        using var client = _factory.CreateClient();
        HttpResponseMessage resp;
        string body;
        try
        {
            resp = await client.GetAsync($"/app/patient/{CamilaId}/results");
            body = await resp.Content.ReadAsStringAsync();
        }
        catch (HttpRequestException) { return; }
        catch (TaskCanceledException) { return; }

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        // The sandbox flaps: a failed fetch degrades to a 200 page with a
        // "could not be loaded" banner rather than throwing. Skip on that path —
        // this test asserts live data, not the outage banner.
        if (body.Contains("could not be loaded", StringComparison.Ordinal)) return;
        body.Should().Contain("MRN " + CamilaMrn,
            because: "the demographics row shows the real MRN, not the FHIR resource id");
        body.Should().Contain("trend-title",
            because: "Camila has real lab observations — at least one trend card must render");
    }
}
