using Hl7.Fhir.Model;

namespace Chiron.Cds.Web.FhirClient;

/// <summary>
/// The minimal patient chart Chiron needs to evaluate its rules. A flat
/// record of FHIR resources, decoupled from any specific FHIR client.
/// </summary>
public sealed record PatientChart(
    Patient Patient,
    IReadOnlyList<Condition> Conditions,
    IReadOnlyList<Observation> Observations,
    IReadOnlyList<MedicationRequest> MedicationRequests,
    Encounter? Encounter);
