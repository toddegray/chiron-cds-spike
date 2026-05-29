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

    /// <summary>
    /// SMART Backend Services configuration (client_credentials + private_key_jwt).
    /// Null when the deployment isn't set up for system-to-system FHIR access.
    /// </summary>
    public BackendServiceOptions? EpicBackend { get; set; }

    /// <summary>
    /// PEM-encoded RSA private key whose public half is published at the
    /// Backend Services app's JWK Set URL. Kept as a flat top-level key
    /// (sourced from user-secrets) rather than nested under
    /// <see cref="EpicBackend"/> so it never lands in committed appsettings.
    /// </summary>
    public string? EpicBackendPrivateKeyPem { get; set; }
}

/// <summary>
/// Non-secret settings for the SMART Backend Services flow. The matching
/// private key lives in <see cref="ChironOptions.EpicBackendPrivateKeyPem"/>.
/// </summary>
public sealed class BackendServiceOptions
{
    /// <summary>Tenant whose FHIR base and SMART token endpoint the backend flow targets.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>The Backend Systems app's client id (the <c>iss</c>/<c>sub</c> of the client assertion).</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>JWK <c>kid</c> identifying the signing key in the published JWK Set.</summary>
    public string KeyId { get; set; } = string.Empty;

    /// <summary>Space-separated <c>system/*</c> scopes requested at the token endpoint.</summary>
    public string Scopes { get; set; } = string.Empty;
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

    /// <summary>
    /// Identifier system (OID/URI) under which this EHR exposes the patient MRN.
    /// EHRs vary: Epic publishes the MRN under an org-specific OID with no
    /// standard "MR" type coding, so the system must be configured per tenant.
    /// Null falls back to the standard "MR"-typed identifier.
    /// </summary>
    public string? MrnSystem { get; set; }
}
