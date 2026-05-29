using System.Net.Http.Headers;

using Chiron.Cds.Web.Tenancy;
using Hl7.Fhir.Model;
using Hl7.Fhir.Rest;
using FirelyClient = Hl7.Fhir.Rest.FhirClient;

namespace Chiron.Cds.Web.FhirClient;

/// <summary>
/// Per-tenant Firely <see cref="FirelyClient"/> wrapper that injects the
/// SMART access token on every outbound request. One instance per request;
/// not thread-safe across tenants because the underlying client carries
/// authentication state.
/// </summary>
public sealed class TenantFhirClient : IDisposable
{
    private readonly FirelyClient _client;
    private readonly HttpClient _http;

    public TenantFhirClient(TenantConfig tenant, string? accessToken)
        : this(tenant, accessToken, new HttpClient { Timeout = TimeSpan.FromSeconds(30) }) { }

    /// <summary>
    /// Test-only seam. Tests inject a <see cref="HttpClient"/> backed by a
    /// stub <see cref="HttpMessageHandler"/> so they can assert the wire
    /// contract (method, path, body) without standing up a real FHIR
    /// server. Not for production use.
    /// </summary>
    internal TenantFhirClient(TenantConfig tenant, string? accessToken, HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
        if (!string.IsNullOrEmpty(accessToken))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        _client = new FirelyClient(
            EnsureTrailingSlash(tenant.FhirBaseUrl),
            _http,
            new FhirClientSettings { PreferredFormat = ResourceFormat.Json });
    }

    // A FHIR base whose path lacks a trailing slash (e.g. ".../api/FHIR/R4")
    // loses its last segment when combined with a relative resource path
    // ("Patient/123") under standard URI rules, yielding ".../api/FHIR/Patient/123"
    // — a 404. Append the slash so the full base is preserved.
    private static Uri EnsureTrailingSlash(Uri baseUrl) =>
        baseUrl.AbsoluteUri.EndsWith('/') ? baseUrl : new Uri(baseUrl.AbsoluteUri + "/");

    /// <summary>Read a single resource by id.</summary>
    public System.Threading.Tasks.Task<TResource?> ReadAsync<TResource>(string id, CancellationToken ct)
        where TResource : Resource, new()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var resourceName = typeof(TResource).Name;
        return _client.ReadAsync<TResource>($"{resourceName}/{id}", null, null, ct);
    }

    /// <summary>Search for resources, returning the full bundle (single page).</summary>
    public System.Threading.Tasks.Task<Bundle?> SearchAsync<TResource>(string[] criteria, CancellationToken ct)
        where TResource : Resource, new()
    {
        ArgumentNullException.ThrowIfNull(criteria);
        return _client.SearchAsync<TResource>(
            criteria,
            includes: (string[]?)null,
            pageSize: null,
            summary: null,
            revIncludes: (string[]?)null,
            ct);
    }

    /// <summary>Create a resource. Returns the server-assigned location.</summary>
    public async System.Threading.Tasks.Task<TResource> CreateAsync<TResource>(TResource resource, CancellationToken ct)
        where TResource : Resource, new()
    {
        ArgumentNullException.ThrowIfNull(resource);
        var created = await _client.CreateAsync(resource, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"FHIR server returned a null body when creating {typeof(TResource).Name}.");
        return created;
    }

    /// <summary>
    /// Update an existing resource by id (FHIR <c>update</c>: PUT
    /// <c>/{ResourceType}/{id}</c>). The supplied resource must carry the
    /// id of an existing server-side record; otherwise behaviour depends on
    /// the server's update-create policy and is unsafe for our use case.
    /// </summary>
    public async System.Threading.Tasks.Task<TResource> UpdateAsync<TResource>(TResource resource, CancellationToken ct)
        where TResource : Resource, new()
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (string.IsNullOrEmpty(resource.Id))
            throw new InvalidOperationException(
                $"Update target {typeof(TResource).Name} must have a non-empty Id.");
        var updated = await _client.UpdateAsync(resource, ct: ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"FHIR server returned a null body when updating {typeof(TResource).Name}/{resource.Id}.");
        return updated;
    }

    public void Dispose()
    {
        _client.Dispose();
        _http.Dispose();
    }
}
