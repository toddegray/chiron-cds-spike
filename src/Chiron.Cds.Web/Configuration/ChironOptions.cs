namespace Chiron.Cds.Web.Configuration;

/// <summary>
/// Top-level options bound from the <c>Chiron</c> configuration section.
/// </summary>
public sealed class ChironOptions
{
    public const string SectionName = "Chiron";

    /// <summary>The tenant used when the inbound request doesn't disambiguate.</summary>
    public string DefaultTenant { get; set; } = string.Empty;

    /// <summary>Map of tenant id → tenant configuration.</summary>
    public Dictionary<string, TenantOptions> Tenants { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The base URL the app is hosted at (used to construct the SMART redirect URI).</summary>
    public string BaseUrl { get; set; } = "https://localhost:7099";
}

/// <summary>Per-tenant configuration. Mirrors the registration values at the EHR's developer console.</summary>
public sealed class TenantOptions
{
    public string DisplayName { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Client secret; never committed. Sourced from user-secrets or env vars.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>Authenticated FHIR base used after SMART launch.</summary>
    public string FhirBaseUrl { get; set; } = string.Empty;

    /// <summary>Optional open (unauthenticated) FHIR base, useful for diagnostics. Cerner publishes one for Code sandbox.</summary>
    public string? FhirOpenBaseUrl { get; set; }

    /// <summary>Space-separated scope string requested at authorization time.</summary>
    public string Scopes { get; set; } = string.Empty;
}
