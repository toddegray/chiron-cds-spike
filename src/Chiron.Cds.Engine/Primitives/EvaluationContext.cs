using System.Globalization;

namespace Chiron.Cds.Engine.Primitives;

/// <summary>
/// Per-evaluation accessor that rules read from. Wraps the Patient,
/// Medications, Labs, and Conditions for one engine call. Convenience
/// methods (<see cref="HasMedication"/>, <see cref="Lab"/>, etc.) keep
/// rule code declarative.
/// </summary>
public sealed class EvaluationContext
{
    private readonly Dictionary<string, Lab> _labsByName;
    private readonly Dictionary<string, Medication> _medsByName;
    private readonly HashSet<string> _conditionNames;
    private readonly Dictionary<string, Allergy> _allergiesBySubstance;
    private readonly Dictionary<string, Immunization> _latestImmunizationByVaccine;
    private readonly Dictionary<string, Procedure> _latestProcedureByKind;

    public EvaluationContext(
        Patient patient,
        IEnumerable<Medication> medications,
        IEnumerable<Lab> labs,
        IEnumerable<Condition> conditions,
        IEnumerable<Allergy>? allergies = null,
        IEnumerable<Immunization>? immunizations = null,
        IEnumerable<Procedure>? procedures = null)
    {
        ArgumentNullException.ThrowIfNull(patient);
        Patient = patient;
        Medications = medications?.ToArray() ?? Array.Empty<Medication>();
        Labs = labs?.ToArray() ?? Array.Empty<Lab>();
        Conditions = conditions?.ToArray() ?? Array.Empty<Condition>();
        Allergies = allergies?.ToArray() ?? Array.Empty<Allergy>();
        Immunizations = immunizations?.ToArray() ?? Array.Empty<Immunization>();
        Procedures = procedures?.ToArray() ?? Array.Empty<Procedure>();
        _labsByName = Labs.GroupBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(l => l.TakenAt ?? DateTimeOffset.MinValue).First(),
                StringComparer.OrdinalIgnoreCase);
        _medsByName = Medications.Where(m => m.Active)
            .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        _conditionNames = new HashSet<string>(
            Conditions.Where(c => c.Active).Select(c => c.Name),
            StringComparer.OrdinalIgnoreCase);
        _allergiesBySubstance = Allergies.Where(a => a.Active)
            .GroupBy(a => a.Substance, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        _latestImmunizationByVaccine = Immunizations
            .Where(i => string.Equals(i.Status, "completed", StringComparison.OrdinalIgnoreCase))
            .GroupBy(i => i.Vaccine, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(i => i.AdministeredAt).First(),
                StringComparer.OrdinalIgnoreCase);
        _latestProcedureByKind = Procedures
            .Where(p => string.Equals(p.Status, "completed", StringComparison.OrdinalIgnoreCase))
            .GroupBy(p => p.Kind, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.PerformedAt).First(),
                StringComparer.OrdinalIgnoreCase);
    }

    public Patient Patient { get; }
    public IReadOnlyList<Medication> Medications { get; }
    public IReadOnlyList<Lab> Labs { get; }
    public IReadOnlyList<Condition> Conditions { get; }
    public IReadOnlyList<Allergy> Allergies { get; }
    public IReadOnlyList<Immunization> Immunizations { get; }
    public IReadOnlyList<Procedure> Procedures { get; }

    public bool HasMedication(string name) => _medsByName.ContainsKey(name);
    public Medication? Medication(string name) => _medsByName.GetValueOrDefault(name);
    public bool HasLab(string name) => _labsByName.ContainsKey(name);
    public bool HasCondition(string name) => _conditionNames.Contains(name);
    public Allergy? Allergy(string substance) => _allergiesBySubstance.GetValueOrDefault(substance);

    /// <summary>Most recent completed dose for the given vaccine, or null if none on file.</summary>
    public Immunization? LatestImmunization(string vaccine) =>
        _latestImmunizationByVaccine.GetValueOrDefault(vaccine);

    /// <summary>Most recent completed procedure of the given kind, or null if none on file.</summary>
    public Procedure? LatestProcedure(string kind) =>
        _latestProcedureByKind.GetValueOrDefault(kind);

    /// <summary>
    /// Most-recent lab observation by name. Throws <see cref="MissingInputException"/>
    /// if the lab is not present; rules should call <see cref="HasLab"/> first
    /// or wrap calls in a guard, depending on whether absence is a no-fire or
    /// a not-applicable.
    /// </summary>
    public Lab Lab(string name) =>
        _labsByName.TryGetValue(name, out var lab) ? lab
            : throw new MissingInputException($"Required lab '{name}' is not present.");

    /// <summary>
    /// Builds an input Fact from a Lab observation. Used by rules that want
    /// to record a lab value as a <c>Because</c> parent of an alert.
    /// </summary>
    public Fact LabFact(string name)
    {
        var lab = Lab(name);
        return Fact.Input(name, lab.Value, lab.Unit, lab.TakenAt);
    }

    /// <summary>
    /// Patient demographic facts as input Facts. <c>age_years</c>, <c>sex</c>,
    /// <c>weight_kg</c> (if present).
    /// </summary>
    public Fact PatientFact(string name) => name switch
    {
        "age_years" => Fact.Input("age_years", (double)Patient.AgeYears, "years"),
        "sex" => Fact.Input("sex", Patient.Sex),
        "weight_kg" => Patient.WeightKg is double w
            ? Fact.Input("weight_kg", w, "kg")
            : throw new MissingInputException("Patient weight_kg is not set."),
        _ => throw new MissingInputException($"Unknown patient fact '{name}'."),
    };
}

/// <summary>
/// Raised when a rule asks for an input the current evaluation does not have.
/// Engine catches this and treats the rule as no-fire rather than crashing
/// the whole evaluation.
/// </summary>
public sealed class MissingInputException : Exception
{
    public MissingInputException(string message) : base(message) { }
}
