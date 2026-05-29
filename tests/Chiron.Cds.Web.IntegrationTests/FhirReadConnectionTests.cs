using Chiron.Cds.Web.Configuration;
using Chiron.Cds.Web.FhirClient;
using Chiron.Cds.Web.SmartLaunch;
using Chiron.Cds.Web.Tenancy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Chiron.Cds.Web.IntegrationTests;

/// <summary>
/// Covers <see cref="FhirReadConnection.ResolveAsync"/>: when the backend is
/// configured it returns the authenticated tenant + minted token; otherwise it
/// falls back to the open (unauthenticated) endpoint with no token.
/// </summary>
public class FhirReadConnectionTests
{
    [Fact]
    public async Task Resolves_Authenticated_Backend_When_Configured()
    {
        var tenants = BuildRegistry();
        var backend = new FakeBackend(tenants, configured: true, tenants.GetById("epic"));
        var connection = new FhirReadConnection(backend, tenants);

        var resolved = await connection.ResolveAsync(CancellationToken.None);

        resolved.Tenant.Id.Should().Be("epic");
        resolved.AccessToken.Should().Be("backend-token",
            because: "the configured branch mints and returns a system token");
    }

    [Fact]
    public async Task Falls_Back_To_Open_Unauthenticated_Endpoint_When_Not_Configured()
    {
        var tenants = BuildRegistry();
        var backend = new FakeBackend(tenants, configured: false, tenants.Default);
        var connection = new FhirReadConnection(backend, tenants);

        var resolved = await connection.ResolveAsync(CancellationToken.None);

        resolved.AccessToken.Should().BeNull(because: "the open endpoint takes no bearer token");
        resolved.Tenant.FhirBaseUrl.Should().Be(tenants.Default.FhirOpenBaseUrl,
            because: "the fallback swaps in the open FHIR base");
    }

    private static TenantRegistry BuildRegistry() => new(Options.Create(new ChironOptions
    {
        DefaultTenant = "open",
        Tenants = new(StringComparer.OrdinalIgnoreCase)
        {
            ["open"] = new TenantOptions
            {
                DisplayName = "Open", ClientId = "c",
                FhirBaseUrl = "https://auth.test/r4", FhirOpenBaseUrl = "https://open.test/r4", Scopes = "",
            },
            ["epic"] = new TenantOptions
            {
                DisplayName = "Epic", ClientId = "e", FhirBaseUrl = "https://fhir.epic.test/r4", Scopes = "",
            },
        },
    }));

    private sealed class FakeBackend : BackendAuthService
    {
        private readonly bool _configured;
        private readonly TenantConfig _tenant;

        public FakeBackend(TenantRegistry tenants, bool configured, TenantConfig tenant)
            : base(tenants,
                   new SmartConfigurationClient(new HttpClient(), NullLogger<SmartConfigurationClient>.Instance),
                   new HttpClient(), Options.Create(new ChironOptions()), NullLogger<BackendAuthService>.Instance)
        {
            _configured = configured;
            _tenant = tenant;
        }

        public override bool IsConfigured => _configured;
        public override TenantConfig Tenant => _tenant;
        public override Task<BackendToken> GetAccessTokenAsync(CancellationToken ct) =>
            Task.FromResult(new BackendToken("backend-token", 3600, "system/Patient.read"));
    }
}
