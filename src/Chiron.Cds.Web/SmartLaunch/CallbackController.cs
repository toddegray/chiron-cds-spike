using Chiron.Cds.Shared;
using Microsoft.AspNetCore.Mvc;

namespace Chiron.Cds.Web.SmartLaunch;

/// <summary>OAuth callback that completes the SMART launch.</summary>
[ApiController]
[Route("smart")]
public sealed class CallbackController : ControllerBase
{
    private readonly AuthorizationService _auth;
    private readonly ILogger<CallbackController> _log;

    public CallbackController(AuthorizationService auth, ILogger<CallbackController> log)
    {
        _auth = auth;
        _log = log;
    }

    /// <summary>
    /// EHR redirects here after the user authenticates and authorizes. We
    /// validate the <c>state</c>, exchange the <c>code</c> for tokens, and
    /// 302 to the post-launch landing page with the session id.
    /// </summary>
    [HttpGet("callback")]
    public async Task<IActionResult> Callback(
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error,
        [FromQuery] string? error_description,
        CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(error))
        {
            _log.LogWarning("Authorization endpoint returned error '{Error}': {Description}", error, error_description);
            return BadRequest(new { error, error_description });
        }

        try
        {
            var session = await _auth.ExchangeCodeAsync(code ?? string.Empty, state ?? string.Empty, ct).ConfigureAwait(false);
            _log.LogInformation(
                "SMART launch completed for tenant {Tenant}, patient {Patient}.",
                session.TenantId, session.PatientId);
            return Redirect($"/app?session={Uri.EscapeDataString(session.SessionId)}");
        }
        catch (InvalidLaunchStateException ex)
        {
            return BadRequest(new { error = "invalid_state", error_description = ex.Message });
        }
        catch (TokenExchangeException ex)
        {
            return BadRequest(new { error = "token_exchange_failed", error_description = ex.Message });
        }
    }
}
