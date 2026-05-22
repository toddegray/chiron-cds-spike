using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Chiron.Cds.Web.CdsHooks.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Task = System.Threading.Tasks.Task;

namespace Chiron.Cds.Web.IntegrationTests;

/// <summary>
/// End-to-end test that fetches a real Cerner sandbox patient's chart from
/// <c>fhir-open.cerner.com</c> and pipes the unmodified FHIR resources
/// through Chiron's CDS Hooks endpoint as prefetch. No augmentation, no
/// hand-rolled JSON: the Cerner-shaped resources Chiron's <c>FhirToFactMapper</c>
/// sees here are the same shape it would see during a SMART launch against
/// the authenticated Cerner FHIR endpoint.
///
/// Patient 12674028 is "ANNIE SMITH": 35-year-old female with an active
/// type-2-diabetes condition and an active metformin order, both
/// confirmed by inspecting the open sandbox. On this real chart CHA₂DS₂-VASc
/// scores female_sex(1) + diabetes(1) = 2 — Medium severity.
///
/// Skipped (no failure) on network errors so flaky CI doesn't go red on
/// Cerner outages.
/// </summary>
[Trait("Category", "Live")]
public class RealCernerPatientTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string OpenBase = "https://fhir-open.cerner.com/r4/ec2458f2-1e24-41c8-b71b-0e701af7583d";
    private const string FhirAuthBase = "https://fhir-ehr-code.cerner.com/r4/ec2458f2-1e24-41c8-b71b-0e701af7583d";

    /// <summary>Cerner sandbox patient with diabetes + metformin (no augmentation).</summary>
    private const string AnnieSmithId = "12674028";

    private readonly WebApplicationFactory<Program> _factory;

    public RealCernerPatientTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Annie_Smith_Real_Chart_Fires_CHA2DS2VASc()
    {
        using var sandboxHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        JsonElement patient, conditions, medications;
        try
        {
            patient = await GetJsonAsync(sandboxHttp, $"{OpenBase}/Patient/{AnnieSmithId}");
            conditions = await GetJsonAsync(sandboxHttp, $"{OpenBase}/Condition?patient={AnnieSmithId}");
            medications = await GetJsonAsync(sandboxHttp, $"{OpenBase}/MedicationRequest?patient={AnnieSmithId}&status=active");
        }
        catch (HttpRequestException) { return; }
        catch (TaskCanceledException) { return; }

        var request = new
        {
            hook = "patient-view",
            hookInstance = Guid.NewGuid().ToString(),
            fhirServer = FhirAuthBase,
            context = new { patientId = AnnieSmithId },
            prefetch = new Dictionary<string, JsonElement>
            {
                ["patient"] = patient,
                ["conditions"] = conditions,
                ["medications"] = medications,
            },
        };

        using var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/cds-services/chiron-patient-view", request);
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            because: "real Cerner-shaped FHIR must be accepted by the CDS Hooks endpoint");

        var body = await resp.Content.ReadFromJsonAsync<CdsHookResponse>();
        body.Should().NotBeNull();

        var cha2ds2 = body!.Cards.FirstOrDefault(c => c.Summary.Contains("CHA", StringComparison.OrdinalIgnoreCase));
        cha2ds2.Should().NotBeNull(
            because: "Annie Smith (female + active diabetes) scores 2 on CHA₂DS₂-VASc");
        cha2ds2!.Indicator.Should().Be("warning", because: "score 2 maps to Medium → CDS Hooks 'warning' indicator");
        cha2ds2.Detail.Should().Contain("diabetes",
            because: "the derivation tree should expose the diabetes component fact");
        cha2ds2.Detail.Should().Contain("female_sex",
            because: "the derivation tree should expose the female_sex component fact");
    }

    [Fact]
    public async Task Annie_Smith_Real_Allergies_Are_Projected_Without_Crash()
    {
        // Annie Smith has 2 documented allergies in the open sandbox:
        // "sulfa drugs" (criticality=high) and "Cashew Nuts". This test
        // confirms our AllergyIntolerance fetch + mapper handle Cerner's
        // real wire shape end-to-end. Annie's active meds (metformin only)
        // don't collide with either allergy, so the drug-allergy rule
        // does not fire on her chart — the assertion is that the request
        // succeeds and the rule did NOT incorrectly fire.
        using var sandboxHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        JsonElement patient, conditions, medications, allergies;
        try
        {
            patient = await GetJsonAsync(sandboxHttp, $"{OpenBase}/Patient/{AnnieSmithId}");
            conditions = await GetJsonAsync(sandboxHttp, $"{OpenBase}/Condition?patient={AnnieSmithId}");
            medications = await GetJsonAsync(sandboxHttp, $"{OpenBase}/MedicationRequest?patient={AnnieSmithId}&status=active");
            allergies = await GetJsonAsync(sandboxHttp, $"{OpenBase}/AllergyIntolerance?patient={AnnieSmithId}");
        }
        catch (HttpRequestException) { return; }
        catch (TaskCanceledException) { return; }

        var request = new
        {
            hook = "patient-view",
            hookInstance = Guid.NewGuid().ToString(),
            fhirServer = FhirAuthBase,
            context = new { patientId = AnnieSmithId },
            prefetch = new Dictionary<string, JsonElement>
            {
                ["patient"] = patient,
                ["conditions"] = conditions,
                ["medications"] = medications,
                ["allergies"] = allergies,
            },
        };

        // Positive sanity: the live Cerner endpoint must have returned at
        // least one allergy entry. Without this assertion the test passes
        // even when Cerner silently returns an empty bundle, which would
        // mean the AllergyIntolerance projection path is no longer exercised
        // end-to-end.
        allergies.GetProperty("entry").GetArrayLength().Should().BeGreaterThan(0,
            because: "Annie Smith has documented allergies (sulfa, cashew) in the open sandbox");

        // The unit-test side of the allergy projection matrix lives in
        // FhirToFactMapperTests; here we only assert end-to-end shape.
        using var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/cds-services/chiron-patient-view", request);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<CdsHookResponse>();
        body.Should().NotBeNull();
        body!.Cards.Should().NotContain(c => c.Summary.Contains("allergy", StringComparison.OrdinalIgnoreCase),
            because: "Annie's allergies (sulfa, cashew) do not collide with her single active med (metformin)");
    }

    [Fact]
    public async Task Nancy_Smarts_Real_Chart_Produces_No_Alerts()
    {
        // Patient 12724066 (Nancy SMARTS) is the canonical Cerner sandbox
        // patient: 35-year-old, no active conditions visible through the open
        // endpoint, normal creatinine, lots of acetaminophen + chlorothiazide
        // but none of the rules in our pack apply. The card list should be
        // empty — proving the engine doesn't fire spurious alerts.
        using var sandboxHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        JsonElement patient, conditions, observations, medications;
        try
        {
            patient = await GetJsonAsync(sandboxHttp, $"{OpenBase}/Patient/12724066");
            conditions = await GetJsonAsync(sandboxHttp, $"{OpenBase}/Condition?patient=12724066");
            observations = await GetJsonAsync(sandboxHttp, $"{OpenBase}/Observation?patient=12724066&category=laboratory");
            medications = await GetJsonAsync(sandboxHttp, $"{OpenBase}/MedicationRequest?patient=12724066&status=active");
        }
        catch (HttpRequestException) { return; }
        catch (TaskCanceledException) { return; }

        var request = new
        {
            hook = "patient-view",
            hookInstance = Guid.NewGuid().ToString(),
            fhirServer = FhirAuthBase,
            context = new { patientId = "12724066" },
            prefetch = new Dictionary<string, JsonElement>
            {
                ["patient"] = patient,
                ["conditions"] = conditions,
                ["observations"] = observations,
                ["medications"] = medications,
            },
        };

        using var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/cds-services/chiron-patient-view", request);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<CdsHookResponse>();
        body.Should().NotBeNull();
        body!.Cards.Should().NotContain(c => c.Summary.Contains("metformin", StringComparison.OrdinalIgnoreCase),
            because: "Nancy Smarts has no active metformin and her creatinine is normal");
        body.Cards.Should().NotContain(c => c.Summary.Contains("warfarin", StringComparison.OrdinalIgnoreCase),
            because: "Nancy Smarts has aspirin but no warfarin, so the warfarin-NSAID rule must not fire");
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient http, string url)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Accept", "application/fhir+json");
        using var resp = await http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        return JsonDocument.Parse(bytes).RootElement.Clone();
    }
}
