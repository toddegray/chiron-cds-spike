using Chiron.Cds.Web.CdsHooks.Models;
using Microsoft.AspNetCore.Mvc;

namespace Chiron.Cds.Web.CdsHooks;

/// <summary>
/// CDS Hooks <c>medication-prescribe</c> service. Fires while the
/// clinician is preparing a prescription, before it's signed. Evaluates
/// the chart for drug-allergy, drug-drug interaction, renal dose
/// adjustment, and Beers / geriatric concerns.
/// </summary>
/// <remarks>
/// Like the other order-context hooks, the spike evaluates against the
/// patient's chart only — the about-to-be-prescribed medication delta
/// (in the EHR's <c>medications</c> context field) is a Phase 2
/// enhancement that requires the engine to accept a "proposed inputs"
/// overlay.
/// </remarks>
[ApiController]
[Route("cds-services/" + DiscoveryController.MedicationPrescribeServiceId)]
public sealed class MedicationPrescribeController : ControllerBase
{
    private readonly PatientViewService _service;

    public MedicationPrescribeController(PatientViewService service)
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
