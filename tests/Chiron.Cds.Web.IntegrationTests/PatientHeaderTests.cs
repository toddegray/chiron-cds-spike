using Chiron.Cds.Engine.Primitives;
using Chiron.Cds.Web.Mappers;
using Chiron.Cds.Web.SmartLaunch;
using FluentAssertions;

namespace Chiron.Cds.Web.IntegrationTests;

/// <summary>
/// Direct unit tests for <see cref="PatientHeader.From"/> and the
/// `SexLabel` / `Humanize` helpers. These previously lived as
/// duplicated private code on DemoController + AppController; lifting
/// them into the record consolidated the projection logic and is the
/// only place the branches are exercised.
/// </summary>
public class PatientHeaderTests
{
    private static EngineInputs Inputs(
        int age = 60,
        string sex = "F",
        IEnumerable<Condition>? conditions = null,
        IEnumerable<Medication>? medications = null,
        IEnumerable<Allergy>? allergies = null,
        IEnumerable<Immunization>? immunizations = null,
        IEnumerable<Procedure>? procedures = null) => new(
        Patient: new Patient("p", age, sex),
        Medications: (medications ?? Array.Empty<Medication>()).ToArray(),
        Labs: Array.Empty<Lab>(),
        Conditions: (conditions ?? Array.Empty<Condition>()).ToArray(),
        Allergies: (allergies ?? Array.Empty<Allergy>()).ToArray(),
        Immunizations: (immunizations ?? Array.Empty<Immunization>()).ToArray(),
        Procedures: (procedures ?? Array.Empty<Procedure>()).ToArray());

    [Theory]
    [InlineData("F", "Female")]
    [InlineData("f", "Female")]
    [InlineData("M", "Male")]
    [InlineData("m", "Male")]
    [InlineData("U", "Other")]
    [InlineData("X", "Other")]
    [InlineData("", "Other")]
    public void SexLabel_Maps_Engine_Codes_To_Display(string code, string expected)
    {
        PatientHeader.SexLabel(code).Should().Be(expected);
    }

    [Theory]
    [InlineData("diabetes", "Diabetes")]
    [InlineData("type_2_diabetes_mellitus", "Type 2 diabetes mellitus")]
    [InlineData("heart_failure", "Heart failure")]
    [InlineData("", "")]
    [InlineData("  ", "")]
    public void Humanize_Replaces_Underscores_And_Capitalizes_First_Letter(string canonical, string expected)
    {
        PatientHeader.Humanize(canonical).Should().Be(expected);
    }

    [Fact]
    public void From_Uses_Age_Years_And_Sex_When_Present()
    {
        var header = PatientHeader.From(Inputs(age: 78, sex: "M"), "Test Patient");
        header.DisplayName.Should().Be("Test Patient");
        header.AgeSex.Should().Contain("78");
        header.AgeSex.Should().Contain("Male");
    }

    [Fact]
    public void From_Falls_Back_To_Sex_Only_When_Age_Is_Zero()
    {
        var header = PatientHeader.From(Inputs(age: 0, sex: "F"), "Newborn");
        header.AgeSex.Should().Be("Female",
            because: "age=0 means birthDate was missing; we don't claim a fake number");
    }

    [Fact]
    public void From_Filters_Inactive_Conditions_And_Dedups()
    {
        var header = PatientHeader.From(Inputs(
            conditions: new[]
            {
                new Condition("hypertension"),
                new Condition("hypertension"),                // duplicate
                new Condition("diabetes", Active: false),     // inactive
                new Condition("heart_failure"),
            }), "P");
        header.ActiveConditions.Should().HaveCount(2);
        header.ActiveConditions.Should().Contain(new[] { "Hypertension", "Heart failure" });
        header.ActiveConditions.Should().NotContain("Diabetes");
    }

    [Fact]
    public void From_Filters_Inactive_Allergies_And_Dedups_And_Humanizes()
    {
        var header = PatientHeader.From(Inputs(
            allergies: new[]
            {
                new Allergy("sulfa"),
                new Allergy("sulfa"),                          // duplicate
                new Allergy("penicillin", Active: false),      // inactive
                new Allergy("cashew_nuts"),
            }), "P");
        header.ActiveAllergies.Should().HaveCount(2);
        header.ActiveAllergies.Should().Contain(new[] { "Sulfa", "Cashew nuts" });
    }

    [Fact]
    public void From_Counts_Only_Active_Medications()
    {
        var header = PatientHeader.From(Inputs(
            medications: new[]
            {
                new Medication("metformin"),
                new Medication("aspirin"),
                new Medication("warfarin", Active: false),
            }), "P");
        header.ActiveMedicationCount.Should().Be(2);
    }

    [Fact]
    public void From_Counts_Only_Completed_Immunizations()
    {
        var header = PatientHeader.From(Inputs(
            immunizations: new[]
            {
                new Immunization("influenza", DateTimeOffset.UtcNow.AddMonths(-3), "completed"),
                new Immunization("covid19", DateTimeOffset.UtcNow.AddMonths(-12), "completed"),
                new Immunization("tdap", DateTimeOffset.UtcNow.AddMonths(-24), "not-done"),
            }), "P");
        header.CompletedImmunizationCount.Should().Be(2);
    }

    [Fact]
    public void From_Counts_Only_Completed_Procedures()
    {
        var header = PatientHeader.From(Inputs(
            procedures: new[]
            {
                new Procedure("mammography", DateTimeOffset.UtcNow.AddMonths(-6), "completed"),
                new Procedure("colonoscopy", DateTimeOffset.UtcNow.AddYears(-5), "completed"),
                new Procedure("sigmoidoscopy", DateTimeOffset.UtcNow.AddYears(-3), "stopped"),
            }), "P");
        header.CompletedProcedureCount.Should().Be(2);
    }

    [Fact]
    public void From_Rejects_Null_Inputs()
    {
        var act = () => PatientHeader.From(inputs: null!, displayName: "x");
        act.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData(78, "M", "78y · Male")]
    [InlineData(35, "F", "35y · Female")]
    [InlineData(7, "U", "7y · Other")]
    [InlineData(0, "F", "Female")]
    [InlineData(0, "", "Other")]
    [InlineData(-1, "M", "Male")]
    public void FormatAgeSex_Composes_Age_With_Sex_Label(int age, string sex, string expected)
    {
        // Lifted out of duplicated code in PanelService; this is the one
        // place the format is defined now.
        PatientHeader.FormatAgeSex(age, sex).Should().Be(expected);
    }

    [Fact]
    public void FormatAgeSex_Tolerates_Null_Sex()
    {
        PatientHeader.FormatAgeSex(40, sex: null).Should().Be("40y · Other");
    }
}
