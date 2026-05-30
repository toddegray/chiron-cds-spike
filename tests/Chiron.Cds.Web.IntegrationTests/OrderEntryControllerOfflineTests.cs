using System.Net;
using Chiron.Cds.Engine;
using Chiron.Cds.Web.CdsHooks.Models;
using Chiron.Cds.Web.Configuration;
using Chiron.Cds.Web.FhirClient;
using Chiron.Cds.Web.Mappers;
using Chiron.Cds.Web.Panel;
using Chiron.Cds.Web.Tenancy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReasoningEngine = Chiron.Cds.Engine.Engine;
using Task = System.Threading.Tasks.Task;

namespace Chiron.Cds.Web.IntegrationTests;

public class OrderEntryControllerOfflineTests : IClassFixture<OrderEntryControllerOfflineTests.Factory>
{
    private readonly Factory _factory;

    public OrderEntryControllerOfflineTests(Factory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Get_Renders_Empty_Form_With_Workflow_Rail_And_Pharmacy_Options()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/app/patient/p1/orders");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("name=\"DrugName\"");
        body.Should().Contain("class=\"tab active\" href=\"/app/patient/p1/orders\"", because: "the orders tab is marked active");
        body.Should().Contain("href=\"/app/patient/p1/results\"");
        body.Should().Contain("<option value=\"stub-pharmacy\"");
        body.Should().Contain(">Sign order</button>",
            because: "the single-button UX shows just Sign — no separate Check button");
        body.Should().NotContain("Check CDS",
            because: "the old two-button design is gone");
    }

    [Fact]
    public async Task Post_With_Blocked_Status_Renders_Cards_And_Acknowledge_Boxes()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsync("/app/patient/p-block/orders", new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("DrugName", "warfarin"),
            new KeyValuePair<string, string>("Strength", "5 mg"),
            new KeyValuePair<string, string>("Refills", "0"),
        }));
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("class=\"banner warn\"");
        body.Should().Contain("Acknowledge 1 critical alert");
        body.Should().Contain("Stubbed critical card");
        body.Should().Contain("name=\"Acknowledged\" value=\"fp-critical\"",
            because: "the blocked path renders a functional ack checkbox");
        body.Should().Contain("Sign with 1 acknowledgement");
    }

    [Fact]
    public async Task Post_With_Acknowledgement_Unblocks_The_Write_Path()
    {
        // Resubmitting with Acknowledged=fp-critical flips the stub from
        // Blocked to a successful write.
        using var client = _factory.CreateClient();
        var resp = await client.PostAsync("/app/patient/p-block/orders", new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("DrugName", "warfarin"),
            new KeyValuePair<string, string>("Strength", "5 mg"),
            new KeyValuePair<string, string>("Refills", "0"),
            new KeyValuePair<string, string>("Acknowledged", "fp-critical"),
        }));
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("class=\"banner ok\"");
        body.Should().Contain("MR-stub-99");
    }

    [Fact]
    public async Task Post_Without_Session_Renders_Clean_Sign_In_Pane()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsync("/app/patient/p-no-session/orders", new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("DrugName", "metformin"),
            new KeyValuePair<string, string>("Strength", "500 mg"),
            new KeyValuePair<string, string>("Refills", "3"),
        }));
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("class=\"signin-pane\"");
        body.Should().Contain("Sign in to write orders");
        body.Should().Contain("href=\"/smart/launch\"");
        body.Should().NotContain("preview-json",
            because: "the dev-toolbox JSON dump is gone — no synthesised FHIR shown to the user");
        body.Should().NotContain("Pending FHIR payload");
    }

    [Fact]
    public async Task Post_With_Token_And_Clear_CDS_Writes_And_Returns_Success_Page()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsync("/app/patient/p-ok/orders?session=test-session", new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("DrugName", "ibuprofen"),
            new KeyValuePair<string, string>("Strength", "400 mg"),
            new KeyValuePair<string, string>("Refills", "0"),
        }));
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("class=\"banner ok\"");
        body.Should().Contain("MR-stub-99");
        body.Should().Contain("href=\"/app/patient/p-ok\"");
    }

    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Chiron:Pharmacies:Entries:0:Id"] = "stub-pharmacy",
                    ["Chiron:Pharmacies:Entries:0:DisplayName"] = "Stub Pharmacy",
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<OrderEntryService>();
                services.AddScoped<OrderEntryService>(sp => new StubOrderEntryService(
                    sp.GetRequiredService<TenantRegistry>(),
                    sp.GetRequiredService<PatientChartFetcher>(),
                    sp.GetRequiredService<FhirToFactMapper>(),
                    sp.GetRequiredService<AlertToCdsCardMapper>(),
                    sp.GetRequiredService<ReasoningEngine>(),
                    sp.GetRequiredService<IOptions<PharmacyOptions>>(),
                    NullLogger<OrderEntryService>.Instance));

                services.RemoveAll<Chiron.Cds.Web.SmartLaunch.ITokenStore>();
                services.AddSingleton<Chiron.Cds.Web.SmartLaunch.ITokenStore, StubTokenStore>();
            });
        }
    }

    private sealed class StubOrderEntryService : OrderEntryService
    {
        public StubOrderEntryService(
            TenantRegistry tenants, PatientChartFetcher fetcher, FhirToFactMapper factMapper,
            AlertToCdsCardMapper cardMapper, ReasoningEngine engine,
            IOptions<PharmacyOptions> pharmacies, ILogger<OrderEntryService> log)
            : base(tenants, fetcher, factMapper, cardMapper, engine, pharmacies, log) { }

        public override Task<OrderEvaluation> EvaluateAsync(string patientId, OrderDraft draft, CancellationToken ct) =>
            Task.FromResult(new OrderEvaluation(
                Cards: new[]
                {
                    new CdsCard("Stubbed warning card", "warning",
                        new CdsCardSource("CDS"), "stub detail", "fp1", Array.Empty<CdsCoding>()),
                },
                ChartError: null));

        public override Task<OrderWriteResult> SignAsync(
            string patientId, OrderDraft draft, string? accessToken,
            IReadOnlySet<string> acknowledgedFingerprints, CancellationToken ct)
        {
            return Task.FromResult(patientId switch
            {
                "p-block" when !acknowledgedFingerprints.Contains("fp-critical") =>
                    OrderWriteResult.Blocked(
                        "Acknowledge 1 critical alert to sign.",
                        new[]
                        {
                            new CdsCard("Stubbed critical card", "critical",
                                new CdsCardSource("CDS"), "do not proceed", "fp-critical", Array.Empty<CdsCoding>()),
                        }),
                "p-block" => OrderWriteResult.Ok("MR-stub-99"),
                "p-no-session" => OrderWriteResult.NotAuthorised(),
                "p-ok" => OrderWriteResult.Ok("MR-stub-99"),
                _ => OrderWriteResult.Failed("Unexpected stub branch."),
            });
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
