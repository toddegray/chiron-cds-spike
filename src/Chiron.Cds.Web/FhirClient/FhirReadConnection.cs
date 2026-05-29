using Chiron.Cds.Web.SmartLaunch;
using Chiron.Cds.Web.Tenancy;

namespace Chiron.Cds.Web.FhirClient;

/// <summary>
/// Resolves the FHIR connection the read surfaces (panel, search, results,
/// Visit Brief) should use. When SMART Backend Services is configured it
/// returns the authenticated tenant plus a freshly minted system token; with
/// no backend it falls back to the open (unauthenticated) sandbox endpoint —
/// the original demo behaviour.
/// </summary>
public class FhirReadConnection
{
    private readonly BackendAuthService _backend;
    private readonly TenantRegistry _tenants;

    /// <summary>Test seam: subclasses override <see cref="ResolveAsync"/> and skip this ctor.</summary>
    protected FhirReadConnection()
    {
        _backend = null!;
        _tenants = null!;
    }

    public FhirReadConnection(BackendAuthService backend, TenantRegistry tenants)
    {
        _backend = backend;
        _tenants = tenants;
    }

    public virtual async Task<FhirConnection> ResolveAsync(CancellationToken ct)
    {
        if (_backend.IsConfigured)
        {
            var token = await _backend.GetAccessTokenAsync(ct).ConfigureAwait(false);
            return new FhirConnection(_backend.Tenant, token.AccessToken);
        }
        return new FhirConnection(_tenants.Default.AsOpen(), AccessToken: null);
    }
}

/// <summary>The tenant + bearer token a read surface should use for its FHIR calls.</summary>
public readonly record struct FhirConnection(TenantConfig Tenant, string? AccessToken);
