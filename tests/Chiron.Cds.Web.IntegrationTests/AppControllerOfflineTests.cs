using System.Net;
using Chiron.Cds.Web.FhirClient;
using Chiron.Cds.Web.SmartLaunch;
using Chiron.Cds.Web.Tenancy;
using FluentAssertions;
using Hl7.Fhir.Model;
using Hl7.Fhir.Rest;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Task = System.Threading.Tasks.Task;

namespace Chiron.Cds.Web.IntegrationTests;

/// <summary>
/// Offline tests for <c>GET /app</c>'s patient resolution. A user-scoped
/// provider launch carries no patient in the token, so the route accepts an
/// optional <c>?patient=</c>; a launch-bound patient always wins. The chart
/// fetch is stubbed so the engine + render run without touching a live EHR.
/// </summary>
public class AppControllerOfflineTests : IClassFixture<AppControllerOfflineTests.Factory>
{
    private readonly Factory _factory;

    public AppControllerOfflineTests(Factory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Launch_Bound_Patient_Wins_Over_Query_Param()
    {
        SeedSession("sess-bound", patientId: "p-session");
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/app?session=sess-bound&patient=p-query");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("Session for patient p-session",
            because: "a patient bound to the launch token takes precedence over the query param");
        body.Should().NotContain("p-query",
            because: "the query patient is ignored when the session already carries one");
    }

    [Fact]
    public async Task Query_Patient_Used_When_Session_Has_No_Patient()
    {
        SeedSession("sess-empty", patientId: "");
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/app?session=sess-empty&patient=p-query");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("Session for patient p-query",
            because: "a user-scoped provider launch has no patient, so the query param selects one");
    }

    [Fact]
    public async Task No_Patient_Anywhere_Renders_Selection_Hint()
    {
        SeedSession("sess-none", patientId: "");
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/app?session=sess-none");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("append ?patient=",
            because: "with no patient in the token and none in the query, the page prompts the clinician to choose one");
    }

    [Fact]
    public async Task Fhir_Error_Diagnostic_Reports_The_Resolved_Patient()
    {
        // Session has no patient, so the query param selects one; the chart
        // fetch then 403s. The diagnostic must name the query-resolved patient,
        // proving the catch block reports `resolved.PatientId`, not the empty
        // session patient.
        SeedSession("sess-403", patientId: "");
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/app?session=sess-403&patient=p-403");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("HTTP 403 (Forbidden)");
        body.Should().Contain("Patient: p-403",
            because: "the diagnostic surfaces the query-resolved patient, not the empty launch patient");
    }

    private void SeedSession(string sessionId, string patientId)
    {
        var store = _factory.Services.GetRequiredService<ITokenStore>();
        store.SaveSession(new SmartSession(
            SessionId: sessionId,
            TenantId: "epic-sandbox",
            AccessToken: "tok",
            RefreshToken: null,
            PatientId: patientId,
            EncounterId: null,
            IdToken: null,
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(30),
            GrantedScopes: new[] { "openid", "fhirUser", "user/Patient.read" }));
    }

    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<PatientChartFetcher>();
                services.AddSingleton<PatientChartFetcher, StubChartFetcher>();
            });
        }
    }

    private sealed class StubChartFetcher : PatientChartFetcher
    {
        public StubChartFetcher() : base(NullLogger<PatientChartFetcher>.Instance) { }

        public override Task<PatientChart> FetchAsync(
            TenantConfig tenant, string accessToken, string patientId, string? encounterId, CancellationToken ct)
        {
            if (patientId == "p-403")
                throw new FhirOperationException("denied", HttpStatusCode.Forbidden);
            return Task.FromResult(new PatientChart(
                Patient: new Patient { Id = patientId, Gender = AdministrativeGender.Female, BirthDate = "1980-01-01" },
                Conditions: Array.Empty<Condition>(),
                Observations: Array.Empty<Observation>(),
                MedicationRequests: Array.Empty<MedicationRequest>(),
                Allergies: Array.Empty<AllergyIntolerance>(),
                Immunizations: Array.Empty<Immunization>(),
                Procedures: Array.Empty<Procedure>(),
                Encounter: null));
        }
    }
}
