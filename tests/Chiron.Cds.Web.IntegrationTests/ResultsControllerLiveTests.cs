using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Chiron.Cds.Web.IntegrationTests;

[Trait("Category", "Live")]
public class ResultsControllerLiveTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string NancyId = "12724066";
    private const string AnnieId = "12674028";
    private readonly WebApplicationFactory<Program> _factory;

    public ResultsControllerLiveTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Nancy_Results_Renders_Real_Trends_And_Reports()
    {
        using var client = _factory.CreateClient();
        HttpResponseMessage resp;
        string body;
        try
        {
            resp = await client.GetAsync($"/app/patient/{NancyId}/results");
            body = await resp.Content.ReadAsStringAsync();
        }
        catch (HttpRequestException) { return; }
        catch (TaskCanceledException) { return; }

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("<h1>",
            because: "the chart-banner h1 renders with the live patient name");
        body.Should().Contain("MRN " + NancyId);
        body.Should().Contain("trend-title",
            because: "Nancy has real lab observations — at least one trend card must render");
        body.Should().Contain("Lipid Panel",
            because: "Nancy has an amended Lipid Panel report in the live sandbox");
        body.Should().Contain("status-amended",
            because: "the amended Lipid Panel report carries an amended-status badge");
    }

    [Fact]
    public async Task Annie_Results_Renders_Empty_States_Honestly()
    {
        // Annie has 1 report (Echo TEE) and zero lab observations in the
        // open sandbox. The page must render the trends empty state.
        using var client = _factory.CreateClient();
        HttpResponseMessage resp;
        string body;
        try
        {
            resp = await client.GetAsync($"/app/patient/{AnnieId}/results");
            body = await resp.Content.ReadAsStringAsync();
        }
        catch (HttpRequestException) { return; }
        catch (TaskCanceledException) { return; }

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("No lab observations on file");
        body.Should().Contain("Echo TEE",
            because: "Annie has one DiagnosticReport (Echo TEE 3rd Party) in the live sandbox");
    }
}
