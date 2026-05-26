using Chiron.Cds.Web.FhirClient;
using Chiron.Cds.Web.Tenancy;
using Hl7.Fhir.Model;
using Task = System.Threading.Tasks.Task;

namespace Chiron.Cds.Web.Panel;

/// <summary>Backs the per-patient Sign-off tab: lists encounters and closes the active one.</summary>
public class EncounterCloseService
{
    private const int MaxHistory = 10;

    private readonly TenantRegistry _tenants;
    private readonly ILogger<EncounterCloseService> _log;

    public EncounterCloseService(TenantRegistry tenants, ILogger<EncounterCloseService> log)
    {
        _tenants = tenants;
        _log = log;
    }

    public virtual async Task<SignOffPageData> GetForPatientAsync(string patientId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(patientId);
        var tenant = _tenants.Default.AsOpen();
        try
        {
            var bundle = await SearchEncountersAsync(tenant, patientId, ct).ConfigureAwait(false);
            var encounters = (bundle?.Entry ?? Enumerable.Empty<Bundle.EntryComponent>())
                .Select(e => e.Resource).OfType<Encounter>()
                .Select(ProjectSummary)
                .OrderByDescending(e => e.PeriodStart ?? DateTimeOffset.MinValue)
                .Take(MaxHistory)
                .ToArray();
            return new SignOffPageData(encounters, Error: null);
        }
        catch (Exception ex) when (ex is Hl7.Fhir.Rest.FhirOperationException or HttpRequestException or TaskCanceledException)
        {
            _log.LogWarning(ex, "Encounter search failed for patient {Id}.", patientId);
            return SignOffPageData.Failure(SummariseError(ex));
        }
    }

    public virtual async Task<EncounterCloseResult> CloseAsync(
        string patientId, string encounterId, string? accessToken, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(patientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(encounterId);

        if (string.IsNullOrEmpty(accessToken))
            return EncounterCloseResult.NotAuthorised();

        try
        {
            var existing = await FetchEncounterAsync(encounterId, accessToken, ct).ConfigureAwait(false);
            if (existing is null)
                return EncounterCloseResult.Failed($"Encounter {encounterId} not found.");
            if (!string.Equals(existing.Subject?.Reference, $"Patient/{patientId}", StringComparison.Ordinal))
                return EncounterCloseResult.Failed($"Encounter {encounterId} does not belong to this patient.");
            if (existing.Status == Encounter.EncounterStatus.Finished)
                return EncounterCloseResult.AlreadyClosed();

            ApplyClose(existing);
            var written = await WriteAsync(existing, accessToken, ct).ConfigureAwait(false);
            return EncounterCloseResult.Ok(written.Id ?? encounterId);
        }
        catch (Exception ex) when (ex is Hl7.Fhir.Rest.FhirOperationException or HttpRequestException)
        {
            _log.LogWarning(ex, "Encounter close failed for patient {Id} / encounter {EncId}.", patientId, encounterId);
            return EncounterCloseResult.Failed("FHIR update failed: " + SummariseError(ex));
        }
    }

    /// <summary>Stamp status=finished and an end timestamp on the encounter resource.</summary>
    internal static void ApplyClose(Encounter encounter)
    {
        ArgumentNullException.ThrowIfNull(encounter);
        encounter.Status = Encounter.EncounterStatus.Finished;
        encounter.Period ??= new Period();
        if (string.IsNullOrEmpty(encounter.Period.End))
            encounter.Period.End = DateTimeOffset.UtcNow.ToString("o");
    }

    /// <summary>Project the FHIR encounter into the row the renderer paints.</summary>
    internal static EncounterSummary ProjectSummary(Encounter e)
    {
        var typeText = e.Type?.FirstOrDefault()?.Text
            ?? e.Type?.SelectMany(t => t.Coding ?? Enumerable.Empty<Coding>())
                .FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.Display))?.Display
            ?? "Encounter";
        var classDisplay = e.Class?.Display
            ?? e.Class?.Code
            ?? string.Empty;
        DateTimeOffset? start = null, end = null;
        if (e.Period?.Start is { } s && DateTimeOffset.TryParse(s, out var ps)) start = ps;
        if (e.Period?.End is { } x && DateTimeOffset.TryParse(x, out var pe)) end = pe;
        return new EncounterSummary(
            EncounterId: e.Id ?? string.Empty,
            Type: typeText,
            Class: classDisplay,
            Status: e.Status?.ToString() ?? "unknown",
            PeriodStart: start,
            PeriodEnd: end);
    }

    /// <summary>Search the patient's encounter history. Virtual so tests can stub.</summary>
    protected virtual async Task<Bundle?> SearchEncountersAsync(
        TenantConfig tenant, string patientId, CancellationToken ct)
    {
        using var client = new TenantFhirClient(tenant, accessToken: null);
        return await client.SearchAsync<Encounter>(
            new[] { $"patient={patientId}", "_count=20" }, ct).ConfigureAwait(false);
    }

    /// <summary>Read a single encounter via the authenticated endpoint. Virtual for tests.</summary>
    protected virtual async Task<Encounter?> FetchEncounterAsync(
        string encounterId, string accessToken, CancellationToken ct)
    {
        var tenant = _tenants.Default;
        using var client = new TenantFhirClient(tenant, accessToken);
        return await client.ReadAsync<Encounter>(encounterId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Update the encounter via FHIR <c>update</c> (HTTP PUT to
    /// <c>/Encounter/{id}</c>) so the existing server-side resource
    /// transitions to status=finished — not a fresh create that would
    /// leave the original encounter forever InProgress. Virtual for tests.
    /// </summary>
    protected virtual async Task<Encounter> WriteAsync(
        Encounter encounter, string accessToken, CancellationToken ct)
    {
        var tenant = _tenants.Default;
        using var client = new TenantFhirClient(tenant, accessToken);
        return await client.UpdateAsync(encounter, ct).ConfigureAwait(false);
    }

    internal static string SummariseError(Exception ex) => ex switch
    {
        Hl7.Fhir.Rest.FhirOperationException fop => $"FHIR {(int)fop.Status} {fop.Status}",
        TaskCanceledException => "Timed out",
        _ => "Network error",
    };
}

/// <summary>One encounter row the Sign-off tab renders.</summary>
public sealed record EncounterSummary(
    string EncounterId,
    string Type,
    string Class,
    string Status,
    DateTimeOffset? PeriodStart,
    DateTimeOffset? PeriodEnd)
{
    public bool IsInProgress => string.Equals(Status, "InProgress", StringComparison.OrdinalIgnoreCase);
}

public sealed record SignOffPageData(IReadOnlyList<EncounterSummary> Encounters, string? Error)
{
    public static SignOffPageData Failure(string error) =>
        new(Array.Empty<EncounterSummary>(), error);
}

public sealed record EncounterCloseResult(EncounterCloseStatus Status, string? UpdatedId, string? Message)
{
    public static EncounterCloseResult Ok(string id) => new(EncounterCloseStatus.Ok, id, null);
    public static EncounterCloseResult AlreadyClosed() => new(EncounterCloseStatus.AlreadyClosed, null, null);
    public static EncounterCloseResult NotAuthorised() => new(EncounterCloseStatus.NotAuthorised, null, null);
    public static EncounterCloseResult Failed(string message) => new(EncounterCloseStatus.Failed, null, message);
}

public enum EncounterCloseStatus { Ok, AlreadyClosed, NotAuthorised, Failed }
