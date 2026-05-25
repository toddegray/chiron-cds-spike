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

    private readonly TenantRegistry _tenants;
    private readonly ILogger<PatientSearchService> _log;

    public PatientSearchService(TenantRegistry tenants, ILogger<PatientSearchService> log)
    {
        _tenants = tenants;
        _log = log;
    }

    public virtual async Task<IReadOnlyList<PatientSearchHit>> SearchAsync(string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<PatientSearchHit>();

        var tenant = _tenants.Default.AsOpen();
        using var client = new TenantFhirClient(tenant, accessToken: null);
        try
        {
            // Pass the raw value to Firely — its query builder URL-encodes
            // internally. Pre-escaping would double-encode (an apostrophe
            // would travel as %2527 and silently never match).
            var bundle = await client.SearchAsync<Patient>(
                new[] { $"name={query.Trim()}", $"_count={MaxResults}" }, ct)
                .ConfigureAwait(false);
            if (bundle?.Entry is null) return Array.Empty<PatientSearchHit>();
            return bundle.Entry
                .Select(e => e.Resource)
                .OfType<Patient>()
                .Take(MaxResults)
                .Select(ToHit)
                .ToArray();
        }
        catch (Hl7.Fhir.Rest.FhirOperationException ex)
        {
            _log.LogWarning(ex, "Patient search failed for query {Query}.", query);
            return Array.Empty<PatientSearchHit>();
        }
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
