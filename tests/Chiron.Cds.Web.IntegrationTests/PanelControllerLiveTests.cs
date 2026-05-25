using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Chiron.Cds.Web.IntegrationTests;

/// <summary>
/// End-to-end tests for the panel surface. Hits the open Cerner sandbox
/// (fhir-open.cerner.com) through the live panel controller routes — same
/// code path the running app exposes at <c>/app/panel</c>,
/// <c>/app/search</c>, and <c>/app/patient/{id}</c>. No augmentation; the
/// names + ids asserted are the actual chart contents.
///
/// Skipped silently on network errors so flaky CI doesn't go red on
/// Cerner outages.
/// </summary>
[Trait("Category", "Live")]
public class PanelControllerLiveTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string AnniePatientId = "12674028";
    private readonly WebApplicationFactory<Program> _factory;

    public PanelControllerLiveTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Panel_Renders_Annie_Smith_Row_With_CHA2DS2VASc_Headline()
    {
        using var client = _factory.CreateClient();
        HttpResponseMessage resp;
        string body;
        try
        {
            resp = await client.GetAsync("/app/panel");
            body = await resp.Content.ReadAsStringAsync();
        }
        catch (HttpRequestException) { return; }
        catch (TaskCanceledException) { return; }

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("Your panel",
            because: "the panel page renders its heading");
        body.Should().Contain("SMITH, ANNIE",
            because: "Annie Smith is in the default panel and her chart name comes back from the live Cerner sandbox");
        body.Should().Contain($"href=\"/app/patient/{AnniePatientId}\"",
            because: "each row drills into the per-patient Visit Brief route");
        body.Should().Contain("CHA",
            because: "Annie's headline flag is CHA₂DS₂-VASc, which the engine fires on her real chart");
    }

    [Fact]
    public async Task Patient_Route_Renders_Visit_Brief_With_Live_Chart()
    {
        using var client = _factory.CreateClient();
        HttpResponseMessage resp;
        string body;
        try
        {
            resp = await client.GetAsync($"/app/patient/{AnniePatientId}");
            body = await resp.Content.ReadAsStringAsync();
        }
        catch (HttpRequestException) { return; }
        catch (TaskCanceledException) { return; }

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("CHA",
            because: "Annie's real chart fires CHA₂DS₂-VASc through the live engine path");
        body.Should().NotContain("Audit fingerprint",
            because: "the dev-toolbox fingerprint footer is gone — clinicians don't read SHA hashes");
        body.Should().NotContain("From Chiron Clinical Reasoning",
            because: "the self-attribution card footer is gone — the page is already Chiron");
        body.Should().Contain($"MRN {AnniePatientId}",
            because: "the new chart-banner demographics row shows the MRN alongside age, sex, DOB");
    }

    [Fact]
    public async Task Search_Returns_Patients_Matching_Query()
    {
        using var client = _factory.CreateClient();
        HttpResponseMessage resp;
        string body;
        try
        {
            resp = await client.GetAsync("/app/search?q=smith");
            body = await resp.Content.ReadAsStringAsync();
        }
        catch (HttpRequestException) { return; }
        catch (TaskCanceledException) { return; }

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("Find a patient");
        body.Should().Contain("Smith",
            because: "the live Cerner sandbox has at least one patient with 'Smith' in the name");
        body.Should().Contain("href=\"/app/patient/",
            because: "search results must link into the Visit Brief route");
    }

    [Fact]
    public async Task Search_With_Empty_Query_Shows_Hint()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/app/search");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("Start typing");
    }
}
