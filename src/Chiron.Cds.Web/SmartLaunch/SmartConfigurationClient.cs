using System.Collections.Concurrent;
using System.Net.Http.Json;

using Chiron.Cds.Shared;

namespace Chiron.Cds.Web.SmartLaunch;

/// <summary>
/// Fetches the <c>.well-known/smart-configuration</c> document for a FHIR
/// base, with per-tenant in-process caching. The SMART App Launch spec
/// requires this dynamic discovery — do not hardcode the authorize / token
/// endpoints.
/// </summary>
public sealed class SmartConfigurationClient
{
    private readonly HttpClient _http;
    private readonly ILogger<SmartConfigurationClient> _log;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(1);

    public SmartConfigurationClient(HttpClient http, ILogger<SmartConfigurationClient> log)
    {
        _http = http;
        _log = log;
    }

    /// <summary>Returns cached value if still fresh; fetches and caches otherwise.</summary>
    public async Task<SmartConfiguration> GetAsync(Uri fhirBase, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fhirBase);
        var key = fhirBase.AbsoluteUri.TrimEnd('/');
        if (_cache.TryGetValue(key, out var entry) && entry.ExpiresAt > DateTimeOffset.UtcNow)
            return entry.Configuration;

        var wellKnownUri = new Uri($"{key}/.well-known/smart-configuration");
        _log.LogInformation("Fetching SMART configuration from {Uri}", wellKnownUri);

        using var resp = await _http.GetAsync(wellKnownUri, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var config = await resp.Content.ReadFromJsonAsync<SmartConfiguration>(cancellationToken: ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Empty SMART configuration response from {wellKnownUri}.");

        _cache[key] = new CacheEntry(config, DateTimeOffset.UtcNow + CacheLifetime);
        return config;
    }

    private sealed record CacheEntry(SmartConfiguration Configuration, DateTimeOffset ExpiresAt);
}
