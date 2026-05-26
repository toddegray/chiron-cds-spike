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

public class ServiceRequestControllerOfflineTests : IClassFixture<ServiceRequestControllerOfflineTests.Factory>
{
    private readonly Factory _factory;
    public ServiceRequestControllerOfflineTests(Factory factory) => _factory = factory;

    [Fact]
    public async Task Get_Lab_Page_Renders_Form_And_History()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/app/patient/p1/orders/labs");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("class=\"order-subnav\"");
        body.Should().Contain("Labs</a>");
        body.Should().Contain("Stub lab order");
        body.Should().Contain("name=\"OrderText\"");
    }

    [Fact]
    public async Task Get_Imaging_Page_Renders_Form_And_Imaging_History()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/app/patient/p1/orders/imaging");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("Stub imaging order");
        body.Should().Contain("class=\"active\">Imaging</a>");
    }

    [Fact]
    public async Task Post_Lab_Without_Session_Renders_Sign_In_Pane()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsync("/app/patient/p1/orders/labs", new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("OrderText", "Lipid Panel"),
            new KeyValuePair<string, string>("Reason", "CV risk"),
            new KeyValuePair<string, string>("Priority", "routine"),
        }));
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("class=\"signin-pane\"");
        body.Should().Contain("Sign in to place laboratory orders");
    }

    [Fact]
    public async Task Post_Imaging_With_Session_Returns_Signed_Banner()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsync("/app/patient/p1/orders/imaging?session=test-session", new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("OrderText", "Chest X-ray PA/LAT"),
            new KeyValuePair<string, string>("Reason", "cough"),
            new KeyValuePair<string, string>("Priority", "routine"),
        }));
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("class=\"banner ok\"");
        body.Should().Contain("sr-stub-7");
    }

    [Fact]
    public async Task Post_Empty_Order_Returns_Failed_Banner()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsync("/app/patient/p1/orders/labs?session=test-session", new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("OrderText", ""),
        }));
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("class=\"banner err\"");
        body.Should().Contain("test or procedure");
    }

    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ServiceRequestService>();
                services.AddScoped<ServiceRequestService>(sp => new StubServiceRequestService(
                    sp.GetRequiredService<TenantRegistry>(),
                    NullLogger<ServiceRequestService>.Instance));

                services.RemoveAll<Chiron.Cds.Web.SmartLaunch.ITokenStore>();
                services.AddSingleton<Chiron.Cds.Web.SmartLaunch.ITokenStore, StubTokenStore>();
            });
        }
    }

    private sealed class StubServiceRequestService : ServiceRequestService
    {
        public StubServiceRequestService(TenantRegistry tenants, ILogger<ServiceRequestService> log)
            : base(tenants, log) { }

        public override Task<ServiceRequestPageData> GetForPatientAsync(
            string patientId, ServiceRequestCategory category, CancellationToken ct) =>
            Task.FromResult(new ServiceRequestPageData(
                History: new[]
                {
                    new ServiceRequestSummary(
                        Name: category == ServiceRequestCategory.Laboratory ? "Stub lab order" : "Stub imaging order",
                        Status: "Active",
                        Priority: "Routine",
                        Reason: null,
                        OccurrenceAt: DateTimeOffset.Parse("2026-01-15T00:00:00Z")),
                },
                Error: null));

        public override Task<ServiceRequestWriteResult> SignAsync(
            string patientId, ServiceRequestDraft draft, ServiceRequestCategory category,
            string? accessToken, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(draft.OrderText))
                return Task.FromResult(ServiceRequestWriteResult.Failed("Enter a test or procedure to order."));
            if (string.IsNullOrEmpty(accessToken))
                return Task.FromResult(ServiceRequestWriteResult.NotAuthorised());
            return Task.FromResult(ServiceRequestWriteResult.Ok("sr-stub-7"));
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
