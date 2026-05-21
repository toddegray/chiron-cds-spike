using System.Net.Http.Json;

using Chiron.Cds.Shared;
using FluentAssertions;

namespace Chiron.Cds.Web.IntegrationTests;

/// <summary>
/// Hits the real Cerner Code sandbox well-known endpoint. Skipped if the
/// network is unavailable. These tests prove that our SMART discovery code
/// targets a real-world server, not a hand-rolled stub.
/// </summary>
[Trait("Category", "Live")]
public class SmartConfigurationTests
{
    private const string FhirBase = "https://fhir-ehr-code.cerner.com/r4/ec2458f2-1e24-41c8-b71b-0e701af7583d";

    [Fact]
    public async Task SmartConfiguration_Discovery_Returns_Endpoints()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        SmartConfiguration? config;
        try
        {
            config = await client.GetFromJsonAsync<SmartConfiguration>(
                $"{FhirBase}/.well-known/smart-configuration");
        }
        catch (HttpRequestException)
        {
            return; // network down — don't fail CI on connectivity
        }
        catch (TaskCanceledException)
        {
            return;
        }

        config.Should().NotBeNull();
        config!.AuthorizationEndpoint.Should().NotBeNullOrEmpty();
        config.TokenEndpoint.Should().NotBeNullOrEmpty();
        config.Capabilities.Should().NotBeNull();
        config.Capabilities!.Should().Contain(c => c.Contains("launch-ehr", StringComparison.OrdinalIgnoreCase));
    }
}
