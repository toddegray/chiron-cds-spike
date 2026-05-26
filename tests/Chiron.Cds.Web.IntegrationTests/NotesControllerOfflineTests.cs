using System.Net;
using Chiron.Cds.Web.FhirClient;
using Chiron.Cds.Web.Panel;
using Chiron.Cds.Web.Tenancy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Task = System.Threading.Tasks.Task;

namespace Chiron.Cds.Web.IntegrationTests;

public class NotesControllerOfflineTests : IClassFixture<NotesControllerOfflineTests.Factory>
{
    private readonly Factory _factory;

    public NotesControllerOfflineTests(Factory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Get_Renders_Form_With_Pre_Filled_Sections_And_History()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/app/patient/p1/notes");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();

        body.Should().Contain("name=\"Subjective\"");
        body.Should().Contain("name=\"Assessment\"");
        body.Should().Contain("Stub Assessment line",
            because: "the stub's pre-filled Assessment text round-trips through the form");
        body.Should().Contain("Stubbed prior note",
            because: "the stub history populates the right-rail list");
        body.Should().MatchRegex("rail-step active\"><a href=\"/app/patient/p1/notes\"",
            because: "the Notes step on the rail is marked active on this route");
    }

    [Fact]
    public async Task Post_Without_Session_Renders_Sign_In_Pane()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsync("/app/patient/p1/notes", new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("Subjective", "Patient reports cough."),
            new KeyValuePair<string, string>("Plan", "Symptomatic care."),
        }));
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("class=\"signin-pane\"");
        body.Should().Contain("Sign in to save the note");
        body.Should().Contain("href=\"/smart/launch\"");
        body.Should().NotContain("\"resourceType\"",
            because: "no FHIR JSON dump anywhere on the page");
    }

    [Fact]
    public async Task Post_With_Token_Returns_Signed_Banner_With_Server_Id()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsync("/app/patient/p1/notes?session=test-session", new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("Subjective", "Patient reports cough."),
            new KeyValuePair<string, string>("Plan", "Symptomatic care."),
        }));
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("class=\"banner ok\"");
        body.Should().Contain("DR-stub-77");
        body.Should().Contain("href=\"/app/patient/p1\"");
    }

    [Fact]
    public async Task Post_With_Empty_Note_Returns_Failed_Banner()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsync("/app/patient/p1/notes?session=test-session", new FormUrlEncodedContent(
            Array.Empty<KeyValuePair<string, string>>()));
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("class=\"banner err\"");
        body.Should().Contain("at least one section");
    }

    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<NoteEntryService>();
                services.AddScoped<NoteEntryService>(sp => new StubNoteEntryService(
                    sp.GetRequiredService<TenantRegistry>(),
                    sp.GetRequiredService<PatientChartFetcher>(),
                    NullLogger<NoteEntryService>.Instance));

                services.RemoveAll<Chiron.Cds.Web.SmartLaunch.ITokenStore>();
                services.AddSingleton<Chiron.Cds.Web.SmartLaunch.ITokenStore, StubTokenStore>();
            });
        }
    }

    private sealed class StubNoteEntryService : NoteEntryService
    {
        public StubNoteEntryService(
            TenantRegistry tenants, PatientChartFetcher fetcher,
            ILogger<NoteEntryService> log)
            : base(tenants, fetcher, log) { }

        public override Task<NotesPageData> GetForPatientAsync(string patientId, CancellationToken ct) =>
            Task.FromResult(new NotesPageData(
                History: new[]
                {
                    new NoteSummary("Stubbed prior note", "Clinical Note", "current",
                        DateTimeOffset.Parse("2024-06-01T00:00:00Z")),
                },
                Draft: new NoteDraft(
                    Subjective: string.Empty,
                    Objective: string.Empty,
                    Assessment: "- Stub Assessment line",
                    Plan: "Continue:\n- Stub Plan line"),
                Error: null));

        public override Task<NoteWriteResult> SignAsync(
            string patientId, NoteDraft draft, string? accessToken, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(accessToken)) return Task.FromResult(NoteWriteResult.NotAuthorised());
            if (IsEmpty(draft)) return Task.FromResult(NoteWriteResult.Failed("A note must include at least one section."));
            return Task.FromResult(NoteWriteResult.Ok("DR-stub-77"));
        }
    }

    private sealed class StubTokenStore : Chiron.Cds.Web.SmartLaunch.ITokenStore
    {
        public void SavePending(Chiron.Cds.Web.SmartLaunch.PendingLaunch launch) { }
        public Chiron.Cds.Web.SmartLaunch.PendingLaunch? TakePending(string state) => null;
        public void SaveSession(Chiron.Cds.Web.SmartLaunch.SmartSession session) { }
        public void RemoveSession(string sessionId) { }
        public Chiron.Cds.Web.SmartLaunch.SmartSession? GetSession(string sessionId) =>
            sessionId == "test-session"
                ? new Chiron.Cds.Web.SmartLaunch.SmartSession(
                    SessionId: sessionId,
                    TenantId: "cerner-code-sandbox",
                    AccessToken: "stub-access-token",
                    RefreshToken: null,
                    PatientId: "p1",
                    EncounterId: null,
                    IdToken: null,
                    ExpiresAt: DateTimeOffset.UtcNow.AddHours(1),
                    GrantedScopes: Array.Empty<string>())
                : null;
    }
}
