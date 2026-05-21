using Chiron.Cds.Web.Tenancy;
using Hl7.Fhir.Model;
using Hl7.Fhir.Rest;
using Task = System.Threading.Tasks.Task;

namespace Chiron.Cds.Web.FhirClient;

/// <summary>
/// Pulls the resources the engine cares about (Patient + Conditions +
/// laboratory Observations + active MedicationRequests + optional Encounter)
/// in parallel and assembles a <see cref="PatientChart"/>.
/// </summary>
public sealed class PatientChartFetcher
{
    /// <summary>LOINC codes the engine's rules look at by name.</summary>
    public static readonly IReadOnlyDictionary<string, string> LabsByLoinc = new Dictionary<string, string>
    {
        ["2160-0"] = "creatinine",
        ["33914-3"] = "egfr",
        ["4548-4"] = "hemoglobin_a1c",
        ["6301-6"] = "inr",
    };

    private readonly ILogger<PatientChartFetcher> _log;

    public PatientChartFetcher(ILogger<PatientChartFetcher> log)
    {
        _log = log;
    }

    public async Task<PatientChart> FetchAsync(
        TenantConfig tenant,
        string accessToken,
        string patientId,
        string? encounterId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(patientId);
        using var client = new TenantFhirClient(tenant, accessToken);

        // Patient read is the only fetch we treat as fatal — without it
        // there's nothing to evaluate. The others (conditions, labs, meds,
        // encounter) degrade gracefully on 403 / 404 / 410 so a permission
        // gap on one resource doesn't blow up the whole evaluation. The
        // engine handles an empty input list cleanly per Rule contract.
        var patient = await client.ReadAsync<Patient>(patientId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Patient/{patientId} not found.");

        var conditionsTask = TryFetchBundleAsync<Condition>(client,
            new[] { $"patient={patientId}" }, "Condition", ct);
        var observationsTask = TryFetchBundleAsync<Observation>(client,
            new[] { $"patient={patientId}", "category=laboratory" }, "Observation", ct);
        var medsTask = TryFetchBundleAsync<MedicationRequest>(client,
            new[] { $"patient={patientId}", "status=active" }, "MedicationRequest", ct);
        var encounterTask = string.IsNullOrEmpty(encounterId)
            ? Task.FromResult<Encounter?>(null)
            : TryReadAsync<Encounter>(client, encounterId, ct);

        await Task.WhenAll(conditionsTask, observationsTask, medsTask, encounterTask).ConfigureAwait(false);

        var conditions = await conditionsTask.ConfigureAwait(false);
        var observations = await observationsTask.ConfigureAwait(false);
        var meds = await medsTask.ConfigureAwait(false);
        var encounter = await encounterTask.ConfigureAwait(false);

        _log.LogInformation(
            "Fetched chart for patient {PatientId}: {Conditions} conditions, {Observations} observations, {Meds} medications.",
            patientId, conditions.Count, observations.Count, meds.Count);

        return new PatientChart(patient, conditions, observations, meds, encounter);
    }

    private async Task<IReadOnlyList<TResource>> TryFetchBundleAsync<TResource>(
        TenantFhirClient client, string[] criteria, string resourceName, CancellationToken ct)
        where TResource : Resource, new()
    {
        try
        {
            return ExtractResources<TResource>(await client.SearchAsync<TResource>(criteria, ct).ConfigureAwait(false));
        }
        catch (FhirOperationException ex) when (IsExpected(ex.Status))
        {
            _log.LogWarning("Skipping {Resource} fetch: {Status} {Reason}",
                resourceName, (int)ex.Status, ex.Message);
            return Array.Empty<TResource>();
        }
    }

    private async Task<TResource?> TryReadAsync<TResource>(
        TenantFhirClient client, string id, CancellationToken ct)
        where TResource : Resource, new()
    {
        try
        {
            return await client.ReadAsync<TResource>(id, ct).ConfigureAwait(false);
        }
        catch (FhirOperationException ex) when (IsExpected(ex.Status))
        {
            _log.LogWarning("Skipping {Resource}/{Id} read: {Status} {Reason}",
                typeof(TResource).Name, id, (int)ex.Status, ex.Message);
            return null;
        }
    }

    private static bool IsExpected(System.Net.HttpStatusCode status) =>
        status == System.Net.HttpStatusCode.Forbidden
        || status == System.Net.HttpStatusCode.NotFound
        || status == System.Net.HttpStatusCode.Gone;

    private static IReadOnlyList<TResource> ExtractResources<TResource>(Bundle? bundle)
        where TResource : Resource
    {
        if (bundle?.Entry is null) return Array.Empty<TResource>();
        return bundle.Entry
            .Select(e => e.Resource)
            .OfType<TResource>()
            .ToArray();
    }
}
