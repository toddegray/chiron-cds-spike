namespace Chiron.Cds.Web.Tenancy;

/// <summary>
/// Immutable per-request snapshot of a tenant's configuration. Built by
/// <see cref="TenantRegistry"/> from the <c>ChironOptions</c> on startup so
/// request-path code never reaches into mutable configuration.
/// </summary>
public sealed record TenantConfig(
    string Id,
    string DisplayName,
    string ClientId,
    string? ClientSecret,
    Uri FhirBaseUrl,
    Uri? FhirOpenBaseUrl,
    string Scopes,
    string? MrnSystem = null);
