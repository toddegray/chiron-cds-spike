using Chiron.Cds.Engine.Primitives;
using Chiron.Cds.Engine.Rules.Interactions;
using FluentAssertions;

namespace Chiron.Cds.Engine.Tests;

public class DrugAllergyTests
{
    private static Engine BuildEngine() =>
        new Engine().RegisterRule(DrugAllergyRule.Rules.Single());

    private static Patient AdultPatient() => new("p", AgeYears: 45, Sex: "F");

    [Fact]
    public void Exact_Match_Fires_High()
    {
        var result = BuildEngine().Evaluate(
            AdultPatient(),
            new[] { new Medication("ibuprofen") },
            Array.Empty<Lab>(),
            Array.Empty<Condition>(),
            new[] { new Allergy("ibuprofen") });

        result.Alerts.Should().ContainSingle()
            .Which.Severity.Should().Be(Severity.High);
    }

    [Fact]
    public void Critical_Allergy_Escalates_To_Critical_Severity()
    {
        var result = BuildEngine().Evaluate(
            AdultPatient(),
            new[] { new Medication("penicillin") },
            Array.Empty<Lab>(),
            Array.Empty<Condition>(),
            new[] { new Allergy("penicillin", Critical: true) });

        var alert = result.Alerts.Should().ContainSingle().Subject;
        alert.Severity.Should().Be(Severity.Critical);
        var allergyFact = alert.Because.Single().Parents
            .Should().ContainSingle(p => p.Name == "allergy.penicillin").Subject;
        allergyFact.Value.Should().Be("critical",
            because: "the allergy fact records criticality so the override log preserves the severity reason");
    }

    [Fact]
    public void Allergy_Reaction_Is_Surfaced_In_Alert_Message()
    {
        var result = BuildEngine().Evaluate(
            AdultPatient(),
            new[] { new Medication("penicillin") },
            Array.Empty<Lab>(),
            Array.Empty<Condition>(),
            new[] { new Allergy("penicillin", Reaction: "anaphylaxis") });

        result.Alerts.Should().ContainSingle()
            .Which.Message.Should().Contain("anaphylaxis");
    }

    [Fact]
    public void Class_Cross_Reactivity_Fires()
    {
        // Patient is allergic to penicillin (class=penicillin) and is on amoxicillin
        // (also class=penicillin). Cross-reactive.
        var result = BuildEngine().Evaluate(
            AdultPatient(),
            new[] { new Medication("amoxicillin") },
            Array.Empty<Lab>(),
            Array.Empty<Condition>(),
            new[] { new Allergy("penicillin", Class: "penicillin") });

        var alert = result.Alerts.Should().ContainSingle().Subject;
        alert.RuleId.Should().Be("drug.allergy.collision");
        alert.Message.Should().Contain("cross-reactive");
    }

    [Fact]
    public void No_Match_No_Fire()
    {
        var result = BuildEngine().Evaluate(
            AdultPatient(),
            new[] { new Medication("metformin") },
            Array.Empty<Lab>(),
            Array.Empty<Condition>(),
            new[] { new Allergy("penicillin", Class: "penicillin") });

        result.Alerts.Should().BeEmpty();
    }

    [Fact]
    public void Inactive_Allergy_Is_Ignored()
    {
        var result = BuildEngine().Evaluate(
            AdultPatient(),
            new[] { new Medication("ibuprofen") },
            Array.Empty<Lab>(),
            Array.Empty<Condition>(),
            new[] { new Allergy("ibuprofen", Active: false) });

        result.Alerts.Should().BeEmpty();
    }

    [Fact]
    public void Inactive_Medication_Is_Ignored()
    {
        var result = BuildEngine().Evaluate(
            AdultPatient(),
            new[] { new Medication("ibuprofen", Active: false) },
            Array.Empty<Lab>(),
            Array.Empty<Condition>(),
            new[] { new Allergy("ibuprofen") });

        result.Alerts.Should().BeEmpty();
    }

    [Fact]
    public void Multiple_Collisions_All_Captured_In_Because()
    {
        var result = BuildEngine().Evaluate(
            AdultPatient(),
            new[]
            {
                new Medication("ibuprofen"),                // collides with NSAID allergy (exact)
                new Medication("amoxicillin"),              // collides with penicillin allergy (class)
                new Medication("metformin"),                // no collision
            },
            Array.Empty<Lab>(),
            Array.Empty<Condition>(),
            new[]
            {
                new Allergy("ibuprofen", Class: "nsaid"),
                new Allergy("penicillin", Class: "penicillin"),
            });

        var alert = result.Alerts.Should().ContainSingle().Subject;
        alert.Because.Should().HaveCount(2);
        alert.Message.Should().Contain("2 active medications");
        alert.Because.SelectMany(f => f.Parents).Select(p => p.Name)
            .Should().Contain(new[] { "medication.ibuprofen", "medication.amoxicillin", "allergy.ibuprofen", "allergy.penicillin" });
    }

    [Fact]
    public void Headline_Prefers_Exact_Match_Over_Class_Match()
    {
        // Both a class-match (amoxicillin ↔ penicillin) and an exact-match
        // (ibuprofen ↔ ibuprofen) collide. The headline must reference the
        // exact match, not the class one.
        var result = BuildEngine().Evaluate(
            AdultPatient(),
            new[]
            {
                new Medication("amoxicillin"),
                new Medication("ibuprofen"),
            },
            Array.Empty<Lab>(),
            Array.Empty<Condition>(),
            new[]
            {
                new Allergy("penicillin", Class: "penicillin"),
                new Allergy("ibuprofen", Class: "nsaid"),
            });

        var alert = result.Alerts.Should().ContainSingle().Subject;
        alert.Message.Should().Contain("\"ibuprofen\"",
            because: "the exact-match collision is stronger evidence than the class-match");
        alert.Message.Should().NotContain("cross-reactive",
            because: "the headline collision is an exact match, not class");
    }

    [Fact]
    public void Fingerprint_Is_Stable_Across_Runs()
    {
        var inputs = new
        {
            Patient = AdultPatient(),
            Meds = new[] { new Medication("ibuprofen") },
            Allergies = new[] { new Allergy("ibuprofen") },
        };
        string Run()
        {
            var alert = BuildEngine().Evaluate(
                inputs.Patient, inputs.Meds, Array.Empty<Lab>(),
                Array.Empty<Condition>(), inputs.Allergies).Alerts.Single();
            return alert.Fingerprint;
        }
        Run().Should().Be(Run());
    }
}
