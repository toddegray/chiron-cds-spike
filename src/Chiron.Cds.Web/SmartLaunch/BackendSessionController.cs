using Chiron.Cds.Shared;
using Microsoft.AspNetCore.Mvc;

namespace Chiron.Cds.Web.SmartLaunch;

/// <summary>
/// Development-only entry point for the SMART Backend Services flow. Mints a
/// system access token (no interactive login), wraps it in a
/// <see cref="SmartSession"/> bound to the requested patient, and hands off to
/// the normal <c>/app/patient/{id}</c> render path. Because a system token can
/// read any patient in the tenant, this route is gated to Development and
/// returns 404 everywhere else — the same posture as
/// <see cref="DevSessionController"/>.
/// </summary>
[ApiController]
[Route("smart")]
public sealed class BackendSessionController : ControllerBase
{
    private readonly IWebHostEnvironment _env;
    private readonly BackendAuthService _backend;
    private readonly ITokenStore _store;
    private readonly ILogger<BackendSessionController> _log;

    public BackendSessionController(
        IWebHostEnvironment env,
        BackendAuthService backend,
        ITokenStore store,
        ILogger<BackendSessionController> log)
    {
        _env = env;
        _backend = backend;
        _store = store;
        _log = log;
    }

    [HttpGet("backend-session")]
    public async Task<IActionResult> BackendSession(
        [FromQuery(Name = "patient")] string? patient,
        CancellationToken ct)
    {
        if (!_env.IsDevelopment())
            return NotFound();

        if (string.IsNullOrEmpty(patient))
            return BadRequest(new { error = "missing_parameter", error_description = "patient is required." });

        if (!_backend.IsConfigured)
            return BadRequest(new { error = "not_configured", error_description = "SMART Backend Services is not configured." });

        BackendAuthService.BackendToken token;
        try
        {
            token = await _backend.GetAccessTokenAsync(ct);
        }
        catch (TokenExchangeException ex)
        {
            _log.LogError(ex, "Backend token acquisition failed.");
            return StatusCode(StatusCodes.Status502BadGateway,
                new { error = "backend_auth_failed", error_description = ex.Message });
        }

        var tenant = _backend.Tenant;
        var session = AuthorizationService.CreateSession(
            tenant.Id, token.AccessToken, refreshToken: null,
            patient, encounterId: null, idToken: null,
            token.ExpiresInSeconds, token.Scope);

        _store.SaveSession(session);
        _log.LogInformation("Backend session minted for tenant {Tenant}, patient {Patient}.", tenant.Id, patient);
        // Hand off to the session-aware /app route, which renders using the
        // session (and the token) we just minted.
        return Redirect($"/app?session={Uri.EscapeDataString(session.SessionId)}&patient={Uri.EscapeDataString(patient)}");
    }
}
