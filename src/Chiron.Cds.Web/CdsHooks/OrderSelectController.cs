using Chiron.Cds.Web.CdsHooks.Models;
using Microsoft.AspNetCore.Mvc;

namespace Chiron.Cds.Web.CdsHooks;

/// <summary>
/// CDS Hooks <c>order-select</c> service. Fires during order entry when
/// the clinician picks a draft order; the EHR posts the chart prefetch
/// and the draft orders here, and Chiron returns cards to render inline
/// in the order screen.
/// </summary>
/// <remarks>
/// The spike evaluates against the patient's chart only. A richer
/// implementation would also project the draft order into a synthetic
/// "proposed Medication" and re-evaluate the drug-allergy / drug-dose
/// rules with the order included — that requires the engine to accept a
/// "proposed inputs" delta, which is a Phase 2 enhancement.
/// </remarks>
[ApiController]
[Route("cds-services/" + DiscoveryController.OrderSelectServiceId)]
public sealed class OrderSelectController : ControllerBase
{
    private readonly PatientViewService _service;

    public OrderSelectController(PatientViewService service)
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
