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
    public const string OrderSelectServiceId = "chiron-order-select";
    public const string OrderSignServiceId = "chiron-order-sign";
    public const string MedicationPrescribeServiceId = "chiron-medication-prescribe";

    private static readonly IReadOnlyDictionary<string, string> StandardPrefetch =
        new Dictionary<string, string>
        {
            ["patient"] = "Patient/{{context.patientId}}",
            ["conditions"] = "Condition?patient={{context.patientId}}",
            ["observations"] = "Observation?patient={{context.patientId}}&category=laboratory",
            ["vitals"] = "Observation?patient={{context.patientId}}&category=vital-signs",
            ["medications"] = "MedicationRequest?patient={{context.patientId}}&status=active",
            ["allergies"] = "AllergyIntolerance?patient={{context.patientId}}",
            ["immunizations"] = "Immunization?patient={{context.patientId}}",
            ["procedures"] = "Procedure?patient={{context.patientId}}",
        };

    /// <summary>The four services Chiron advertises today.</summary>
    private static readonly IReadOnlyList<CdsServiceDescriptor> Services = new[]
    {
        new CdsServiceDescriptor(
            Hook: "patient-view",
            Id: PatientViewServiceId,
            Title: "Chiron Clinical Reasoning — Patient View",
            Description: "Explainable CDS alerts at patient-view time. Every card carries its full derivation graph.",
            Prefetch: StandardPrefetch),
        new CdsServiceDescriptor(
            Hook: "order-select",
            Id: OrderSelectServiceId,
            Title: "Chiron Clinical Reasoning — Order Select",
            Description: "Evaluates the patient's chart against the proposed draft orders during order entry. Surfaces drug-allergy, renal-dose, and Beers concerns inline.",
            Prefetch: StandardPrefetch),
        new CdsServiceDescriptor(
            Hook: "order-sign",
            Id: OrderSignServiceId,
            Title: "Chiron Clinical Reasoning — Order Sign",
            Description: "Final review at order signing. Surfaces any chart-wide alerts the clinician hasn't yet acknowledged.",
            Prefetch: StandardPrefetch),
        new CdsServiceDescriptor(
            Hook: "medication-prescribe",
            Id: MedicationPrescribeServiceId,
            Title: "Chiron Clinical Reasoning — Medication Prescribe",
            Description: "Evaluates the prescription about to be issued against the patient's chart for drug-allergy, drug-drug, and renal-dose concerns.",
            Prefetch: StandardPrefetch),
    };

    [HttpGet]
    public CdsServicesResponse Get() => new(Services: Services);
}
