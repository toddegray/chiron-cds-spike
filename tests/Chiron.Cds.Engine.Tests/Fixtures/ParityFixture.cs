using System.Text.Json;
using System.Text.Json.Serialization;

using Chiron.Cds.Engine.Primitives;

namespace Chiron.Cds.Engine.Tests.Fixtures;

internal sealed record ParityFixture(
    string Name,
    string? Description,
    PatientDto Patient,
    IReadOnlyList<MedicationDto> Medications,
    IReadOnlyList<LabDto> Labs,
    IReadOnlyList<ConditionDto> Conditions,
    ExpectedAlertDto? ExpectAlert)
{
    public Primitives.Patient ToEnginePatient() =>
        new(Patient.Id, Patient.AgeYears, Patient.Sex, Patient.WeightKg);

    public IEnumerable<Primitives.Medication> ToEngineMedications() =>
        Medications.Select(m => new Primitives.Medication(m.Name, Active: m.Active));

    public IEnumerable<Primitives.Lab> ToEngineLabs() =>
        Labs.Select(l => new Primitives.Lab(l.Name, l.Value, l.Unit));

    public IEnumerable<Primitives.Condition> ToEngineConditions() =>
        Conditions.Select(c => new Primitives.Condition(c.Name, Active: c.Active));
}

internal sealed record PatientDto(
    string Id,
    [property: JsonPropertyName("age_years")] int AgeYears,
    string Sex,
    [property: JsonPropertyName("weight_kg")] double? WeightKg = null);

internal sealed record MedicationDto(string Name, bool Active = true);

internal sealed record LabDto(string Name, double Value, string? Unit);

internal sealed record ConditionDto(string Name, bool Active = true);

internal sealed record ExpectedAlertDto(
    [property: JsonPropertyName("rule_id")] string RuleId,
    string Severity,
    [property: JsonPropertyName("message_contains")] string MessageContains,
    string? Fingerprint);

internal static class FixtureLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public static ParityFixture Load(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<ParityFixture>(json, Options)
            ?? throw new InvalidOperationException($"Failed to deserialize {fileName}");
    }

    public static IEnumerable<string> AllFixtureFiles()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        return Directory.EnumerateFiles(dir, "*.json").Select(Path.GetFileName).Cast<string>().OrderBy(s => s);
    }
}
