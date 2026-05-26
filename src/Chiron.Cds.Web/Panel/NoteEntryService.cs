using System.Text;
using Chiron.Cds.Web.FhirClient;
using Chiron.Cds.Web.Tenancy;
using Hl7.Fhir.Model;
using Task = System.Threading.Tasks.Task;

namespace Chiron.Cds.Web.Panel;

/// <summary>
/// Backs the per-patient Notes tab. Fetches existing
/// <see cref="DocumentReference"/> history, pre-fills a SOAP draft from the
/// active chart, and writes a signed note back when authorised.
/// </summary>
public class NoteEntryService
{
    private const int MaxHistory = 30;

    /// <summary>LOINC code for a clinical progress note.</summary>
    private const string ProgressNoteLoinc = "11506-3";

    private readonly TenantRegistry _tenants;
    private readonly PatientChartFetcher _fetcher;
    private readonly ILogger<NoteEntryService> _log;

    public NoteEntryService(
        TenantRegistry tenants,
        PatientChartFetcher fetcher,
        ILogger<NoteEntryService> log)
    {
        _tenants = tenants;
        _fetcher = fetcher;
        _log = log;
    }

    /// <summary>
    /// Build the Notes-tab view: list existing notes + a SOAP draft
    /// pre-filled from active conditions and meds so the doctor isn't
    /// starting from a blank Assessment / Plan.
    /// </summary>
    public virtual async Task<NotesPageData> GetForPatientAsync(string patientId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(patientId);
        var tenant = _tenants.Default.AsOpen();

        PatientChart chart;
        Bundle? notes;
        try
        {
            (chart, notes) = await FetchAsync(tenant, patientId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is Hl7.Fhir.Rest.FhirOperationException or HttpRequestException or TaskCanceledException)
        {
            _log.LogWarning(ex, "Notes page fetch failed for patient {Id}.", patientId);
            return NotesPageData.Failure(SummariseError(ex));
        }

        var history = (notes?.Entry ?? Enumerable.Empty<Bundle.EntryComponent>())
            .Select(e => e.Resource).OfType<DocumentReference>()
            .Select(ProjectNoteSummary)
            .OrderByDescending(n => n.AuthoredAt ?? DateTimeOffset.MinValue)
            .Take(MaxHistory)
            .ToArray();

        return new NotesPageData(history, ComposeDraft(chart), Error: null);
    }

    /// <summary>
    /// Write a signed note. Returns <see cref="NoteWriteStatus.NotAuthorised"/>
    /// when no SMART session is active — the renderer surfaces a sign-in
    /// prompt, never a synthesised payload.
    /// </summary>
    public virtual async Task<NoteWriteResult> SignAsync(
        string patientId, NoteDraft draft, string? accessToken, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(patientId);
        ArgumentNullException.ThrowIfNull(draft);

        if (string.IsNullOrEmpty(accessToken))
            return NoteWriteResult.NotAuthorised();
        if (IsEmpty(draft))
            return NoteWriteResult.Failed("A note must include at least one section.");

        var resource = BuildDocumentReference(patientId, draft);
        try
        {
            var id = await WriteAsync(resource, accessToken, ct).ConfigureAwait(false);
            return NoteWriteResult.Ok(id);
        }
        catch (Exception ex) when (ex is Hl7.Fhir.Rest.FhirOperationException or HttpRequestException)
        {
            _log.LogWarning(ex, "DocumentReference write failed for patient {Id}.", patientId);
            return NoteWriteResult.Failed("FHIR write failed: " + SummariseError(ex));
        }
    }

    /// <summary>
    /// Pre-fill the Assessment section from active conditions and the Plan
    /// section from active medications. Subjective and Objective stay empty
    /// — the doctor fills those in from the encounter.
    /// </summary>
    internal static NoteDraft ComposeDraft(PatientChart chart)
    {
        ArgumentNullException.ThrowIfNull(chart);
        var conditions = chart.Conditions
            .Where(c => c.ClinicalStatus?.Coding?.Any(x => x.Code == "active") == true)
            .Select(c => c.Code?.Text
                ?? c.Code?.Coding?.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.Display))?.Display)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(s => "- " + s)
            .ToArray();
        var meds = chart.MedicationRequests
            .Where(m => m.Status == MedicationRequest.MedicationrequestStatus.Active)
            .Select(m => (m.Medication as CodeableConcept)?.Text)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(s => "- " + s)
            .ToArray();
        return new NoteDraft(
            Subjective: string.Empty,
            Objective: string.Empty,
            Assessment: conditions.Length == 0 ? string.Empty : string.Join('\n', conditions),
            Plan: meds.Length == 0 ? string.Empty : "Continue:\n" + string.Join('\n', meds));
    }

    /// <summary>
    /// Project a FHIR <see cref="DocumentReference"/> into the summary the
    /// history list renders (one row per note).
    /// </summary>
    internal static NoteSummary ProjectNoteSummary(DocumentReference d)
    {
        var title = d.Type?.Text
            ?? d.Type?.Coding?.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.Display))?.Display
            ?? "Note";
        var category = d.Category?
            .SelectMany(c => c.Coding ?? Enumerable.Empty<Coding>())
            .FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.Display))?.Display
            ?? d.Category?.FirstOrDefault()?.Text;
        DateTimeOffset? authored = null;
        if (d.Date is DateTimeOffset dt) authored = dt;
        return new NoteSummary(
            Title: title,
            Category: category,
            Status: d.Status?.ToString() ?? "unknown",
            AuthoredAt: authored);
    }

    /// <summary>Build the FHIR <see cref="DocumentReference"/> we POST to the EHR.</summary>
    internal static DocumentReference BuildDocumentReference(string patientId, NoteDraft draft)
    {
        var text = ComposeNoteText(draft);
        var bytes = Encoding.UTF8.GetBytes(text);
        return new DocumentReference
        {
            Status = DocumentReferenceStatus.Current,
            Type = new CodeableConcept
            {
                Coding = new List<Coding>
                {
                    new() { System = "http://loinc.org", Code = ProgressNoteLoinc, Display = "Progress note" },
                },
                Text = "Progress note",
            },
            Category = new List<CodeableConcept>
            {
                new() { Coding = new List<Coding> { new() { Display = "Clinical Note" } } },
            },
            Subject = new ResourceReference($"Patient/{patientId}"),
            Date = DateTimeOffset.UtcNow,
            Content = new List<DocumentReference.ContentComponent>
            {
                new()
                {
                    Attachment = new Attachment
                    {
                        ContentType = "text/plain",
                        Data = bytes,
                        Title = "Progress note",
                    },
                },
            },
        };
    }

    /// <summary>
    /// SOAP sections joined into a single plain-text payload. Empty sections
    /// are omitted so a note authored as "Plan-only" doesn't carry empty S/O/A
    /// headers.
    /// </summary>
    internal static string ComposeNoteText(NoteDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var sb = new StringBuilder();
        AppendSection(sb, "Subjective", draft.Subjective);
        AppendSection(sb, "Objective", draft.Objective);
        AppendSection(sb, "Assessment", draft.Assessment);
        AppendSection(sb, "Plan", draft.Plan);
        return sb.ToString();
    }

    private static void AppendSection(StringBuilder sb, string heading, string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return;
        if (sb.Length > 0) sb.AppendLine().AppendLine();
        sb.Append(heading.ToUpperInvariant()).AppendLine().Append(body.TrimEnd());
    }

    /// <summary>True when every SOAP section is blank/whitespace.</summary>
    internal static bool IsEmpty(NoteDraft draft) =>
        string.IsNullOrWhiteSpace(draft.Subjective)
        && string.IsNullOrWhiteSpace(draft.Objective)
        && string.IsNullOrWhiteSpace(draft.Assessment)
        && string.IsNullOrWhiteSpace(draft.Plan);

    /// <summary>
    /// Default chart + history fetch. A failure on the history search is
    /// tolerated — the form stays usable even when the EHR's
    /// <c>DocumentReference</c> endpoint isn't reachable. The chart fetch is
    /// the only thing that can fail the whole page.
    /// </summary>
    protected virtual async Task<(PatientChart Chart, Bundle? Notes)> FetchAsync(
        TenantConfig tenant, string patientId, CancellationToken ct)
    {
        var chartTask = FetchChartAsync(tenant, patientId, ct);
        Bundle? notes = null;
        try
        {
            notes = await SearchNotesAsync(tenant, patientId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is Hl7.Fhir.Rest.FhirOperationException or HttpRequestException or TaskCanceledException)
        {
            _log.LogWarning(ex, "DocumentReference search failed for {Id}; rendering with empty history.", patientId);
        }
        var chart = await chartTask.ConfigureAwait(false);
        return (chart, notes);
    }

    /// <summary>Default chart fetch path. Virtual so tests can simulate it without an HTTP call.</summary>
    protected virtual Task<PatientChart> FetchChartAsync(
        TenantConfig tenant, string patientId, CancellationToken ct) =>
        _fetcher.FetchAsync(tenant, accessToken: string.Empty, patientId, encounterId: null, ct);

    /// <summary>
    /// Issue the DocumentReference history search. Exposed as a separate
    /// virtual seam so tests can simulate a failed history fetch without
    /// affecting the chart fetch.
    /// </summary>
    protected virtual async Task<Bundle?> SearchNotesAsync(
        TenantConfig tenant, string patientId, CancellationToken ct)
    {
        using var client = new TenantFhirClient(tenant, accessToken: null);
        return await client.SearchAsync<DocumentReference>(
            new[] { $"patient={patientId}", "_count=30" }, ct).ConfigureAwait(false);
    }

    /// <summary>Default write path. Virtual so tests can stub without an auth server.</summary>
    protected virtual async Task<string> WriteAsync(
        DocumentReference resource, string accessToken, CancellationToken ct)
    {
        var tenant = _tenants.Default;
        using var client = new TenantFhirClient(tenant, accessToken);
        var created = await client.CreateAsync(resource, ct).ConfigureAwait(false);
        return created.Id ?? string.Empty;
    }

    internal static string SummariseError(Exception ex) => ex switch
    {
        Hl7.Fhir.Rest.FhirOperationException fop => $"FHIR {(int)fop.Status} {fop.Status}",
        TaskCanceledException => "Timed out",
        _ => "Network error",
    };
}

