using Chiron.Cds.Web.FhirClient;
using Chiron.Cds.Web.Tenancy;
using Hl7.Fhir.Model;
using Task = System.Threading.Tasks.Task;

namespace Chiron.Cds.Web.Panel;

/// <summary>
/// Searches the open FHIR endpoint for patients matching a name query.
/// This is the search-by-name surface backing <c>/app/search?q=…</c>. In
/// production the same code path will run against the authenticated
/// endpoint with the SMART access token — the <see cref="TenantFhirClient"/>
/// is already auth-aware.
/// </summary>
public class PatientSearchService
{
    private const int MaxResults = 20;
    private const int MinQueryLength = 2;

    // Single-character prefixes blow the index up on every FHIR server I've
    // tested; cap our wait at 12 s and surface a friendly warning rather
    // than the full 30 s HttpClient timeout the dev exception page rendered.
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(12);

    private readonly FhirReadConnection _connection;
    private readonly ILogger<PatientSearchService> _log;

    public PatientSearchService(FhirReadConnection connection, ILogger<PatientSearchService> log)
    {
        _connection = connection;
        _log = log;
    }

    public virtual async Task<PatientSearchResult> SearchAsync(PatientSearchCriteria criteria, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        if (criteria.IsEmpty) return PatientSearchResult.Empty;

        var connection = await _connection.ResolveAsync(ct).ConfigureAwait(false);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(SearchTimeout);

        try
        {
            return await RunStrategyAsync(criteria, connection, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning("Patient search timed out after {Seconds}s.", SearchTimeout.TotalSeconds);
            return Warn("Search timed out. Try a more specific query.");
        }
        catch (Exception ex) when (ex is Hl7.Fhir.Rest.FhirOperationException or HttpRequestException)
        {
            _log.LogWarning(ex, "Patient search failed.");
            return Warn("Search failed — the FHIR endpoint returned an error. Check the connected tenant and try again.");
        }
    }

    // Pick the lookup strategy by precedence: MRN (most specific) → encounter →
    // name + date of birth. The sandbox rejects a bare name search, so a name
    // query requires a date of birth alongside it.
    private async Task<PatientSearchResult> RunStrategyAsync(
        PatientSearchCriteria criteria, FhirConnection connection, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(criteria.Mrn))
        {
            var bundle = await ExecutePatientSearchAsync(connection.Tenant, connection.AccessToken,
                new[] { $"identifier={criteria.Mrn.Trim()}", $"_count={MaxResults}" }, ct).ConfigureAwait(false);
            return ToResult(bundle, connection.Tenant.MrnSystem);
        }

        if (!string.IsNullOrWhiteSpace(criteria.EncounterId))
        {
            var patient = await ResolveEncounterPatientAsync(
                connection.Tenant, connection.AccessToken, criteria.EncounterId.Trim(), ct).ConfigureAwait(false);
            return patient is null
                ? Warn("No patient found for that encounter id.")
                : new PatientSearchResult(new[] { ToHit(patient, connection.Tenant.MrnSystem) }, Warning: null);
        }

        var name = criteria.Name?.Trim() ?? string.Empty;
        if (name.Length < MinQueryLength)
            return Warn($"Type at least {MinQueryLength} characters of a name, or use the MRN / encounter id field.");
        if (string.IsNullOrWhiteSpace(criteria.BirthDate))
            return Warn("Add a date of birth — the sandbox requires it alongside a name search.");

        var (family, given) = SplitName(name);
        var fhir = new List<string> { $"family={family}", $"birthdate={criteria.BirthDate.Trim()}" };
        if (!string.IsNullOrEmpty(given)) fhir.Add($"given={given}");
        fhir.Add($"_count={MaxResults}");
        return ToResult(await ExecutePatientSearchAsync(
            connection.Tenant, connection.AccessToken, fhir.ToArray(), ct).ConfigureAwait(false), connection.Tenant.MrnSystem);
    }

    /// <summary>Split a free-text name into (family, given). "Lopez, Camila" and "Camila Lopez" both yield family=Lopez.</summary>
    internal static (string Family, string? Given) SplitName(string name)
    {
        if (name.Contains(','))
        {
            var parts = name.Split(',', 2);
            var given = parts.Length > 1 ? parts[1].Trim() : null;
            return (parts[0].Trim(), string.IsNullOrEmpty(given) ? null : given);
        }
        var tokens = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length >= 2
            ? (tokens[^1], string.Join(' ', tokens[..^1]))
            : (name, null);
    }

