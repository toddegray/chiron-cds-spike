using System.Net;
using Chiron.Cds.Web.FhirClient;
using Chiron.Cds.Web.Panel;
using Chiron.Cds.Web.Tenancy;
using FluentAssertions;
using Hl7.Fhir.Model;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using FhirOperationException = Hl7.Fhir.Rest.FhirOperationException;
using Task = System.Threading.Tasks.Task;

namespace Chiron.Cds.Web.IntegrationTests;

/// <summary>
/// Offline tests for <c>GET /app/patient/{id}/results</c>: the controller
/// wiring + workflow-rail assembly + error-banner branch, with the
/// <see cref="ResultReviewService"/> replaced by a deterministic stub.
/// </summary>
public class ResultsControllerOfflineTests : IClassFixture<ResultsControllerOfflineTests.Factory>
{
    private readonly Factory _factory;

    public ResultsControllerOfflineTests(Factory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Renders_Trend_And_Report_Sections_Plus_Workflow_Rail()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/app/patient/p-good/results");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();

        body.Should().Contain("<h1>Happy, Patient</h1>");
        body.Should().Contain("Glucose Level",
            because: "the stubbed lab observation projects into the trends section");
        body.Should().Contain("Temperature Oral",
            because: "the stubbed vital observation merges into the same trends section as labs");
        body.Should().Contain("Lipid Panel",
            because: "the stubbed DiagnosticReport renders in the reports section");
        body.Should().Contain("status-amended",
            because: "the report status pill carries through to the rendered class");
        body.Should().Contain("class=\"chart-rail\"",
            because: "the patient page renders the visit-workflow rail");
        body.Should().Contain("href=\"/app/patient/p-good\"",
            because: "the Visit brief step on the rail links back to the chart root");
        body.Should().MatchRegex("rail-step active\"><a href=\"/app/patient/p-good/results\"",
            because: "the Results step is marked active on the rail when the Results page is rendered");
    }

    [Fact]
    public async Task Error_Path_Renders_Banner_Without_Sections()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/app/patient/p-bad/results");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();

        body.Should().Contain("Chart results could not be loaded");
        body.Should().Contain("FHIR 403 Forbidden");
        body.Should().NotContain("Lab trends",
            because: "trends/reports sections are suppressed in the error state");
    }

    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ResultReviewService>();
                services.AddScoped<ResultReviewService>(sp => new StubResultReviewService(
                    new StubReadConnection(sp.GetRequiredService<TenantRegistry>().Default),
                    NullLogger<ResultReviewService>.Instance));
            });
        }
    }

    private sealed class StubResultReviewService : ResultReviewService
    {
        public StubResultReviewService(FhirReadConnection connection, ILogger<ResultReviewService> log)
            : base(connection, log) { }

        protected override Task<(Patient? Patient, Bundle? Reports, Bundle? Labs, Bundle? Vitals)>
            FetchAsync(TenantConfig tenant, string? accessToken, string patientId, CancellationToken ct)
        {
            if (patientId == "p-bad")
                throw new FhirOperationException("denied", HttpStatusCode.Forbidden);

            var patient = new Patient
            {
                Id = patientId,
                Gender = AdministrativeGender.Female,
                BirthDate = "1980-01-01",
                Name = { new HumanName { Text = "Happy, Patient" } },
            };
            var report = new DiagnosticReport
            {
                Id = "R1",
                Code = new CodeableConcept { Text = "Lipid Panel" },
                Status = DiagnosticReport.DiagnosticReportStatus.Amended,
                Category = new List<CodeableConcept>
                {
                    new() { Coding = new List<Coding> { new() { Display = "Laboratory" } } },
                },
                Issued = DateTimeOffset.Parse("2025-01-13T06:46:59Z"),
            };
            var lab = new Observation
            {
                Code = new CodeableConcept
                {
                    Text = "Glucose Level",
                    Coding = new List<Coding> { new() { System = "http://loinc.org", Code = "2345-7" } },
                },
                Value = new Quantity { Value = 6m, Unit = "mmol/L" },
                Effective = new FhirDateTime("2026-05-21T00:00:00Z"),
            };
            var vital = new Observation
            {
                Code = new CodeableConcept
                {
                    Text = "Temperature Oral",
                    Coding = new List<Coding> { new() { System = "http://loinc.org", Code = "8331-1" } },
                },
                Value = new Quantity { Value = 37.0m, Unit = "Cel" },
                Effective = new FhirDateTime("2026-05-21T00:00:00Z"),
            };
            return Task.FromResult<(Patient?, Bundle?, Bundle?, Bundle?)>((
                patient,
                BundleOf(report),
                BundleOf(lab),
                BundleOf(vital)));
        }

        private static Bundle BundleOf<T>(T resource) where T : Resource => new()
        {
            Entry = new List<Bundle.EntryComponent> { new() { Resource = resource } },
        };
    }
}
