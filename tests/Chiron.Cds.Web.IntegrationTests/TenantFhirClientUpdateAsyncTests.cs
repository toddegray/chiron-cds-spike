using System.Net;
using System.Net.Http.Headers;
using Chiron.Cds.Web.FhirClient;
using Chiron.Cds.Web.Tenancy;
using FluentAssertions;
using Hl7.Fhir.Model;
using Task = System.Threading.Tasks.Task;

namespace Chiron.Cds.Web.IntegrationTests;

/// <summary>
/// Pins the wire contract for <see cref="TenantFhirClient.UpdateAsync{T}"/>:
/// it must issue an HTTP <c>PUT</c> to <c>/{ResourceType}/{id}</c>, carrying
/// the resource body. A stub <see cref="HttpMessageHandler"/> captures the
/// outbound request so we can assert it without standing up a real server.
/// </summary>
public class TenantFhirClientUpdateAsyncTests
{
    private static TenantConfig Tenant() => new(
        Id: "t",
        DisplayName: "T",
        ClientId: "c",
        ClientSecret: null,
        FhirBaseUrl: new Uri("https://fhir.test/r4"),
        FhirOpenBaseUrl: null,
        Scopes: "");

    [Fact]
    public async Task UpdateAsync_Issues_Put_To_Resource_Path_With_Id()
    {
        var captured = new CapturingHandler(jsonBody: """
        { "resourceType": "Encounter", "id": "e1", "status": "finished" }
        """);
        using var http = new HttpClient(captured);
        using var client = new TenantFhirClient(Tenant(), accessToken: "tok", http);

        var encounter = new Encounter
        {
            Id = "e1",
            Status = Encounter.EncounterStatus.Finished,
            Subject = new ResourceReference("Patient/p1"),
        };
        var returned = await client.UpdateAsync(encounter, CancellationToken.None);

        captured.LastRequest.Should().NotBeNull();
        captured.LastRequest!.Method.Should().Be(HttpMethod.Put,
            because: "FHIR update is a PUT to the resource's canonical URL");
        captured.LastRequest.RequestUri!.AbsolutePath.Should().EndWith("/Encounter/e1",
            because: "the path must include the resource type and id of the existing record");
        captured.LastRequest.Headers.Authorization.Should().Be(
            new AuthenticationHeaderValue("Bearer", "tok"));
        returned.Id.Should().Be("e1");
    }

    [Fact]
    public async Task UpdateAsync_Rejects_Resource_With_No_Id()
    {
        var captured = new CapturingHandler(jsonBody: "{}");
        using var http = new HttpClient(captured);
        using var client = new TenantFhirClient(Tenant(), accessToken: "tok", http);
        var encounter = new Encounter { Status = Encounter.EncounterStatus.Finished };

        var act = async () => await client.UpdateAsync(encounter, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*non-empty Id*");
        captured.LastRequest.Should().BeNull(
            because: "the id guard must fire before any HTTP call goes out");
    }

    /// <summary>
    /// Records the most recent outbound request and replies with a canned
    /// FHIR JSON body so the Firely client can deserialise a response.
    /// </summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly string _jsonBody;
        public HttpRequestMessage? LastRequest { get; private set; }

        public CapturingHandler(string jsonBody)
        {
            _jsonBody = jsonBody;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_jsonBody),
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/fhir+json");
            return Task.FromResult(response);
        }
    }
}