    private static PatientSearchResult ToResult(Bundle? bundle, string? mrnSystem)
    {
        if (bundle?.Entry is null) return PatientSearchResult.Empty;
        var hits = bundle.Entry
            .Select(e => e.Resource)
            .OfType<Patient>()
            .Take(MaxResults)
            .Select(p => ToHit(p, mrnSystem))
            .ToArray();
        return new PatientSearchResult(hits, Warning: null);
    }

    private static PatientSearchResult Warn(string message) =>
        new(Array.Empty<PatientSearchHit>(), message);

    /// <summary>
    /// Issue a FHIR Patient search. Firely URL-encodes the criteria internally;
    /// pre-escaping would double-encode. Virtual so tests can simulate timeouts
    /// and transport errors without a real FHIR server.
    /// </summary>
    protected virtual async Task<Bundle?> ExecutePatientSearchAsync(
        TenantConfig tenant, string? accessToken, string[] criteria, CancellationToken ct)
    {
        using var client = new TenantFhirClient(tenant, accessToken);
        return await client.SearchAsync<Patient>(criteria, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolve the patient behind an encounter id: read the Encounter, then its
    /// subject Patient. Virtual so tests can stub the two reads.
    /// </summary>
    protected virtual async Task<Patient?> ResolveEncounterPatientAsync(
        TenantConfig tenant, string? accessToken, string encounterId, CancellationToken ct)
    {
        using var client = new TenantFhirClient(tenant, accessToken);
        var encounter = await client.ReadAsync<Encounter>(encounterId, ct).ConfigureAwait(false);
        var subject = encounter?.Subject?.Reference;
        if (string.IsNullOrEmpty(subject)) return null;
        var patientId = subject.Split('/')[^1];
        return string.IsNullOrEmpty(patientId)
            ? null
            : await client.ReadAsync<Patient>(patientId, ct).ConfigureAwait(false);
    }

    /// <summary>Project a FHIR <see cref="Patient"/> into the search-hit DTO.</summary>
    internal static PatientSearchHit ToHit(Patient p, string? mrnSystem)
    {
        var name = p.Name?.FirstOrDefault();
        var display = name?.Text;
        if (string.IsNullOrWhiteSpace(display))
        {
            var family = name?.Family ?? string.Empty;
            var given = name?.Given is null ? string.Empty : string.Join(' ', name.Given);
            display = string.Join(", ",
                new[] { family, given }.Where(s => !string.IsNullOrWhiteSpace(s)));
        }
        return new PatientSearchHit(
            PatientId: p.Id ?? string.Empty,
            DisplayName: string.IsNullOrWhiteSpace(display) ? $"Patient {p.Id}" : display,
            Gender: p.Gender?.ToString() ?? string.Empty,
            BirthDate: p.BirthDate ?? string.Empty,
            Mrn: PatientMrn.Extract(p, mrnSystem));
    }
}

/// <summary>
/// Inputs to a patient lookup. Any subset may be filled; the service picks a
/// strategy by precedence (MRN → encounter id → name + date of birth).
/// </summary>
public sealed record PatientSearchCriteria(string? Name, string? BirthDate, string? Mrn, string? EncounterId)
{
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Name) && string.IsNullOrWhiteSpace(BirthDate)
        && string.IsNullOrWhiteSpace(Mrn) && string.IsNullOrWhiteSpace(EncounterId);
}

public sealed record PatientSearchHit(
    string PatientId,
    string DisplayName,
    string Gender,
    string BirthDate,
    string? Mrn = null);

/// <summary>
/// Result of a <see cref="PatientSearchService.SearchAsync"/> call. The
/// optional <see cref="Warning"/> carries a user-facing message (timeout,
/// failure, too-short query) the renderer surfaces above the results.
/// </summary>
public sealed record PatientSearchResult(
    IReadOnlyList<PatientSearchHit> Hits,
    string? Warning)
{
    public static readonly PatientSearchResult Empty = new(Array.Empty<PatientSearchHit>(), null);
}
