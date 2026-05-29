using Chiron.Cds.Web.FhirClient;
using Chiron.Cds.Web.Tenancy;

namespace Chiron.Cds.Web.IntegrationTests;

/// <summary>
/// Test double for <see cref="FhirReadConnection"/> that returns a fixed
/// tenant + token without touching the backend OAuth path. Used by service
/// stubs whose overridden fetch methods ignore the connection but whose public
/// methods still resolve one.
/// </summary>
internal sealed class StubReadConnection : FhirReadConnection
{
    private readonly FhirConnection _connection;

    public StubReadConnection(TenantConfig tenant, string? accessToken = null)
        => _connection = new FhirConnection(tenant, accessToken);

    public override Task<FhirConnection> ResolveAsync(CancellationToken ct) =>
        Task.FromResult(_connection);
}
