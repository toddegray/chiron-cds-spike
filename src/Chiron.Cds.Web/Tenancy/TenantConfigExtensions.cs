namespace Chiron.Cds.Web.Tenancy;

/// <summary>
/// Convenience extensions over <see cref="TenantConfig"/>. Kept in the
/// Tenancy namespace so both the SMART-launch and replacement-mode code
/// paths reach for the same helper.
/// </summary>
public static class TenantConfigExtensions
{
    /// <summary>
    /// Return a copy of the tenant whose <see cref="TenantConfig.FhirBaseUrl"/>
    /// points at the open (unauthenticated) FHIR endpoint. Used by the
    /// replacement-mode panel + search surfaces to share the existing
    /// chart-fetching pipeline against the public sandbox without a SMART
    /// session.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the tenant has no <see cref="TenantConfig.FhirOpenBaseUrl"/>
    /// configured — without it there is no unauthenticated endpoint to hit.
    /// </exception>
    public static TenantConfig AsOpen(this TenantConfig tenant)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        if (tenant.FhirOpenBaseUrl is null)
            throw new InvalidOperationException(
                $"Tenant '{tenant.Id}' has no FhirOpenBaseUrl configured; the open " +
                "surface needs an unauthenticated FHIR endpoint to run without a SMART session.");
        return tenant with { FhirBaseUrl = tenant.FhirOpenBaseUrl };
    }
}
