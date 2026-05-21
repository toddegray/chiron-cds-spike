using System.Text.Json;

using Chiron.Cds.Web.CdsHooks.Models;
using Chiron.Cds.Web.FhirClient;
using Chiron.Cds.Web.Mappers;
using Chiron.Cds.Web.Tenancy;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using ReasoningEngine = Chiron.Cds.Engine.Engine;
using Chiron.Cds.Engine;

namespace Chiron.Cds.Web.CdsHooks;

/// <summary>
/// Handles <c>patient-view</c> CDS Hook invocations. Uses inbound prefetch
/// when the EHR supplies it; falls back to direct FHIR reads via the
/// <see cref="CdsFhirAuthorization"/> bearer token otherwise.
/// </summary>
public sealed class PatientViewService
{
    private static readonly FhirJsonDeserializer FhirDeserializer = new(new DeserializerSettings());

    private readonly ReasoningEngine _engine;
    private readonly PatientChartFetcher _chartFetcher;
    private readonly FhirToFactMapper _factMapper;
    private readonly AlertToCdsCardMapper _cardMapper;
    private readonly TenantRegistry _tenants;
    private readonly OverrideLog _overrideLog;
    private readonly ILogger<PatientViewService> _log;

    public PatientViewService(
        ReasoningEngine engine,
        PatientChartFetcher chartFetcher,
        FhirToFactMapper factMapper,
        AlertToCdsCardMapper cardMapper,
        TenantRegistry tenants,
        OverrideLog overrideLog,
        ILogger<PatientViewService> log)
    {
        _engine = engine;
        _chartFetcher = chartFetcher;
        _factMapper = factMapper;
        _cardMapper = cardMapper;
        _tenants = tenants;
        _overrideLog = overrideLog;
        _log = log;
    }

    public async Task<CdsHookResponse> EvaluateAsync(CdsHookRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var patientId = ReadPatientIdFromContext(request.Context);
        if (string.IsNullOrEmpty(patientId))
        {
            _log.LogWarning("CDS Hook request {Instance} missing patientId in context.", request.HookInstance);
            return new CdsHookResponse(Array.Empty<CdsCard>());
        }

        PatientChart chart;
        if (request.Prefetch is { Count: > 0 } && TryBuildChartFromPrefetch(request.Prefetch, patientId, out chart!))
        {
            _log.LogInformation("Using prefetched resources for patient {PatientId}.", patientId);
        }
        else if (request.FhirAuthorization is not null && !string.IsNullOrEmpty(request.FhirServer))
        {
            var tenant = _tenants.GetByFhirBase(request.FhirServer);
            chart = await _chartFetcher.FetchAsync(
                tenant,
                request.FhirAuthorization.AccessToken,
                patientId,
                encounterId: null,
                ct).ConfigureAwait(false);
        }
        else
        {
            _log.LogWarning(
                "CDS Hook request {Instance} has neither prefetch nor fhirAuthorization. Cannot evaluate.",
                request.HookInstance);
            return new CdsHookResponse(Array.Empty<CdsCard>());
        }

        var inputs = _factMapper.Project(chart);
        var result = _engine.Evaluate(inputs.Patient, inputs.Medications, inputs.Labs, inputs.Conditions);
        var cards = new List<CdsCard>(result.Alerts.Count);
        foreach (var alert in result.Alerts)
        {
            _overrideLog.RecordFire(alert);
            cards.Add(_cardMapper.Map(alert));
        }
        return new CdsHookResponse(cards);
    }

    private static string? ReadPatientIdFromContext(JsonElement context)
    {
        if (context.ValueKind != JsonValueKind.Object) return null;
        return context.TryGetProperty("patientId", out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;
    }

    private bool TryBuildChartFromPrefetch(
        IReadOnlyDictionary<string, JsonElement> prefetch,
        string patientId,
        out PatientChart chart)
    {
        chart = null!;
        try
        {
            Patient? patient = null;
            var conditions = new List<Condition>();
            var observations = new List<Observation>();
            var meds = new List<MedicationRequest>();

            foreach (var (key, value) in prefetch)
            {
                if (value.ValueKind == JsonValueKind.Null || value.ValueKind == JsonValueKind.Undefined) continue;
                var resource = ParseResource(value);
                if (resource is null) continue;
                switch (resource)
                {
                    case Patient p when string.Equals(p.Id, patientId, StringComparison.Ordinal):
                        patient = p; break;
                    case Bundle b:
                        foreach (var entry in b.Entry ?? Enumerable.Empty<Bundle.EntryComponent>())
                        {
                            switch (entry.Resource)
                            {
                                case Condition c: conditions.Add(c); break;
                                case Observation o: observations.Add(o); break;
                                case MedicationRequest m: meds.Add(m); break;
                                case Patient pp when string.Equals(pp.Id, patientId, StringComparison.Ordinal):
                                    patient ??= pp; break;
                            }
                        }
                        break;
                    case Condition c: conditions.Add(c); break;
                    case Observation o: observations.Add(o); break;
                    case MedicationRequest m: meds.Add(m); break;
                }
            }

            if (patient is null) return false;
            chart = new PatientChart(patient, conditions, observations, meds, Encounter: null);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to project prefetch payload for patient {PatientId}.", patientId);
            return false;
        }
    }

    private static Resource? ParseResource(JsonElement element)
    {
        var raw = element.GetRawText();
        try
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(raw);
            var reader = new System.Text.Json.Utf8JsonReader(bytes);
            if (FhirDeserializer.TryDeserializeResource(ref reader, out var resource, out _))
                return resource;
            return null;
        }
        catch (Exception ex) when (ex is FormatException or DeserializationFailedException or JsonException)
        {
            return null;
        }
    }
}
