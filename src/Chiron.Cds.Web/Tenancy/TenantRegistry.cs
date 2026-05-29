using Chiron.Cds.Shared;
using Chiron.Cds.Web.Configuration;
using Microsoft.Extensions.Options;

namespace Chiron.Cds.Web.Tenancy;

/// <summary>
/// Read-only lookup of <see cref="TenantConfig"/>. Built once at startup
/// from <see cref="ChironOptions"/> and accessed everywhere else via DI.
/// </summary>
public sealed class TenantRegistry
{
    private readonly Dictionary<string, TenantConfig> _byId;
    private readonly Dictionary<string, TenantConfig> _byFhirBase;

    public TenantRegistry(IOptions<ChironOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var opts = options.Value;

        _byId = new(StringComparer.OrdinalIgnoreCase);
        _byFhirBase = new(StringComparer.OrdinalIgnoreCase);

        foreach (var (id, t) in opts.Tenants)
        {
            if (string.IsNullOrWhiteSpace(t.FhirBaseUrl))
                throw new InvalidOperationException(
                    $"Tenant '{id}' has no FhirBaseUrl configured.");
            if (string.IsNullOrWhiteSpace(t.ClientId))
                throw new InvalidOperationException(
                    $"Tenant '{id}' has no ClientId configured.");

            var fhirBase = new Uri(t.FhirBaseUrl, UriKind.Absolute);
            Uri? fhirOpen = null;
            if (!string.IsNullOrWhiteSpace(t.FhirOpenBaseUrl))
                fhirOpen = new Uri(t.FhirOpenBaseUrl, UriKind.Absolute);

            var cfg = new TenantConfig(
                Id: id,
                DisplayName: string.IsNullOrWhiteSpace(t.DisplayName) ? id : t.DisplayName,
                ClientId: t.ClientId,
                ClientSecret: t.ClientSecret,
                FhirBaseUrl: fhirBase,
                FhirOpenBaseUrl: fhirOpen,
                Scopes: t.Scopes,
                MrnSystem: t.MrnSystem);

            _byId[id] = cfg;
            _byFhirBase[Normalize(fhirBase.AbsoluteUri)] = cfg;
        }

        Default = !string.IsNullOrWhiteSpace(opts.DefaultTenant)
            && _byId.TryGetValue(opts.DefaultTenant, out var def)
                ? def
                : _byId.Values.FirstOrDefault()
                    ?? throw new InvalidOperationException(
                        "No tenants configured. Set Chiron.Tenants in appsettings.json.");
    }

    /// <summary>The tenant chosen when no <c>iss</c> is supplied.</summary>
    public TenantConfig Default { get; }

    public IReadOnlyCollection<TenantConfig> All => _byId.Values;

    public TenantConfig GetById(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new UnknownTenantException(id ?? "");
        return _byId.TryGetValue(id, out var t) ? t : throw new UnknownTenantException(id);
    }

    /// <summary>
    /// Resolve a tenant from the OAuth <c>iss</c> (which equals the FHIR base
    /// URL in SMART App Launch). This is the anti-spoofing check on the
    /// inbound launch.
    /// </summary>
    public TenantConfig GetByFhirBase(string iss)
    {
        if (string.IsNullOrWhiteSpace(iss)) throw new UntrustedIssuerException(iss ?? "");
        return _byFhirBase.TryGetValue(Normalize(iss), out var t)
            ? t
            : throw new UntrustedIssuerException(iss);
    }

    private static string Normalize(string url) => url.TrimEnd('/');
}
