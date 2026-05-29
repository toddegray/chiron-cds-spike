using Chiron.Cds.Engine;
using Chiron.Cds.Engine.Primitives;
using Chiron.Cds.Web.CdsHooks.Models;
using Chiron.Cds.Web.Configuration;
using Chiron.Cds.Web.FhirClient;
using Chiron.Cds.Web.Mappers;
using Chiron.Cds.Web.SmartLaunch;
using Chiron.Cds.Web.Tenancy;
using FhirPatient = Hl7.Fhir.Model.Patient;
using Microsoft.Extensions.Options;
using ReasoningEngine = Chiron.Cds.Engine.Engine;

namespace Chiron.Cds.Web.Panel;

/// <summary>
/// Backs the panel view: for the connection resolved by
/// <see cref="FhirReadConnection"/> — the authenticated backend tenant when
/// configured, otherwise the open (unauthenticated) endpoint — pulls each
/// configured patient's chart, runs the engine, and returns the data needed to
/// render the worklist + the per-patient Visit Brief.
/// </summary>
public class PanelService
{
    private readonly PanelOptions _options;
    private readonly FhirReadConnection _connection;
    private readonly PatientChartFetcher _fetcher;
    private readonly FhirToFactMapper _factMapper;
    private readonly AlertToCdsCardMapper _cardMapper;
    private readonly ReasoningEngine _engine;
    private readonly ILogger<PanelService> _log;

    public PanelService(
        IOptions<PanelOptions> options,
        FhirReadConnection connection,
        PatientChartFetcher fetcher,
        FhirToFactMapper factMapper,
        AlertToCdsCardMapper cardMapper,
        ReasoningEngine engine,
        ILogger<PanelService> log)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _connection = connection;
        _fetcher = fetcher;
        _factMapper = factMapper;
        _cardMapper = cardMapper;
        _engine = engine;
        _log = log;
    }

    /// <summary>
    /// Evaluate every panel patient in parallel. Failures degrade gracefully
    /// — one bad patient does not blank the whole panel; the row carries an
    /// error flag the renderer surfaces.
    /// </summary>
    public virtual async Task<IReadOnlyList<PanelEntry>> GetPanelAsync(CancellationToken ct)
    {
        var ids = _options.Patients.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToArray();
        if (ids.Length == 0) return Array.Empty<PanelEntry>();

        var connection = await _connection.ResolveAsync(ct).ConfigureAwait(false);
        var tasks = ids.Select(id => EvaluatePatientAsync(connection, id, ct)).ToArray();
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>Fetch + evaluate a single patient by id (Visit Brief drill-in path).</summary>
    public virtual async Task<PanelEntry?> GetPatientAsync(string patientId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(patientId);
        var connection = await _connection.ResolveAsync(ct).ConfigureAwait(false);
        return await EvaluatePatientAsync(connection, patientId, ct).ConfigureAwait(false);
    }

    private async Task<PanelEntry> EvaluatePatientAsync(
        FhirConnection connection, string patientId, CancellationToken ct)
    {
        try
        {
            var chart = await _fetcher.FetchAsync(
                connection.Tenant, connection.AccessToken ?? string.Empty, patientId, encounterId: null, ct)
                .ConfigureAwait(false);
            var inputs = _factMapper.Project(chart);
            var result = _engine.Evaluate(
                inputs.Patient, inputs.Medications, inputs.Labs, inputs.Conditions,
                inputs.Allergies, inputs.Immunizations, inputs.Procedures);
            var cards = result.Alerts.Select(_cardMapper.Map).ToArray();

            return new PanelEntry(
                PatientId: patientId,
                DisplayName: ChartName(chart, patientId),
                AgeSex: PatientHeader.FormatAgeSex(inputs.Patient.AgeYears, inputs.Patient.Sex),
                DateOfBirth: chart.Patient.BirthDate,
                Mrn: PatientMrn.Extract(chart.Patient, connection.Tenant.MrnSystem),
                Inputs: inputs,
                Cards: cards,
                Error: null);
        }
        catch (Exception ex) when (ex is Hl7.Fhir.Rest.FhirOperationException or HttpRequestException or TaskCanceledException)
        {
            _log.LogWarning(ex, "Panel patient {Id} failed to load.", patientId);
            // No DateOfBirth / Mrn on the error entry: the chart didn't load,
            // and PanelController.Patient renders the error path with patient: null
            // so the demographics row wouldn't read them anyway.
            return new PanelEntry(
                PatientId: patientId,
                DisplayName: $"Patient {patientId}",
                AgeSex: string.Empty,
                Inputs: null,
                Cards: Array.Empty<CdsCard>(),
                Error: SummariseError(ex));
        }
    }

    /// <summary>Compose a display name from the FHIR patient name, falling back to <c>"Patient {id}"</c>.</summary>
    internal static string ChartName(FhirPatient patient, string fallbackId)
    {
        var name = patient.Name?.FirstOrDefault();
        if (name is null) return $"Patient {fallbackId}";
        if (!string.IsNullOrWhiteSpace(name.Text)) return name.Text;
        var given = name.Given is null ? string.Empty : string.Join(' ', name.Given);
        var family = name.Family ?? string.Empty;
        var combined = $"{family}, {given}".Trim(' ', ',');
        return string.IsNullOrEmpty(combined) ? $"Patient {fallbackId}" : combined;
    }

    private static string ChartName(PatientChart chart, string fallbackId) =>
        ChartName(chart.Patient, fallbackId);

    /// <summary>Map exceptions to the short string the worklist renders into the row flag.</summary>
    internal static string SummariseError(Exception ex) => ex switch
    {
        Hl7.Fhir.Rest.FhirOperationException fop => $"FHIR {(int)fop.Status} {fop.Status}",
        TaskCanceledException => "Timed out",
        _ => "Network error",
    };
}

/// <summary>
/// One evaluated panel patient — what the worklist row needs plus the data
/// the per-patient Visit Brief drill-in needs. Carries an <see cref="Error"/>
/// string when the FHIR fetch failed so the renderer can flag the row
/// instead of dropping it silently.
/// </summary>
public sealed record PanelEntry(
    string PatientId,
    string DisplayName,
    string AgeSex,
    EngineInputs? Inputs,
    IReadOnlyList<CdsCard> Cards,
    string? Error,
    string? DateOfBirth = null,
    string? Mrn = null);
