using Chiron.Cds.Web.Mappers;

namespace Chiron.Cds.Web.SmartLaunch;

/// <summary>
/// Display-time summary of the patient that the Visit Brief renders in the
/// left rail. Derived from the engine inputs at evaluation time; not
/// stored. All fields are pre-formatted for rendering — no further string
/// interpolation should happen at the view layer.
/// </summary>
internal sealed record PatientHeader(
    string DisplayName,
    string AgeSex,
    IReadOnlyList<string> ActiveConditions,
    IReadOnlyList<string> ActiveAllergies,
    int ActiveMedicationCount,
    int CompletedImmunizationCount,
    int CompletedProcedureCount)
{
    /// <summary>Project an <see cref="EngineInputs"/> + display name into a header for the view.</summary>
    public static PatientHeader From(EngineInputs inputs, string displayName)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var sexLabel = SexLabel(inputs.Patient.Sex);
        var ageSex = inputs.Patient.AgeYears > 0
            ? $"{inputs.Patient.AgeYears}y · {sexLabel}"
            : sexLabel;
        return new PatientHeader(
            DisplayName: displayName,
            AgeSex: ageSex,
            ActiveConditions: inputs.Conditions
                .Where(c => c.Active)
                .Select(c => Humanize(c.Name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            ActiveAllergies: inputs.Allergies
                .Where(a => a.Active)
                .Select(a => Humanize(a.Substance))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            ActiveMedicationCount: inputs.Medications.Count(m => m.Active),
            CompletedImmunizationCount: inputs.Immunizations
                .Count(i => string.Equals(i.Status, "completed", StringComparison.OrdinalIgnoreCase)),
            CompletedProcedureCount: inputs.Procedures
                .Count(p => string.Equals(p.Status, "completed", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Map an engine sex code (F / M / U / other) to a display label.</summary>
    internal static string SexLabel(string sex) =>
        string.IsNullOrEmpty(sex)
            ? "Other"
            : sex.ToUpperInvariant() switch
            {
                "F" => "Female",
                "M" => "Male",
                _ => "Other",
            };

    /// <summary>Snake-case canonical name → display string ("type_2_diabetes_mellitus" → "Type 2 diabetes mellitus").</summary>
    internal static string Humanize(string canonical)
    {
        if (string.IsNullOrEmpty(canonical)) return canonical;
        var trimmed = canonical.Replace('_', ' ').Trim();
        if (trimmed.Length == 0) return trimmed;
        return char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
    }
}
