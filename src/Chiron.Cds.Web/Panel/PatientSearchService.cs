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

    private readonly TenantRegistry _tenants;
    private readonly ILogger<PatientSearchService> _log;

    public PatientSearchService(TenantRegistry tenants, ILogger<PatientSearchService> log)
    {
        _tenants = tenants;
        _log = log;
    }

    public virtual async Task<PatientSearchResult> SearchAsync(string query, CancellationToken ct)
    {
        var trimmed = query?.Trim() ?? string.Empty;
        if (trimmed.Length == 0) return PatientSearchResult.Empty;
        if (trimmed.Length < MinQueryLength)
            return new PatientSearchResult(
                Array.Empty<PatientSearchHit>(),
                Warning: $"Type at least {MinQueryLength} characters — a single-letter search times out on most FHIR servers.");

        var tenant = _tenants.Default.AsOpen();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(SearchTimeout);

        try
        {
            var bundle = await ExecuteSearchAsync(tenant, trimmed, MaxResults, timeoutCts.Token)
                .ConfigureAwait(false);
            if (bundle?.Entry is null) return PatientSearchResult.Empty;
            var hits = bundle.Entry
                .Select(e => e.Resource)
                .OfType<Patient>()
                .Take(MaxResults)
                .Select(ToHit)
                .ToArray();
            return new PatientSearchResult(hits, Warning: null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning("Patient search for {Query} timed out after {Seconds}s.", trimmed, SearchTimeout.TotalSeconds);
            return new PatientSearchResult(
                Array.Empty<PatientSearchHit>(),
                Warning: "Search timed out. Try a more specific query (e.g. a full last name).");
        }
        catch (Exception ex) when (ex is Hl7.Fhir.Rest.FhirOperationException or HttpRequestException)
        {
            _log.LogWarning(ex, "Patient search failed for query {Query}.", trimmed);
            return new PatientSearchResult(
                Array.Empty<PatientSearchHit>(),
                Warning: "Search failed — the FHIR endpoint returned an error. Check the connected tenant and try again.");
        }
    }

    /// <summary>
    /// Issue the FHIR Patient search. Pass the raw query through to Firely —
    /// its query builder URL-encodes internally; pre-escaping would
    /// double-encode (an apostrophe would travel as %2527 and silently
    /// never match). Exposed as virtual so tests can simulate timeouts and
    /// transport errors without standing up a real FHIR server.
    /// </summary>
    protected virtual async Task<Bundle?> ExecuteSearchAsync(
        TenantConfig tenant, string query, int maxResults, CancellationToken ct)
    {
        using var client = new TenantFhirClient(tenant, accessToken: null);
        return await client.SearchAsync<Patient>(
            new[] { $"name={query}", $"_count={maxResults}" }, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Project a FHIR <see cref="Patient"/> into the search-hit DTO.</summary>
    internal static PatientSearchHit ToHit(Patient p)
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
            BirthDate: p.BirthDate ?? string.Empty);
    }
}

public sealed record PatientSearchHit(
    string PatientId,
    string DisplayName,
    string Gender,
    string BirthDate);

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
