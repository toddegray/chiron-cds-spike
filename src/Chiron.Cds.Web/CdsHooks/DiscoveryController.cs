using Chiron.Cds.Web.CdsHooks.Models;
using Microsoft.AspNetCore.Mvc;

namespace Chiron.Cds.Web.CdsHooks;

/// <summary>
/// CDS Hooks discovery endpoint. EHRs hit this once at registration time
/// to learn what services we offer and what prefetch they can pre-populate.
/// </summary>
[ApiController]
[Route("cds-services")]
public sealed class DiscoveryController : ControllerBase
{
    public const string PatientViewServiceId = "chiron-patient-view";

    private static readonly CdsServiceDescriptor PatientViewService = new(
        Hook: "patient-view",
        Id: PatientViewServiceId,
        Title: "Chiron Clinical Reasoning",
        Description: "Explainable CDS alerts at patient-view time. Every card carries its full derivation graph.",
        Prefetch: new Dictionary<string, string>
        {
            ["patient"] = "Patient/{{context.patientId}}",
            ["conditions"] = "Condition?patient={{context.patientId}}",
            ["observations"] = "Observation?patient={{context.patientId}}&category=laboratory",
            ["medications"] = "MedicationRequest?patient={{context.patientId}}&status=active",
        });

    [HttpGet]
    public CdsServicesResponse Get() => new(Services: new[] { PatientViewService });
}
