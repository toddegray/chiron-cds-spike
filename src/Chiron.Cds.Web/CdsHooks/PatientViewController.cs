using Chiron.Cds.Web.CdsHooks.Models;
using Microsoft.AspNetCore.Mvc;

namespace Chiron.Cds.Web.CdsHooks;

/// <summary>POST endpoint for the <c>chiron-patient-view</c> service.</summary>
[ApiController]
[Route("cds-services/" + DiscoveryController.PatientViewServiceId)]
public sealed class PatientViewController : ControllerBase
{
    private readonly PatientViewService _service;

    public PatientViewController(PatientViewService service)
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
