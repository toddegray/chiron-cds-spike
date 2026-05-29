using Hl7.Fhir.Model;

namespace Chiron.Cds.Web.FhirClient;

/// <summary>
/// Extracts the human-facing medical record number from a FHIR Patient. The
/// MRN is not the FHIR resource id — it is an org-assigned identifier. EHRs
/// expose it inconsistently: Epic publishes it under an org-specific OID with
/// no standard type coding, so the system is configured per tenant
/// (<c>TenantConfig.MrnSystem</c>); other EHRs tag it with the HL7 v2-0203
/// "MR" type, which is the fallback.
/// </summary>
public static class PatientMrn
{
    public static string? Extract(Patient? patient, string? mrnSystem)
    {
        if (patient?.Identifier is null) return null;

        if (!string.IsNullOrEmpty(mrnSystem))
        {
            var bySystem = patient.Identifier
                .FirstOrDefault(i => string.Equals(i.System, mrnSystem, StringComparison.OrdinalIgnoreCase))?.Value;
            if (!string.IsNullOrWhiteSpace(bySystem)) return bySystem.Trim();
        }

        var byType = patient.Identifier.FirstOrDefault(i => IsMedicalRecordNumber(i.Type))?.Value;
        return string.IsNullOrWhiteSpace(byType) ? null : byType.Trim();
    }

    private static bool IsMedicalRecordNumber(CodeableConcept? type)
    {
        if (type is null) return false;
        if (string.Equals(type.Text, "MRN", StringComparison.OrdinalIgnoreCase)) return true;
        return type.Coding?.Any(c => string.Equals(c.Code, "MR", StringComparison.OrdinalIgnoreCase)) == true;
    }
}
