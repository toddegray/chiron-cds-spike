using System.Globalization;
using Chiron.Cds.Engine.Primitives;
using Chiron.Cds.Web.CdsHooks.Models;
using Chiron.Cds.Web.Configuration;
using Chiron.Cds.Web.FhirClient;
using Chiron.Cds.Web.Mappers;
using Chiron.Cds.Web.Tenancy;
using Hl7.Fhir.Model;
using Microsoft.Extensions.Options;
using ReasoningEngine = Chiron.Cds.Engine.Engine;
using EngineMedication = Chiron.Cds.Engine.Primitives.Medication;
using FhirCodeableConcept = Hl7.Fhir.Model.CodeableConcept;

namespace Chiron.Cds.Web.Panel;

/// <summary>Builds a draft <see cref="MedicationRequest"/>, runs CDS over it, and (when authorised) writes it.</summary>
public class OrderEntryService
{
    private readonly TenantRegistry _tenants;
    private readonly PatientChartFetcher _fetcher;
    private readonly FhirToFactMapper _factMapper;
    private readonly AlertToCdsCardMapper _cardMapper;
    private readonly ReasoningEngine _engine;
    private readonly PharmacyOptions _pharmacies;
    private readonly ILogger<OrderEntryService> _log;

    public OrderEntryService(
        TenantRegistry tenants,
        PatientChartFetcher fetcher,
        FhirToFactMapper factMapper,
        AlertToCdsCardMapper cardMapper,
        ReasoningEngine engine,
        IOptions<PharmacyOptions> pharmacies,
        ILogger<OrderEntryService> log)
    {
        ArgumentNullException.ThrowIfNull(pharmacies);
        _tenants = tenants;
        _fetcher = fetcher;
        _factMapper = factMapper;
        _cardMapper = cardMapper;
        _engine = engine;
        _pharmacies = pharmacies.Value;
        _log = log;
    }

    public IReadOnlyList<PharmacyEntry> Pharmacies => _pharmacies.Entries;