/// <summary>Form-bound SOAP draft. Empty strings on every section is a clean draft.</summary>
public sealed record NoteDraft(string Subjective, string Objective, string Assessment, string Plan)
{
    public static readonly NoteDraft Empty = new(string.Empty, string.Empty, string.Empty, string.Empty);
}

/// <summary>One row in the per-patient note history list.</summary>
public sealed record NoteSummary(
    string Title,
    string? Category,
    string Status,
    DateTimeOffset? AuthoredAt);

/// <summary>Aggregate the Notes tab renders.</summary>
public sealed record NotesPageData(
    IReadOnlyList<NoteSummary> History,
    NoteDraft Draft,
    string? Error)
{
    public static NotesPageData Failure(string error) =>
        new(Array.Empty<NoteSummary>(), NoteDraft.Empty, error);
}

public sealed record NoteWriteResult(NoteWriteStatus Status, string? WrittenId, string? Message)
{
    public static NoteWriteResult Ok(string id) => new(NoteWriteStatus.Ok, id, null);
    public static NoteWriteResult NotAuthorised() => new(NoteWriteStatus.NotAuthorised, null, null);
    public static NoteWriteResult Failed(string message) => new(NoteWriteStatus.Failed, null, message);
}

public enum NoteWriteStatus { Ok, NotAuthorised, Failed }
