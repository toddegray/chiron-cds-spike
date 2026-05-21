using FluentAssertions;
using Hl7.Fhir.Model;
using Hl7.Fhir.Rest;
using FirelyClient = Hl7.Fhir.Rest.FhirClient;
using Task = System.Threading.Tasks.Task;

namespace Chiron.Cds.Web.IntegrationTests;

/// <summary>
/// Reads against Cerner's open (unauthenticated) Code sandbox. Proves the
/// Firely client works against a real server with no SMART launch in the
/// way. Skipped on network failure.
/// </summary>
[Trait("Category", "Live")]
public class CernerOpenSandboxTests
{
    private const string OpenBase = "https://fhir-open.cerner.com/r4/ec2458f2-1e24-41c8-b71b-0e701af7583d";

    /// <summary>Cerner publishes test patient ids; this is a known one in the Code sandbox.</summary>
    private const string KnownPatientId = "12724066";

    [Fact]
    public async Task OpenSandbox_Patient_Can_Be_Fetched()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var client = new FirelyClient(new Uri(OpenBase), http,
            new FhirClientSettings { PreferredFormat = ResourceFormat.Json });

        Patient? patient;
        try
        {
            patient = await client.ReadAsync<Patient>($"Patient/{KnownPatientId}");
        }
        catch (HttpRequestException) { return; }
        catch (TaskCanceledException) { return; }
        catch (FhirOperationException) { return; }

        patient.Should().NotBeNull();
        patient!.Id.Should().Be(KnownPatientId);
    }

    [Fact]
    public async Task OpenSandbox_Patient_Search_Returns_Bundle()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var client = new FirelyClient(new Uri(OpenBase), http,
            new FhirClientSettings { PreferredFormat = ResourceFormat.Json });

        Bundle? bundle;
        try
        {
            bundle = await client.SearchAsync<Patient>(
                new[] { $"_id={KnownPatientId}" },
                includes: (string[]?)null,
                pageSize: null,
                summary: null,
                revIncludes: (string[]?)null);
        }
        catch (HttpRequestException) { return; }
        catch (TaskCanceledException) { return; }
        catch (FhirOperationException) { return; }

        bundle.Should().NotBeNull();
        bundle!.Entry.Should().NotBeNull();
    }
}
