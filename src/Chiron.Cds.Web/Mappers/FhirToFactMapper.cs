using System.Globalization;

using Chiron.Cds.Engine.Primitives;
using Chiron.Cds.Engine.Rules.Interactions;
using Chiron.Cds.Web.FhirClient;
using Hl7.Fhir.Model;

using EnginePatient = Chiron.Cds.Engine.Primitives.Patient;
using EngineCondition = Chiron.Cds.Engine.Primitives.Condition;
using EngineMedication = Chiron.Cds.Engine.Primitives.Medication;
using EngineAllergy = Chiron.Cds.Engine.Primitives.Allergy;
using EngineImmunization = Chiron.Cds.Engine.Primitives.Immunization;
using EngineProcedure = Chiron.Cds.Engine.Primitives.Procedure;
using FhirPatient = Hl7.Fhir.Model.Patient;
using FhirCondition = Hl7.Fhir.Model.Condition;
using FhirAllergyIntolerance = Hl7.Fhir.Model.AllergyIntolerance;
using FhirImmunization = Hl7.Fhir.Model.Immunization;
using FhirProcedure = Hl7.Fhir.Model.Procedure;

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
        // Lipid panel for ASCVD risk
        ["2093-3"] = "total_cholesterol",
        ["2085-9"] = "hdl_cholesterol",
        ["13457-7"] = "ldl_cholesterol_calculated",
        // Vital signs used by risk scores
        ["8480-6"] = "systolic_bp",
        ["8462-4"] = "diastolic_bp",
        // Smoking status (HL7 SDC)
        ["72166-2"] = "tobacco_smoking_status",
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
        var immunizations = chart.Immunizations.SelectMany(ProjectImmunization).ToArray();
        var procedures = chart.Procedures.SelectMany(ProjectProcedure).ToArray();
        return new EngineInputs(patient, medications, labs, conditions, allergies, immunizations, procedures);
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
        var takenAt = ExtractEffective(obs.Effective);

        // Top-level value: typical lab pattern (creatinine, A1c, etc.).
        if (obs.Code?.Coding is { Count: > 0 })
        {
            foreach (var lab in ProjectLabFromCodingAndValue(obs.Code.Coding, obs.Value, takenAt))
                yield return lab;
        }

        // Panel-style observations carry the actual values in components
        // (Cerner stores Blood pressure this way: top-level "Blood pressure",
        // 8480-6 + 8462-4 in components).
        foreach (var component in obs.Component ?? Enumerable.Empty<Observation.ComponentComponent>())
        {
            if (component.Code?.Coding is not { Count: > 0 }) continue;
            foreach (var lab in ProjectLabFromCodingAndValue(component.Code.Coding, component.Value, takenAt))
                yield return lab;
        }
    }

    private IEnumerable<Lab> ProjectLabFromCodingAndValue(IEnumerable<Coding> codings, DataType? value, DateTimeOffset? takenAt)
    {
        foreach (var coding in codings)
        {
            if (coding.System != "http://loinc.org") continue;
            if (string.IsNullOrEmpty(coding.Code)) continue;
            if (!KnownLabsByLoinc.TryGetValue(coding.Code, out var engineName)) continue;
            if (value is not Quantity q || q.Value is null) continue;
            yield return new Lab(engineName, (double)q.Value, q.Unit, takenAt);
        }
    }

    private static DateTimeOffset? ExtractEffective(DataType? effective) => effective switch
    {
        FhirDateTime fdt when DateTimeOffset.TryParse(fdt.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto) => dto,
        Period { Start: not null } p when DateTimeOffset.TryParse(p.Start, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto) => dto,
        _ => null,
    };

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

    private IEnumerable<EngineImmunization> ProjectImmunization(FhirImmunization imm)
    {
        // FHIR Immunization.status → engine string. Unknown / unsupported
        // statuses drop the row rather than silently being counted as
        // "completed" — fabricating a completed dose would let a future
        // FHIR status (e.g. R5's "in-progress") satisfy a coverage rule
        // that should still fire.
        string? status = imm.Status switch
        {
            FhirImmunization.ImmunizationStatusCodes.Completed => "completed",
            FhirImmunization.ImmunizationStatusCodes.NotDone => "not-done",
            FhirImmunization.ImmunizationStatusCodes.EnteredInError => "entered-in-error",
            _ => null,
        };
        if (status is null)
        {
            _log.LogDebug("Skipping immunization {Id}: unsupported status {Status}.", imm.Id, imm.Status);
            yield break;
        }

        // Vaccine identity: prefer CVX coding, then displayed text.
        var vaccineText = imm.VaccineCode?.Coding
            ?.FirstOrDefault(c => c.System == "http://hl7.org/fhir/sid/cvx")?.Code
            ?? imm.VaccineCode?.Text
            ?? imm.VaccineCode?.Coding?.FirstOrDefault()?.Display;
        var vaccine = NormalizeVaccine(vaccineText);
        if (string.IsNullOrEmpty(vaccine)) yield break;

        var administeredAt = imm.Occurrence switch
        {
            FhirDateTime fdt when DateTimeOffset.TryParse(fdt.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto) => dto,
            Period { Start: not null } p when DateTimeOffset.TryParse(p.Start, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto) => dto,
            _ => (DateTimeOffset?)null,
        };
        if (administeredAt is null)
        {
            _log.LogDebug("Skipping immunization {Id}: no occurrence date.", imm.Id);
            yield break;
        }

        yield return new EngineImmunization(vaccine, administeredAt.Value, status);
    }

    /// <summary>
    /// Map a FHIR vaccine identifier (CVX code or display text) to an
    /// engine canonical vaccine name. Covers the most common adult CVX
    /// codes for influenza, Tdap, zoster, pneumococcal, and COVID-19;
    /// unknown codes return empty (the immunization is dropped, never
    /// silently misclassified).
    /// </summary>
    internal static string NormalizeVaccine(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return string.Empty;
        var trimmed = code.Trim().ToLowerInvariant();

        // Try CVX numeric code first.
        if (CvxToCanonical.TryGetValue(trimmed, out var byCvx)) return byCvx;

        // Then text contains-match for display strings.
        if (trimmed.Contains("influenza") || trimmed.Contains("flu shot") || trimmed.Contains("flumist")) return "influenza";
        if (trimmed.Contains("tdap") || trimmed.Contains("dtap")) return "tdap";
        if (trimmed.Contains("zoster") || trimmed.Contains("shingrix")) return "zoster_recombinant";
        if (trimmed.Contains("pneumococcal") || trimmed.Contains("pcv20") || trimmed.Contains("pcv15") || trimmed.Contains("prevnar")) return "pneumococcal_pcv20";
        if (trimmed.Contains("covid") || trimmed.Contains("sars-cov-2")) return "covid19";

        return string.Empty;
    }

    private IEnumerable<EngineProcedure> ProjectProcedure(FhirProcedure proc)
    {
        // Only completed procedures satisfy surveillance intervals.
        // Other statuses (in-progress, not-done, entered-in-error,
        // unknown) are preserved with their status string so the
        // override log can audit them; the engine's LatestProcedure
        // filter excludes anything but "completed".
        string status = proc.Status switch
        {
            EventStatus.Completed => "completed",
            EventStatus.InProgress => "in-progress",
            EventStatus.NotDone => "not-done",
            EventStatus.EnteredInError => "entered-in-error",
            EventStatus.Stopped => "stopped",
            EventStatus.OnHold => "on-hold",
            EventStatus.Preparation => "preparation",
            _ => "unknown",
        };

        var codeText = proc.Code?.Text;
        var codings = proc.Code?.Coding ?? new List<Coding>();
        string? kind = null;
        foreach (var coding in codings)
        {
            kind = NormalizeProcedure(coding.System, coding.Code, coding.Display);
            if (!string.IsNullOrEmpty(kind)) break;
        }
        if (string.IsNullOrEmpty(kind))
            kind = NormalizeProcedure(system: null, code: null, displayText: codeText);
        if (string.IsNullOrEmpty(kind)) yield break;

        var performedAt = proc.Performed switch
        {
            FhirDateTime fdt when DateTimeOffset.TryParse(fdt.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto) => dto,
            Period { Start: not null } p when DateTimeOffset.TryParse(p.Start, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto) => dto,
            FhirString fs when DateTimeOffset.TryParse(fs.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto) => dto,
            _ => (DateTimeOffset?)null,
        };
        if (performedAt is null)
        {
            _log.LogDebug("Skipping procedure {Id}: no Performed date.", proc.Id);
            yield break;
        }

        yield return new EngineProcedure(kind, performedAt.Value, status);
    }

    /// <summary>
    /// Map a FHIR procedure (CPT/HCPCS/SNOMED code or display text) to the
    /// canonical procedure name the engine uses for surveillance rules.
    /// Unknown codes return empty (the procedure is dropped, never
    /// silently misclassified).
    /// </summary>
    internal static string NormalizeProcedure(string? system, string? code, string? displayText)
    {
        if (!string.IsNullOrEmpty(code))
        {
            var key = $"{system}|{code}";
            if (ProcedureCodeToCanonical.TryGetValue(key, out var byCode)) return byCode;
            // Some EHRs only put the code without a system; fall back to lookup by code alone.
            if (ProcedureCodeToCanonical.TryGetValue($"|{code}", out var byBareCode)) return byBareCode;
        }
        if (string.IsNullOrWhiteSpace(displayText)) return string.Empty;
        var lower = displayText.Trim().ToLowerInvariant();
        if (lower.Contains("mammogra")) return "mammography";
        if (lower.Contains("colonoscop")) return "colonoscopy";
        if (lower.Contains("sigmoidoscop")) return "sigmoidoscopy";
        if (lower.Contains("fit") || lower.Contains("fecal immuno")) return "fit_screening";
        if (lower.Contains("fobt") || lower.Contains("guaiac")) return "fobt";
        if (lower.Contains("pap smear") || lower.Contains("cervical cytology")) return "cervical_cytology";
        if (lower.Contains("dexa") || lower.Contains("dual-energy x-ray") || lower.Contains("bone density")) return "dxa_scan";
        return string.Empty;
    }

    /// <summary>
    /// (system|code) → canonical procedure name. CPT codes use no system
    /// prefix in some EHRs ("|45378") so the lookup tries both forms.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> ProcedureCodeToCanonical = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // Mammography (CPT)
        ["http://www.ama-assn.org/go/cpt|77067"] = "mammography",
        ["http://www.ama-assn.org/go/cpt|77066"] = "mammography",
        ["http://www.ama-assn.org/go/cpt|77065"] = "mammography",
        ["|77067"] = "mammography",
        ["|77066"] = "mammography",
        ["|77065"] = "mammography",
        // Mammography (SNOMED)
        ["http://snomed.info/sct|71651003"] = "mammography",
        // Colonoscopy (CPT)
        ["http://www.ama-assn.org/go/cpt|45378"] = "colonoscopy",
        ["http://www.ama-assn.org/go/cpt|45380"] = "colonoscopy",
        ["http://www.ama-assn.org/go/cpt|45385"] = "colonoscopy",
        ["|45378"] = "colonoscopy",
        ["|45380"] = "colonoscopy",
        ["|45385"] = "colonoscopy",
        // Colonoscopy (SNOMED)
        ["http://snomed.info/sct|73761001"] = "colonoscopy",
        // Sigmoidoscopy (CPT)
        ["http://www.ama-assn.org/go/cpt|45330"] = "sigmoidoscopy",
        ["|45330"] = "sigmoidoscopy",
        // FIT (CPT 82270)
        ["http://www.ama-assn.org/go/cpt|82270"] = "fit_screening",
        ["|82270"] = "fit_screening",
        ["http://www.ama-assn.org/go/cpt|82274"] = "fit_screening",
        ["|82274"] = "fit_screening",
        // Pap smear / cervical cytology (CPT 88141-88175)
        ["http://www.ama-assn.org/go/cpt|88142"] = "cervical_cytology",
        ["|88142"] = "cervical_cytology",
        ["http://www.ama-assn.org/go/cpt|88175"] = "cervical_cytology",
        ["|88175"] = "cervical_cytology",
        // DXA bone density (CPT 77080)
        ["http://www.ama-assn.org/go/cpt|77080"] = "dxa_scan",
        ["|77080"] = "dxa_scan",
    };

    /// <summary>
    /// CVX → canonical vaccine name. CVX is the CDC's standard vaccine
    /// coding system; full list at https://www2a.cdc.gov/vaccines/iis/iisstandards/vaccines.asp
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> CvxToCanonical = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // Influenza
        ["88"] = "influenza", ["140"] = "influenza", ["141"] = "influenza",
        ["150"] = "influenza", ["155"] = "influenza", ["158"] = "influenza",
        ["161"] = "influenza", ["166"] = "influenza", ["171"] = "influenza",
        ["185"] = "influenza", ["186"] = "influenza", ["197"] = "influenza",
        // Tdap
        ["115"] = "tdap", ["139"] = "tdap",
        // Zoster (recombinant Shingrix; live ZVL is 121)
        ["187"] = "zoster_recombinant",
        // Pneumococcal
        ["133"] = "pneumococcal_pcv20", // PCV13
        ["152"] = "pneumococcal_pcv20", // PCV15 surrogate
        ["215"] = "pneumococcal_pcv20", // PCV20
        ["33"] = "pneumococcal_pcv20",  // PPSV23
        // COVID-19
        ["207"] = "covid19", ["208"] = "covid19", ["210"] = "covid19",
        ["211"] = "covid19", ["212"] = "covid19", ["218"] = "covid19",
        ["219"] = "covid19", ["228"] = "covid19", ["229"] = "covid19",
        ["300"] = "covid19", ["301"] = "covid19", ["302"] = "covid19",
    };

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

    internal static string NormalizeMedicationName(string? display)
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
    IReadOnlyList<EngineAllergy> Allergies,
    IReadOnlyList<EngineImmunization> Immunizations,
    IReadOnlyList<EngineProcedure> Procedures);
