using System.Net;
using Chiron.Cds.Engine;
using Chiron.Cds.Web.CdsHooks.Models;
using Chiron.Cds.Web.Configuration;
using Chiron.Cds.Web.FhirClient;
using Chiron.Cds.Web.Mappers;
using Chiron.Cds.Web.Panel;
using Chiron.Cds.Web.Tenancy;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReasoningEngine = Chiron.Cds.Engine.Engine;

namespace Chiron.Cds.Web.IntegrationTests;

/// <summary>
/// Controller-level tests for <c>/app/panel</c>, <c>/app/patient/{id}</c>,
/// and <c>/app/search</c> that hit zero external services. <see cref="PanelService"/>
/// and <see cref="PatientSearchService"/> are replaced with deterministic
/// stubs in DI; the assertions cover the controller's mapping + error
/// branches that the live tests can't drive without artificial outages.
/// </summary>
public class PanelControllerOfflineTests : IClassFixture<PanelControllerOfflineTests.Factory>
{
    private readonly Factory _factory;

    public PanelControllerOfflineTests(Factory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Panel_Renders_Stubbed_Rows_With_Mixed_Success_And_Error()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/app/panel");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();

        body.Should().Contain("Happy, Patient");
        body.Should().Contain("Broken, Patient");
        body.Should().Contain("Error — FHIR 403 Forbidden",
            because: "the row with PanelEntry.Error set renders 'Error — {detail}' into the flag");
        body.Should().Contain("href=\"/app/patient/p-good\"");
        body.Should().Contain("href=\"/app/patient/p-bad\"");
    }

    [Fact]
    public async Task Patient_Route_Renders_Error_Banner_When_Entry_Has_Error()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/app/patient/p-bad");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("could not be loaded");
        body.Should().Contain("FHIR 403 Forbidden");
        body.Should().Contain("connected FHIR endpoint returned an error");
    }

    [Fact]
    public async Task Patient_Route_404s_When_Service_Returns_Null()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/app/patient/p-missing");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Search_Renders_Stubbed_Hits()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/app/search?q=stub");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("Stubbed, Patient");
        body.Should().Contain("href=\"/app/patient/s-1\"");
        body.Should().Contain("results-count\">1</span>");
    }

    [Fact]
    public async Task Search_With_Empty_Query_Skips_Service_Call_And_Shows_Hint()
    {
        // Reset the static call-spy first — other tests in this class
        // may have populated it.
        StubPatientSearchService.LastQuery = null;

        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/app/search");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("Start typing");
        StubPatientSearchService.LastQuery.Should().BeNull(
            because: "the controller's empty-query short-circuit must not reach the search service");
    }

    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<PanelService>();
                services.AddScoped<PanelService>(sp => new StubPanelService(
                    sp.GetRequiredService<IOptions<PanelOptions>>(),
                    sp.GetRequiredService<TenantRegistry>(),
                    sp.GetRequiredService<PatientChartFetcher>(),
                    sp.GetRequiredService<FhirToFactMapper>(),
                    sp.GetRequiredService<AlertToCdsCardMapper>(),
                    sp.GetRequiredService<ReasoningEngine>(),
                    NullLogger<PanelService>.Instance));

                services.RemoveAll<PatientSearchService>();
                services.AddScoped<PatientSearchService>(sp => new StubPatientSearchService(
                    sp.GetRequiredService<TenantRegistry>(),
                    NullLogger<PatientSearchService>.Instance));
            });
        }
    }

    private sealed class StubPanelService : PanelService
    {
        public StubPanelService(
            IOptions<PanelOptions> options,
            TenantRegistry tenants,
            PatientChartFetcher fetcher,
            FhirToFactMapper factMapper,
            AlertToCdsCardMapper cardMapper,
            ReasoningEngine engine,
            ILogger<PanelService> log)
            : base(options, tenants, fetcher, factMapper, cardMapper, engine, log) { }

        public override Task<IReadOnlyList<PanelEntry>> GetPanelAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PanelEntry>>(new[]
            {
                Good("p-good", "Happy, Patient", "8:00 AM"),
                Bad("p-bad", "Broken, Patient", "8:10 AM"),
            });

        public override Task<PanelEntry?> GetPatientAsync(string patientId, CancellationToken ct) =>
            Task.FromResult<PanelEntry?>(patientId switch
            {
                "p-good" => Good(patientId, "Happy, Patient", "8:00 AM"),
                "p-bad" => Bad(patientId, "Broken, Patient", "8:10 AM"),
                _ => null,
            });

        private static PanelEntry Good(string id, string name, string slot) => new(
            PatientId: id,
            DisplayName: name,
            AppointmentTime: slot,
            AgeSex: "45y · Female",
            Inputs: null,
            Cards: Array.Empty<CdsCard>(),
            Error: null);

        private static PanelEntry Bad(string id, string name, string slot) => new(
            PatientId: id,
            DisplayName: name,
            AppointmentTime: slot,
            AgeSex: "",
            Inputs: null,
            Cards: Array.Empty<CdsCard>(),
            Error: "FHIR 403 Forbidden");
    }

    private sealed class StubPatientSearchService : PatientSearchService
    {
        public static string? LastQuery;

        public StubPatientSearchService(TenantRegistry tenants, ILogger<PatientSearchService> log)
            : base(tenants, log) { }

        public override Task<IReadOnlyList<PatientSearchHit>> SearchAsync(string query, CancellationToken ct)
        {
            LastQuery = query;
            return Task.FromResult<IReadOnlyList<PatientSearchHit>>(new[]
            {
                new PatientSearchHit("s-1", "Stubbed, Patient", "female", "1980-01-01"),
            });
        }
    }
}
