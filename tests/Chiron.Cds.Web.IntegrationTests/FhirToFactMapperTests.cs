using Chiron.Cds.Web.FhirClient;
using Chiron.Cds.Web.Mappers;
using FluentAssertions;
using Hl7.Fhir.Model;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chiron.Cds.Web.IntegrationTests;

/// <summary>
/// Unit tests for <see cref="FhirToFactMapper"/>. Exercises the
/// allergy-projection branch matrix that the real-Cerner integration
/// test can only hit when the network is available — these run offline.
/// </summary>
public class FhirToFactMapperTests
{
    private readonly FhirToFactMapper _mapper = new(NullLogger<FhirToFactMapper>.Instance);

    private static Patient BuildPatient() => new()
    {
        Id = "p1",
        Gender = AdministrativeGender.Female,
        BirthDate = "1990-01-01",
    };

    private static AllergyIntolerance BuildAllergy(
        string? text = "Sulfa drugs",
        string? clinicalStatus = "active",
        string? verificationStatus = "confirmed",
        AllergyIntolerance.AllergyIntoleranceCriticality? criticality = null,
        string? reactionText = null) => new()
    {
        Code = text is null ? null : new CodeableConcept { Text = text },
        ClinicalStatus = clinicalStatus is null ? null : new CodeableConcept
        {
            Coding = new List<Coding> { new("http://terminology.hl7.org/CodeSystem/allergyintolerance-clinical", clinicalStatus) },
        },
        VerificationStatus = verificationStatus is null ? null : new CodeableConcept
        {
            Coding = new List<Coding> { new("http://terminology.hl7.org/CodeSystem/allergyintolerance-verification", verificationStatus) },
        },
        Criticality = criticality,
        Reaction = reactionText is null
            ? new List<AllergyIntolerance.ReactionComponent>()
            : new List<AllergyIntolerance.ReactionComponent>
            {
                new()
                {
                    Manifestation = new List<CodeableConcept> { new() { Text = reactionText } },
                },
            },
    };

    private static PatientChart BuildChart(params AllergyIntolerance[] allergies) => new(
        Patient: BuildPatient(),
        Conditions: Array.Empty<Condition>(),
        Observations: Array.Empty<Observation>(),
        MedicationRequests: Array.Empty<MedicationRequest>(),
        Allergies: allergies,
        Encounter: null);

    [Fact]
    public void Allergy_With_Active_Status_Is_Projected_Active()
    {
        var inputs = _mapper.Project(BuildChart(BuildAllergy(text: "Ibuprofen")));
        inputs.Allergies.Should().ContainSingle().Which.Active.Should().BeTrue();
        inputs.Allergies[0].Substance.Should().Be("ibuprofen");
    }

    [Fact]
    public void Allergy_With_Inactive_Clinical_Status_Becomes_Inactive()
    {
        var inputs = _mapper.Project(BuildChart(
            BuildAllergy(text: "Penicillin", clinicalStatus: "inactive")));
        inputs.Allergies.Should().ContainSingle().Which.Active.Should().BeFalse();
    }

    [Fact]
    public void Refuted_Allergy_Is_Dropped()
    {
        var inputs = _mapper.Project(BuildChart(
            BuildAllergy(text: "Codeine", verificationStatus: "refuted")));
        inputs.Allergies.Should().BeEmpty();
    }

    [Fact]
    public void Entered_In_Error_Allergy_Is_Dropped()
    {
        var inputs = _mapper.Project(BuildChart(
            BuildAllergy(text: "Aspirin", verificationStatus: "entered-in-error")));
        inputs.Allergies.Should().BeEmpty();
    }

    [Fact]
    public void High_Criticality_Becomes_Critical()
    {
        var inputs = _mapper.Project(BuildChart(BuildAllergy(
            text: "Penicillin",
            criticality: AllergyIntolerance.AllergyIntoleranceCriticality.High)));
        inputs.Allergies.Should().ContainSingle().Which.Critical.Should().BeTrue();
    }

    [Fact]
    public void Low_Criticality_Is_Not_Critical()
    {
        var inputs = _mapper.Project(BuildChart(BuildAllergy(
            text: "Bee stings",
            criticality: AllergyIntolerance.AllergyIntoleranceCriticality.Low)));
        inputs.Allergies.Should().ContainSingle().Which.Critical.Should().BeFalse();
    }

    [Fact]
    public void Reaction_Text_Is_Captured()
    {
        var inputs = _mapper.Project(BuildChart(BuildAllergy(
            text: "Penicillin",
            reactionText: "Anaphylaxis")));
        inputs.Allergies.Should().ContainSingle().Which.Reaction.Should().Be("Anaphylaxis");
    }

    [Fact]
    public void Falls_Back_To_Coding_Display_When_No_Text()
    {
        var allergy = new AllergyIntolerance
        {
            Code = new CodeableConcept
            {
                Coding = new List<Coding> { new("http://snomed.info/sct", "294505008", "Sulfonamide allergy") },
            },
            ClinicalStatus = new CodeableConcept
            {
                Coding = new List<Coding> { new("", "active") },
            },
        };
        var inputs = _mapper.Project(BuildChart(allergy));
        inputs.Allergies.Should().ContainSingle().Which.Substance.Should().Be("sulfonamide");
    }

    [Fact]
    public void Allergy_With_Null_Code_Is_Dropped()
    {
        var inputs = _mapper.Project(BuildChart(BuildAllergy(text: null)));
        inputs.Allergies.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Penicillin G Sodium", "penicillin")]
    [InlineData("amoxicillin/clavulanate", "amoxicillin")]
    [InlineData("  IBUPROFEN  ", "ibuprofen")]
    [InlineData("Sulfa drugs", "sulfa")]
    [InlineData("Bactrim DS", "bactrim")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void NormalizeSubstance_Reduces_To_Canonical_Identifier(string input, string expected)
    {
        FhirToFactMapper.NormalizeSubstance(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("Penicillin]<script>alert(1)</script>", "penicillin")]
    [InlineData("**bold** text", "bold")]
    [InlineData("[link](javascript:evil)", "link")]
    [InlineData("***", "")]
    public void NormalizeSubstance_Strips_Markdown_And_Html_Injection(string input, string expected)
    {
        FhirToFactMapper.NormalizeSubstance(input).Should().Be(expected,
            because: "the substance must not carry markdown/HTML special chars into card rendering");
    }

    [Theory]
    [InlineData("penicillin", "penicillin")]
    [InlineData("amoxicillin", "penicillin")]
    [InlineData("cephalexin", "cephalosporin")]
    [InlineData("ibuprofen", "nsaid")]
    [InlineData("atorvastatin", "statin")]
    [InlineData("lisinopril", "ace_inhibitor")]
    [InlineData("metformin", null)]
    [InlineData("unknown_drug", null)]
    public void ClassifyAllergen_Returns_Expected_Class(string substance, string? expected)
    {
        FhirToFactMapper.ClassifyAllergen(substance).Should().Be(expected);
    }
}