    /// <summary>
    /// Run the CDS engine against the patient chart with the draft order's
    /// medication added to the active medication list. Returns the cards
    /// the engine fires for the proposed order plus any critical-severity
    /// gates the renderer needs to enforce before sign.
    /// </summary>
    public virtual async Task<OrderEvaluation> EvaluateAsync(
        string patientId, OrderDraft draft, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(patientId);
        ArgumentNullException.ThrowIfNull(draft);

        PatientChart chart;
        try
        {
            chart = await FetchChartAsync(patientId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is Hl7.Fhir.Rest.FhirOperationException or HttpRequestException or TaskCanceledException)
        {
            _log.LogWarning(ex, "Order evaluation could not load chart for patient {Id}.", patientId);
            return OrderEvaluation.ChartLoadFailed(SummariseError(ex));
        }

        var inputs = _factMapper.Project(chart);
        var draftMed = ProjectDraftMedication(draft);
        var medications = inputs.Medications.Append(draftMed).ToArray();

        var result = _engine.Evaluate(
            inputs.Patient, medications, inputs.Labs, inputs.Conditions,
            inputs.Allergies, inputs.Immunizations, inputs.Procedures);
        var cards = result.Alerts.Select(_cardMapper.Map).ToArray();
        return new OrderEvaluation(cards, ChartError: null);
    }

    /// <summary>
    /// Sign + write the order to the authenticated FHIR endpoint. Requires
    /// (a) a non-empty access token (i.e. an active SMART session), and (b)
    /// every critical-severity card from the evaluation acknowledged by the
    /// caller. Returns the server-assigned resource id, or an error reason.
    /// </summary>
    public virtual async Task<OrderWriteResult> SignAsync(
        string patientId,
        OrderDraft draft,
        string? accessToken,
        IReadOnlySet<string> acknowledgedFingerprints,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(patientId);
        ArgumentNullException.ThrowIfNull(draft);

        // Always run CDS first so the user sees what the engine fired.
        // Auth state and ack gating are checked only after the cards are
        // available — bailing earlier would leave the doctor wondering
        // whether CDS even ran.
        var evaluation = await EvaluateAsync(patientId, draft, ct).ConfigureAwait(false);
        if (evaluation.ChartError is not null)
            return OrderWriteResult.Failed("Could not load chart: " + evaluation.ChartError);

        var critical = evaluation.Cards
            .Where(c => string.Equals(c.Indicator, "critical", StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Uuid)
            .Where(uuid => !string.IsNullOrEmpty(uuid))
            .ToArray();
        var unack = critical.Where(fp => !acknowledgedFingerprints.Contains(fp!)).ToArray();
        if (unack.Length > 0)
            return OrderWriteResult.Blocked(
                $"Acknowledge {unack.Length} critical alert{(unack.Length == 1 ? "" : "s")} to sign.",
                evaluation.Cards);

        var resource = BuildMedicationRequest(patientId, draft);

        if (string.IsNullOrEmpty(accessToken))
        {
            // No SMART session — render the would-be FHIR payload as a
            // preview instead of a dead-end "go authenticate" banner. The
            // doctor sees end-to-end what Chiron would have sent.
            return OrderWriteResult.Preview(SerialisePreview(resource), evaluation.Cards);
        }

        try
        {
            var id = await WriteAsync(resource, accessToken, ct).ConfigureAwait(false);
            return OrderWriteResult.Ok(id);
        }
        catch (Exception ex) when (ex is Hl7.Fhir.Rest.FhirOperationException or HttpRequestException)
        {
            _log.LogWarning(ex, "MedicationRequest write failed for patient {Id}.", patientId);
            return OrderWriteResult.Failed("FHIR write failed: " + SummariseError(ex));
        }
    }

    /// <summary>Compose the engine-side medication from the draft form fields.</summary>
    internal static EngineMedication ProjectDraftMedication(OrderDraft draft)
    {
        var canonical = FhirToFactMapper.NormalizeMedicationName(draft.DrugName);
        return new EngineMedication(
            Name: canonical,
            DoseMg: TryParseDoseMg(draft.Strength),
            Frequency: string.IsNullOrWhiteSpace(draft.Frequency) ? null : draft.Frequency,
            Route: string.IsNullOrWhiteSpace(draft.Route) ? null : draft.Route,
            Active: true);
    }

    /// <summary>
    /// Serialise a <see cref="MedicationRequest"/> to indented FHIR JSON for
    /// the no-session preview pane. Exposed as internal-static so tests can
    /// pin the contract (resourceType, intent, indentation) against the real
    /// Firely converter without having to drive a full request lifecycle.
    /// </summary>
    internal static string SerialisePreview(MedicationRequest resource)
    {
        var jsonOptions = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
        Hl7.Fhir.Serialization.FhirJsonConverterOptionsExtensions.ForFhir(
            jsonOptions, Hl7.Fhir.Model.ModelInfo.ModelInspector);
        return System.Text.Json.JsonSerializer.Serialize(resource, jsonOptions);
    }

    /// <summary>Build the FHIR <see cref="MedicationRequest"/> resource we POST to the EHR.</summary>
    internal static MedicationRequest BuildMedicationRequest(string patientId, OrderDraft draft)
    {
        var medText = ComposeMedicationText(draft);
        var sigText = ComposeSigText(draft);
        var resource = new MedicationRequest
        {
            Status = MedicationRequest.MedicationrequestStatus.Active,
            Intent = MedicationRequest.MedicationRequestIntent.Order,
            Medication = new FhirCodeableConcept { Text = medText },
            Subject = new ResourceReference($"Patient/{patientId}"),
            AuthoredOnElement = new FhirDateTime(DateTimeOffset.UtcNow),
            DosageInstruction = new List<Dosage>
            {
                new() { Text = sigText, AsNeeded = new FhirBoolean(draft.AsNeeded) },
            },
            DispenseRequest = new MedicationRequest.DispenseRequestComponent
            {
                NumberOfRepeatsAllowed = draft.Refills,
                Quantity = string.IsNullOrWhiteSpace(draft.Quantity) ? null : new Quantity { Unit = draft.Quantity },
                Performer = string.IsNullOrWhiteSpace(draft.PharmacyDisplay)
                    ? null
                    : new ResourceReference { Display = draft.PharmacyDisplay },
            },
            Substitution = new MedicationRequest.SubstitutionComponent
            {
                Allowed = new FhirBoolean(draft.SubstitutionAllowed),
            },
        };
        if (!string.IsNullOrWhiteSpace(draft.NoteToPharmacist))
            resource.Note = new List<Annotation> { new() { Text = new Markdown(draft.NoteToPharmacist) } };
        return resource;
    }

    /// <summary>"metFORMIN 500 mg oral tablet" — Cerner-shape free-text composite.</summary>
    internal static string ComposeMedicationText(OrderDraft draft)
    {
        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(draft.DrugName)) parts.Add(draft.DrugName.Trim());
        if (!string.IsNullOrWhiteSpace(draft.Strength)) parts.Add(draft.Strength.Trim());
        if (!string.IsNullOrWhiteSpace(draft.Form)) parts.Add(draft.Form.Trim().ToLowerInvariant());
        return parts.Count == 0 ? "Unspecified medication" : string.Join(' ', parts);
    }

    /// <summary>"500 mg, Oral, twice daily — as needed for pain" — composite sig.</summary>
    internal static string ComposeSigText(OrderDraft draft)
    {
        var parts = new List<string>(4);
        if (!string.IsNullOrWhiteSpace(draft.Strength)) parts.Add(draft.Strength.Trim());
        if (!string.IsNullOrWhiteSpace(draft.Route)) parts.Add(draft.Route.Trim());
        if (!string.IsNullOrWhiteSpace(draft.Frequency)) parts.Add(draft.Frequency.Trim());
        var line = string.Join(", ", parts);
        if (draft.AsNeeded && !string.IsNullOrWhiteSpace(draft.PrnReason))
            line += " — as needed for " + draft.PrnReason.Trim();
        else if (draft.AsNeeded)
            line += " — as needed";
        return line.Length == 0 ? "Take as directed." : line;
    }

    /// <summary>
    /// Default chart fetch path; exposed as virtual so tests can stub.
    /// Mirrors <see cref="PanelService"/>'s open-tenant pattern.
    /// </summary>
    protected virtual Task<PatientChart> FetchChartAsync(string patientId, CancellationToken ct)
    {
        var tenant = _tenants.Default.AsOpen();
        return _fetcher.FetchAsync(tenant, accessToken: string.Empty, patientId, encounterId: null, ct);
    }

    /// <summary>
    /// Default write path; exposed as virtual so tests can intercept without
    /// standing up an authenticated FHIR server.
    /// </summary>
    protected virtual async Task<string> WriteAsync(
        MedicationRequest resource, string accessToken, CancellationToken ct)
    {
        var tenant = _tenants.Default;
        using var client = new TenantFhirClient(tenant, accessToken);
        var created = await client.CreateAsync(resource, ct).ConfigureAwait(false);
        return created.Id ?? string.Empty;
    }

    private static double? TryParseDoseMg(string? strength)
    {
        if (string.IsNullOrWhiteSpace(strength)) return null;
        // "500 mg" / "500mg" / "12.5 mg" — keep digits + decimal, parse invariantly.
        var s = new string(strength.Where(c => char.IsDigit(c) || c == '.').ToArray());
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    internal static string SummariseError(Exception ex) => ex switch
    {
        Hl7.Fhir.Rest.FhirOperationException fop => $"FHIR {(int)fop.Status} {fop.Status}",
        TaskCanceledException => "Timed out",
        _ => "Network error",
    };
}

/// <summary>
/// Form-bound order draft. All fields are user input. <see cref="PharmacyId"/>
/// is the form key from the configured pharmacy list; <see cref="PharmacyDisplay"/>
/// is its display label, set server-side.
/// </summary>
public sealed record OrderDraft(
    string DrugName,
    string Strength,
    string? Form,
    string? Route,
    string? Frequency,
    string? Quantity,
    int Refills,
    bool AsNeeded,
    string? PrnReason,
    string? PharmacyId,
    string? PharmacyDisplay,
    bool SubstitutionAllowed,
    string? NoteToPharmacist)
{
    public static readonly OrderDraft Empty = new(
        DrugName: string.Empty,
        Strength: string.Empty,
        Form: null,
        Route: null,
        Frequency: null,
        Quantity: null,
        Refills: 0,
        AsNeeded: false,
        PrnReason: null,
        PharmacyId: null,
        PharmacyDisplay: null,
        SubstitutionAllowed: true,
        NoteToPharmacist: null);
}

/// <summary>Inline CDS result. Each card is a candidate alert the engine fired against the draft order.</summary>
public sealed record OrderEvaluation(IReadOnlyList<CdsCard> Cards, string? ChartError)
{
    public static OrderEvaluation ChartLoadFailed(string error) =>
        new(Array.Empty<CdsCard>(), error);
}

/// <summary>
/// Result of a sign attempt. <see cref="Status"/> distinguishes success
/// (resource written, <see cref="WrittenId"/> non-null), client-side block
/// (CDS alerts not acknowledged), missing-session, or downstream FHIR
/// failure. <see cref="Cards"/> is non-empty on Blocked so the renderer can
/// re-display the cards inline.
/// </summary>
public sealed record OrderWriteResult(
    OrderWriteStatus Status,
    string? WrittenId,
    string? Message,
    IReadOnlyList<CdsCard> Cards,
    string? PreviewJson)
{
    public static OrderWriteResult Ok(string id) =>
        new(OrderWriteStatus.Ok, id, null, Array.Empty<CdsCard>(), null);
    public static OrderWriteResult Blocked(string message, IReadOnlyList<CdsCard> cards) =>
        new(OrderWriteStatus.Blocked, null, message, cards, null);
    public static OrderWriteResult Preview(string previewJson, IReadOnlyList<CdsCard> cards) =>
        new(OrderWriteStatus.Preview, null, null, cards, previewJson);
    public static OrderWriteResult Failed(string message) =>
        new(OrderWriteStatus.Failed, null, message, Array.Empty<CdsCard>(), null);
}

public enum OrderWriteStatus
{
    Ok,
    Blocked,
    Preview,
    Failed,
}
