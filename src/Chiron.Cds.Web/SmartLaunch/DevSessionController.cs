using Chiron.Cds.Shared;
using Chiron.Cds.Web.Tenancy;
using Microsoft.AspNetCore.Mvc;

namespace Chiron.Cds.Web.SmartLaunch;

/// <summary>
/// Development-only escape hatch that mints a <see cref="SmartSession"/> from an
/// access token obtained out-of-band (e.g. Epic's LaunchPad), then hands off to
/// the normal <c>/app</c> render path. It exists so the authenticated FHIR data
/// flow can be exercised end-to-end when an EHR's interactive login can't be
/// completed locally. It performs NO token validation — the token's validity is
/// proven only when the downstream FHIR read succeeds — so it is gated to the
/// Development environment and returns 404 everywhere else.
/// </summary>
[ApiController]
[Route("smart")]
public sealed class DevSessionController : ControllerBase
{
    private readonly IWebHostEnvironment _env;
    private readonly ITokenStore _store;
    private readonly TenantRegistry _tenants;
    private readonly ILogger<DevSessionController> _log;

    // SMART access tokens are short-lived; mirror a typical ~1h grant minus
    // headroom so the minted session doesn't outlive the real token by much.
    private const int AssumedTokenLifetimeSeconds = 3300;

    public DevSessionController(
        IWebHostEnvironment env,
        ITokenStore store,
        TenantRegistry tenants,
        ILogger<DevSessionController> log)
    {
        _env = env;
        _store = store;
        _tenants = tenants;
        _log = log;
    }

    [HttpGet("dev-session")]
    public IActionResult DevSession(
        [FromQuery(Name = "access_token")] string? accessToken,
        [FromQuery(Name = "patient")] string? patient,
        [FromQuery(Name = "tenant")] string tenant = "epic-sandbox")
    {
        // 404 (not 403) outside Development so the route's existence isn't
        // advertised in production.
        if (!_env.IsDevelopment())
            return NotFound();

        if (string.IsNullOrEmpty(accessToken) || string.IsNullOrEmpty(patient))
            return BadRequest(new { error = "missing_parameter", error_description = "access_token and patient are required." });

        TenantConfig tenantConfig;
        try
        {
            tenantConfig = _tenants.GetById(tenant);
        }
        catch (UnknownTenantException ex)
        {
            return BadRequest(new { error = "unknown_tenant", error_description = ex.Message });
        }

        var session = AuthorizationService.CreateSession(
            tenantConfig.Id, accessToken, refreshToken: null,
            patient, encounterId: null, idToken: null,
            AssumedTokenLifetimeSeconds, tenantConfig.Scopes);

        _store.SaveSession(session);
        _log.LogInformation(
            "Dev session minted for tenant {Tenant}, patient {Patient}.", tenantConfig.Id, patient);
        return Redirect($"/app?session={Uri.EscapeDataString(session.SessionId)}");
    }
}
