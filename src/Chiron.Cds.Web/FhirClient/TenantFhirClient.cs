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
    {
        ArgumentNullException.ThrowIfNull(tenant);
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        if (!string.IsNullOrEmpty(accessToken))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        _client = new FirelyClient(
            tenant.FhirBaseUrl,
            _http,
            new FhirClientSettings { PreferredFormat = ResourceFormat.Json });
    }

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

    public void Dispose()
    {
        _client.Dispose();
        _http.Dispose();
    }
}
