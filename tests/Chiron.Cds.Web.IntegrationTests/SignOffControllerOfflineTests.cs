using System.Net;
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

public class SignOffControllerOfflineTests : IClassFixture<SignOffControllerOfflineTests.Factory>
{
    private readonly Factory _factory;

    public SignOffControllerOfflineTests(Factory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Get_Renders_In_Progress_Encounter_With_Close_Button()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/app/patient/p1/signoff");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("Outpatient");
        body.Should().Contain("status-inprogress");
        body.Should().Contain(">Sign off and close</button>");
        body.Should().Contain("name=\"EncounterId\" value=\"enc-active\"");
        body.Should().MatchRegex("rail-step active\"><a href=\"/app/patient/p1/signoff\"",
            because: "the Sign off step on the rail is marked active on the sign-off page");
    }

    [Fact]
    public async Task Post_Without_Session_Renders_Sign_In_Pane()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsync("/app/patient/p1/signoff", new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("EncounterId", "enc-active"),
        }));
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("class=\"signin-pane\"");
        body.Should().Contain("Sign in to close encounters");
        body.Should().Contain("href=\"/smart/launch\"");
    }

    [Fact]
    public async Task Post_With_Token_Returns_Closed_Banner_With_Server_Id()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsync("/app/patient/p1/signoff?session=test-session", new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("EncounterId", "enc-active"),
        }));
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("class=\"banner ok\"");
        body.Should().Contain("<code>enc-active</code>");
        body.Should().Contain("href=\"/app/panel\"",
            because: "the closed-banner offers a 'Next patient' link back to the panel");
    }

    [Fact]
    public async Task Post_Missing_EncounterId_Returns_BadRequest()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsync("/app/patient/p1/signoff",
            new FormUrlEncodedContent(Array.Empty<KeyValuePair<string, string>>()));
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<EncounterCloseService>();
                services.AddScoped<EncounterCloseService>(sp => new StubEncounterCloseService(
                    sp.GetRequiredService<TenantRegistry>(),
                    NullLogger<EncounterCloseService>.Instance));

                services.RemoveAll<Chiron.Cds.Web.SmartLaunch.ITokenStore>();
                services.AddSingleton<Chiron.Cds.Web.SmartLaunch.ITokenStore, StubTokenStore>();
            });
        }
    }

    private sealed class StubEncounterCloseService : EncounterCloseService
    {
        public StubEncounterCloseService(TenantRegistry tenants, ILogger<EncounterCloseService> log)
            : base(tenants, log) { }

        public override Task<SignOffPageData> GetForPatientAsync(string patientId, CancellationToken ct) =>
            Task.FromResult(new SignOffPageData(
                Encounters: new[]
                {
                    new EncounterSummary("enc-active", "Outpatient", "ambulatory", "InProgress",
                        DateTimeOffset.Parse("2026-05-25T09:00:00Z"), null),
                },
                Error: null));

        public override Task<EncounterCloseResult> CloseAsync(
            string patientId, string encounterId, string? accessToken, CancellationToken ct) =>
            Task.FromResult(string.IsNullOrEmpty(accessToken)
                ? EncounterCloseResult.NotAuthorised()
                : EncounterCloseResult.Ok(encounterId));
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
