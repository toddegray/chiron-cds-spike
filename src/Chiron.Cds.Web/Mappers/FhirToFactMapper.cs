using System.Globalization;

using Chiron.Cds.Engine.Primitives;
using Chiron.Cds.Engine.Rules.Interactions;
using Chiron.Cds.Web.FhirClient;
using Hl7.Fhir.Model;

using EnginePatient = Chiron.Cds.Engine.Primitives.Patient;
using EngineCondition = Chiron.Cds.Engine.Primitives.Condition;
using EngineMedication = Chiron.Cds.Engine.Primitives.Medication;
using EngineAllergy = Chiron.Cds.Engine.Primitives.Allergy;
using FhirPatient = Hl7.Fhir.Model.Patient;
using FhirCondition = Hl7.Fhir.Model.Condition;
using FhirAllergyIntolerance = Hl7.Fhir.Model.AllergyIntolerance;

namespace Chiron.Cds.Web.Mappers;

/// <summary>
/// Projects FHIR resources into engine-side primitives. Conservative: any
/// resource missing a required field is logged and skipped rather than
/// crashing the evaluation. Numeric values use invariant-culture parsing
/// to match the Python/TS engines.
/// </summary>
public sealed class FhirToFactMapper
{
    private static readonly Dictionary<string, string> KnownLabsByLoinc = new(StringComparer.Ordinal)
    {
        ["2160-0"] = "creatinine",
        ["33914-3"] = "egfr",
        ["4548-4"] = "hemoglobin_a1c",
        ["6301-6"] = "inr",
    };

    private readonly ILogger<FhirToFactMapper> _log;

    public FhirToFactMapper(ILogger<FhirToFactMapper> log)
    {
        _log = log;
    }

    public EngineInputs Project(PatientChart chart)
    {
        ArgumentNullException.ThrowIfNull(chart);
        var patient = ProjectPatient(chart.Patient);
        var labs = chart.Observations.SelectMany(ProjectLab).ToArray();
        var conditions = chart.Conditions.SelectMany(ProjectCondition).ToArray();
        var medications = chart.MedicationRequests.SelectMany(ProjectMedication).ToArray();
        var allergies = chart.Allergies.SelectMany(ProjectAllergy).ToArray();
        return new EngineInputs(patient, medications, labs, conditions, allergies);
    }

    private EnginePatient ProjectPatient(FhirPatient patient)
    {
        if (patient is null) throw new ArgumentNullException(nameof(patient));
        var ageYears = ComputeAgeYears(patient.BirthDate);
        var sex = patient.Gender switch
        {
            AdministrativeGender.Female => "F",
            AdministrativeGender.Male => "M",
            _ => "U",
        };
        return new EnginePatient(patient.Id ?? "<unknown>", ageYears, sex);
    }

    private IEnumerable<Lab> ProjectLab(Observation obs)
    {
        if (obs.Code?.Coding is null) yield break;
        foreach (var coding in obs.Code.Coding)
        {
            if (coding.System != "http://loinc.org") continue;
            if (string.IsNullOrEmpty(coding.Code)) continue;
            if (!KnownLabsByLoinc.TryGetValue(coding.Code, out var engineName)) continue;
            if (obs.Value is not Quantity q || q.Value is null)
            {
                _log.LogDebug("Skipping observation {Id}: no Quantity value.", obs.Id);
                yield break;
            }
            DateTimeOffset? takenAt = obs.Effective switch
            {
                FhirDateTime fdt when DateTimeOffset.TryParse(fdt.Value, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var dto) => dto,
                Period { Start: not null } p when DateTimeOffset.TryParse(p.Start, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var dto) => dto,
                _ => null,
            };
            yield return new Lab(engineName, (double)q.Value, q.Unit, takenAt);
        }
    }

    private IEnumerable<EngineCondition> ProjectCondition(FhirCondition condition)
    {
        if (condition.Code?.Coding is null) yield break;
        var active = condition.ClinicalStatus?.Coding?.Any(c => c.Code == "active") ?? true;
        var onset = condition.Onset is FhirDateTime fdt
            && DateTimeOffset.TryParse(fdt.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto)
            ? dto : (DateTimeOffset?)null;

        foreach (var coding in condition.Code.Coding)
        {
            var name = NormalizeConditionName(coding.Display ?? coding.Code);
            if (string.IsNullOrEmpty(name)) continue;
            yield return new EngineCondition(name, onset, active);
        }
    }

    private IEnumerable<EngineMedication> ProjectMedication(MedicationRequest req)
    {
        var active = req.Status == MedicationRequest.MedicationrequestStatus.Active;
        var nameFromConcept = (req.Medication as CodeableConcept)?.Coding?.FirstOrDefault()?.Display
            ?? (req.Medication as CodeableConcept)?.Text;
        var name = NormalizeMedicationName(nameFromConcept);
        if (string.IsNullOrEmpty(name)) yield break;
        yield return new EngineMedication(name, Active: active);
    }

