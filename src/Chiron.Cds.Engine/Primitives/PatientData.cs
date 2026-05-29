namespace Chiron.Cds.Engine.Primitives;

/// <summary>
/// Engine-side patient. Decoupled from FHIR <c>Patient</c> so the engine
/// can be tested without the Firely SDK.
/// </summary>
public sealed record Patient(
    string Id,
    int AgeYears,
    string Sex,
    double? WeightKg = null,
    IReadOnlyDictionary<string, object>? Attributes = null);

/// <summary>Engine-side lab observation.</summary>
public sealed record Lab(
    string Name,
    double Value,
    string? Unit,
    DateTimeOffset? TakenAt = null);

/// <summary>Engine-side medication on a patient's active medication list.</summary>
public sealed record Medication(
    string Name,
    double? DoseMg = null,
    string? Frequency = null,
    string? Route = null,
    bool Active = true);

/// <summary>
/// Engine-side condition / problem-list entry. <c>RecordedDate</c> (when the
/// condition was charted) is distinct from <c>Onset</c> (when it began) and is
/// used to order the problem list when onset is absent.
/// </summary>
public sealed record Condition(
    string Name,
    DateTimeOffset? Onset = null,
    bool Active = true,
    DateTimeOffset? RecordedDate = null);

/// <summary>Engine-side allergy / intolerance entry.</summary>
/// <param name="Substance">Canonical substance name (e.g. "penicillin", "sulfa", "shellfish").</param>
/// <param name="Class">Optional class grouping ("antibiotic", "nsaid", "opioid", "food") so rules can match by class.</param>
/// <param name="Reaction">Optional reaction text from the chart (e.g. "anaphylaxis", "rash").</param>
/// <param name="Critical">True if the reaction is documented as life-threatening / high-criticality.</param>
/// <param name="Active">True if the allergy is currently active (not refuted or resolved).</param>
public sealed record Allergy(
    string Substance,
    string? Class = null,
    string? Reaction = null,
    bool Critical = false,
    bool Active = true);

/// <summary>Engine-side immunization record. One entry per administered dose.</summary>
/// <param name="Vaccine">Canonical vaccine name: "influenza", "tdap", "zoster_recombinant", "pneumococcal_pcv20", "covid19".</param>
/// <param name="AdministeredAt">When the dose was administered.</param>
/// <param name="Status">FHIR status string ("completed", "not-done", "entered-in-error"). Only "completed" doses count toward gap rules.</param>
public sealed record Immunization(
    string Vaccine,
    DateTimeOffset AdministeredAt,
    string Status = "completed");

/// <summary>Engine-side procedure record. One entry per performed procedure.</summary>
/// <param name="Kind">Canonical procedure name: "mammography", "colonoscopy", "sigmoidoscopy", "fit_screening", "fobt", "cervical_cytology", "dxa_scan".</param>
/// <param name="PerformedAt">When the procedure was performed.</param>
/// <param name="Status">FHIR status ("completed", "in-progress", etc.). Only "completed" satisfies surveillance interval rules.</param>
public sealed record Procedure(
    string Kind,
    DateTimeOffset PerformedAt,
    string Status = "completed");
