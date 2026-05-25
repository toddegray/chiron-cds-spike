using System.Net;
using System.Net.Http.Headers;
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
    public async Task Get_Renders_Empty_Form_With_Chart_Tabs_And_Pharmacy_Options()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/app/patient/p1/orders");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("name=\"DrugName\"");
        body.Should().Contain("chart-tab active",
            because: "the Orders tab is active on this route");
        body.Should().Contain("href=\"/app/patient/p1/results\"",
            because: "Results tab links to the per-patient results route");
        body.Should().Contain("<option value=\"stub-pharmacy\"",
            because: "the configured pharmacy renders in the dropdown");
    }

    [Fact]
    public async Task Post_Check_Returns_Inline_Cards_And_Info_Banner()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsync("/app/patient/p1/orders", new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("DrugName", "metformin"),
            new KeyValuePair<string, string>("Strength", "500 mg"),
            new KeyValuePair<string, string>("Refills", "3"),
            new KeyValuePair<string, string>("Action", "check"),
        }));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("class=\"banner info\"");
        body.Should().Contain("Stubbed warning card",
            because: "the stub returns a card the renderer must echo into the page");
    }

    [Fact]
    public async Task Post_Sign_Without_Session_Returns_Not_Authorised_Banner()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsync("/app/patient/p1/orders", new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("DrugName", "metformin"),
            new KeyValuePair<string, string>("Strength", "500 mg"),
            new KeyValuePair<string, string>("Refills", "0"),
            new KeyValuePair<string, string>("Action", "sign"),
        }));
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("class=\"banner warn\"");
        body.Should().Contain("authenticated SMART session");
    }

    [Fact]
    public async Task Post_Sign_With_Blocked_Status_Renders_Cards_And_Warning()
    {
        // Patient "p-block" triggers the stub's Blocked branch.
        using var client = _factory.CreateClient();
        var resp = await client.PostAsync("/app/patient/p-block/orders?session=test-session", new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("DrugName", "warfarin"),
            new KeyValuePair<string, string>("Strength", "5 mg"),
            new KeyValuePair<string, string>("Refills", "0"),
            new KeyValuePair<string, string>("Action", "sign"),
        }));
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("class=\"banner warn\"");
        body.Should().Contain("Critical CDS alerts are not acknowledged");
        body.Should().Contain("Stubbed critical card");
    }

    [Fact]
    public async Task Post_Sign_With_Token_Returns_Success_Page()
    {
        // Patient "p-ok" triggers the stub's successful write.
        using var client = _factory.CreateClient();
        var resp = await client.PostAsync("/app/patient/p-ok/orders?session=test-session", new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("DrugName", "ibuprofen"),
            new KeyValuePair<string, string>("Strength", "400 mg"),
            new KeyValuePair<string, string>("Refills", "0"),
            new KeyValuePair<string, string>("Action", "sign"),
        }));
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("class=\"banner ok\"");
        body.Should().Contain("MR-stub-99");
        body.Should().Contain("href=\"/app/patient/p-ok\"",
            because: "the success page links back to the Visit Brief");
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

                // For session-token tests, seed a stub session.
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
                        new CdsCardSource("Chiron"), "stub detail", "fp1", Array.Empty<CdsCoding>()),
                },
                ChartError: null));

        public override Task<OrderWriteResult> SignAsync(
            string patientId, OrderDraft draft, string? accessToken,
            IReadOnlySet<string> acknowledgedFingerprints, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(accessToken))
                return Task.FromResult(OrderWriteResult.NotAuthorised(
                    "Signing requires an authenticated SMART session — open /smart/launch first."));
            return Task.FromResult(patientId switch
            {
                "p-block" => OrderWriteResult.Blocked(
                    "Critical CDS alerts are not acknowledged: fp-critical",
                    new[]
                    {
                        new CdsCard("Stubbed critical card", "critical",
                            new CdsCardSource("Chiron"), "do not proceed", "fp-critical", Array.Empty<CdsCoding>()),
                    }),
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
