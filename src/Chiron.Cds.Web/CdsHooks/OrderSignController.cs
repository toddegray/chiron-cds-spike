using Chiron.Cds.Web.CdsHooks.Models;
using Microsoft.AspNetCore.Mvc;

namespace Chiron.Cds.Web.CdsHooks;

/// <summary>
/// CDS Hooks <c>order-sign</c> service. Fires at the moment the
/// clinician signs the order set — final chance to surface any chart-
/// wide concerns the clinician hasn't yet acknowledged.
/// </summary>
/// <remarks>
/// Like the other order-context hooks, the spike evaluates against the
/// patient's chart only; the orders-about-to-be-signed delta is a
/// Phase 2 enhancement that requires the engine to accept a "proposed
/// inputs" overlay.
/// </remarks>
[ApiController]
[Route("cds-services/" + DiscoveryController.OrderSignServiceId)]
public sealed class OrderSignController : ControllerBase
{
    private readonly PatientViewService _service;

    public OrderSignController(PatientViewService service)
    {
        _service = service;
    }

    [HttpPost]
    public async Task<ActionResult<CdsHookResponse>> Post(
        [FromBody] CdsHookRequest request,
        CancellationToken ct)
    {
        if (request is null) return BadRequest();
        var response = await _service.EvaluateAsync(request, ct).ConfigureAwait(false);
        return Ok(response);
    }
}