    private IEnumerable<EngineAllergy> ProjectAllergy(FhirAllergyIntolerance allergy)
    {
        // Skip non-allergy entries (refuted, entered-in-error, inactive).
        var clinicalStatus = allergy.ClinicalStatus?.Coding?.FirstOrDefault()?.Code;
        var active = string.IsNullOrEmpty(clinicalStatus)
            ? true
            : string.Equals(clinicalStatus, "active", StringComparison.OrdinalIgnoreCase);
        var verificationStatus = allergy.VerificationStatus?.Coding?.FirstOrDefault()?.Code;
        if (string.Equals(verificationStatus, "refuted", StringComparison.OrdinalIgnoreCase)
            || string.Equals(verificationStatus, "entered-in-error", StringComparison.OrdinalIgnoreCase))
        {
            yield break;
        }

        var substanceText = allergy.Code?.Text
            ?? allergy.Code?.Coding?.FirstOrDefault()?.Display
            ?? allergy.Code?.Coding?.FirstOrDefault()?.Code;
        var substance = NormalizeSubstance(substanceText);
        if (string.IsNullOrEmpty(substance)) yield break;

        var critical = allergy.Criticality == FhirAllergyIntolerance.AllergyIntoleranceCriticality.High;
        var reactionText = allergy.Reaction
            .SelectMany(r => r.Manifestation ?? Enumerable.Empty<CodeableConcept>())
            .Select(m => m.Text ?? m.Coding?.FirstOrDefault()?.Display)
            .FirstOrDefault(s => !string.IsNullOrEmpty(s));

        yield return new EngineAllergy(
            Substance: substance,
            Class: ClassifyAllergen(substance),
            Reaction: reactionText,
            Critical: critical,
            Active: active);
    }

    /// <summary>
    /// Reduce a FHIR-controlled substance display string to a canonical
    /// lowercase identifier limited to <c>[a-z0-9_-]</c>. A hostile or
    /// malformed <c>Code.text</c> (e.g. one containing markdown emphasis or
    /// link syntax) cannot survive normalization, so it cannot reach the
    /// alert rendering pipeline as a special character. Empty results are
    /// dropped upstream.
    /// </summary>
    internal static string NormalizeSubstance(string? display)
    {
        if (string.IsNullOrWhiteSpace(display)) return string.Empty;
        var trimmed = display.Trim().ToLowerInvariant();
        var sb = new System.Text.StringBuilder(trimmed.Length);
        foreach (var ch in trimmed)
        {
            var isIdentifierChar = ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-';
            if (isIdentifierChar)
            {
                sb.Append(ch);
            }
            else if (sb.Length > 0)
            {
                // First non-identifier character after we've collected the
                // initial token ends the substance. "Penicillin G Sodium"
                // → "penicillin"; "amoxicillin/clavulanate" → "amoxicillin".
                break;
            }
            // Leading non-identifier characters are skipped silently.
        }
        return sb.ToString();
    }

    /// <summary>
    /// Drug class (e.g. "penicillin", "nsaid") for a canonical substance
    /// name. Single source of truth lives in
    /// <see cref="DrugAllergyRule.KnownDrugClasses"/>; this method exists
    /// so the mapper code reads coherently next to <c>NormalizeSubstance</c>.
    /// </summary>
    internal static string? ClassifyAllergen(string substance) =>
        DrugAllergyRule.KnownDrugClasses.GetValueOrDefault(substance);

    private static int ComputeAgeYears(string? birthDate)
    {
        if (string.IsNullOrEmpty(birthDate)) return 0;
        if (!DateOnly.TryParse(birthDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dob)) return 0;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var age = today.Year - dob.Year;
        if (dob > today.AddYears(-age)) age--;
        return Math.Max(age, 0);
    }

    private static string NormalizeConditionName(string? display)
    {
        if (string.IsNullOrWhiteSpace(display)) return string.Empty;
        var lower = display.Trim().ToLowerInvariant();
        // Map a few common Cerner sandbox display strings to the engine's
        // canonical condition names. Production would do this via SNOMED →
        // canonical mapping, not string matching.
        if (lower.Contains("heart failure")) return "heart_failure";
        if (lower.Contains("hypertension")) return "hypertension";
        if (lower.Contains("diabetes")) return "diabetes";
        if (lower.Contains("stroke")) return "stroke";
        if (lower.Contains("transient ischemic")) return "tia";
        if (lower.Contains("myocardial infarction")) return "myocardial_infarction";
        if (lower.Contains("peripheral artery")) return "peripheral_artery_disease";
        return lower.Replace(' ', '_').Replace(',', '_');
    }

    private static string NormalizeMedicationName(string? display)
    {
        if (string.IsNullOrWhiteSpace(display)) return string.Empty;
        var lower = display.Trim().ToLowerInvariant();
        // Strip dosage forms and concentrations so "Metformin 500 MG Oral Tablet"
        // collapses to "metformin".
        var firstWord = lower.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return firstWord ?? string.Empty;
    }
}

/// <summary>Engine inputs assembled from a <see cref="PatientChart"/>.</summary>
public sealed record EngineInputs(
    EnginePatient Patient,
    IReadOnlyList<EngineMedication> Medications,
    IReadOnlyList<Lab> Labs,
    IReadOnlyList<EngineCondition> Conditions,
    IReadOnlyList<EngineAllergy> Allergies);
