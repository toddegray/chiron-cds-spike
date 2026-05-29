using System.Net;
using System.Net.Http.Headers;
using Chiron.Cds.Web.FhirClient;
using Chiron.Cds.Web.Tenancy;
using FluentAssertions;
using Hl7.Fhir.Model;
using Task = System.Threading.Tasks.Task;

namespace Chiron.Cds.Web.IntegrationTests;

/// <summary>
/// Pins that a FHIR base URL with a path segment (e.g. ".../r4") is preserved
/// in full when the client builds resource URLs. A base lacking a trailing
/// slash loses its last segment under standard URI combination
/// (".../r4" + "Patient/123" → ".../Patient/123"), which the server answers
/// with a 404 — the exact failure that broke the live Epic chart read.
/// </summary>
public class TenantFhirClientBasePathTests
{
    private static TenantConfig Tenant() => new(
        Id: "t", DisplayName: "T", ClientId: "c", ClientSecret: null,
        FhirBaseUrl: new Uri("https://fhir.test/api/FHIR/R4"),
        FhirOpenBaseUrl: null, Scopes: "");

    [Fact]
    public async Task ReadAsync_Preserves_Full_Base_Path()
    {
        var captured = new CapturingHandler("""{ "resourceType": "Patient", "id": "123" }""");
        using var http = new HttpClient(captured);
        using var client = new TenantFhirClient(Tenant(), accessToken: "tok", http);

        await client.ReadAsync<Patient>("123", CancellationToken.None);

        captured.LastRequest!.RequestUri!.AbsolutePath.Should().Be("/api/FHIR/R4/Patient/123",
            because: "the full FHIR base path (including the R4 version segment) must survive URL building");
    }

    [Fact]
    public async Task SearchAsync_Preserves_Full_Base_Path()
    {
        var captured = new CapturingHandler("""{ "resourceType": "Bundle", "type": "searchset" }""");
        using var http = new HttpClient(captured);
        using var client = new TenantFhirClient(Tenant(), accessToken: "tok", http);

        await client.SearchAsync<Condition>(new[] { "patient=123" }, CancellationToken.None);

        captured.LastRequest!.RequestUri!.AbsolutePath.Should().StartWith("/api/FHIR/R4/Condition",
            because: "search must hit the versioned base, not a truncated path");
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly string _jsonBody;
        public HttpRequestMessage? LastRequest { get; private set; }
        public CapturingHandler(string jsonBody) => _jsonBody = jsonBody;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_jsonBody) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/fhir+json");
            return Task.FromResult(response);
        }
    }
}
