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

/// <summary>Engine-side condition / problem-list entry.</summary>
public sealed record Condition(
    string Name,
    DateTimeOffset? Onset = null,
    bool Active = true);
