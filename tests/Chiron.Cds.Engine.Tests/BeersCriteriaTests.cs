using Chiron.Cds.Engine.Primitives;
using Chiron.Cds.Engine.Rules.Geriatric;
using FluentAssertions;

namespace Chiron.Cds.Engine.Tests;

public class BeersCriteriaTests
{
    private static Engine BuildEngine() =>
        new Engine().RegisterRule(BeersCriteriaRule.Rules.Single());

    private static Patient Elder(int age = 78) => new("p", AgeYears: age, Sex: "F");

    [Fact]
    public void Patient_Under_65_Does_Not_Fire()
    {
        var result = BuildEngine().Evaluate(
            new Patient("p", AgeYears: 64, Sex: "F"),
            new[] { new Medication("diphenhydramine") },
            Array.Empty<Lab>(),
            Array.Empty<Condition>());
        result.Alerts.Should().BeEmpty();
    }

    [Fact]
    public void Elderly_On_Diphenhydramine_Fires_Medium()
    {
        var result = BuildEngine().Evaluate(
            Elder(),
            new[] { new Medication("diphenhydramine") },
            Array.Empty<Lab>(),
            Array.Empty<Condition>());

        var alert = result.Alerts.Should().ContainSingle().Subject;
        alert.RuleId.Should().Be("beers.pim.elderly");
        alert.Severity.Should().Be(Severity.Medium);
        alert.Message.Should().Contain("diphenhydramine");
        alert.Message.Should().Contain("anticholinergic");
    }

    [Fact]
    public void Multiple_Pim_Meds_Bumps_To_High()
    {
        // 3+ Beers-flagged meds = polypharmacy concern → escalate
        var result = BuildEngine().Evaluate(
            Elder(),
            new[]
            {
                new Medication("zolpidem"),
                new Medication("lorazepam"),
                new Medication("cyclobenzaprine"),
            },
            Array.Empty<Lab>(),
            Array.Empty<Condition>());

        var alert = result.Alerts.Should().ContainSingle().Subject;
        alert.Severity.Should().Be(Severity.High);
        alert.Message.Should().Contain("3 potentially inappropriate");
        alert.Because.Should().HaveCount(3);
    }

    [Theory]
    [InlineData("cyclobenzaprine")]
    [InlineData("amitriptyline")]
    [InlineData("glyburide")]
    [InlineData("oxybutynin")]
    [InlineData("indomethacin")]
    [InlineData("meperidine")]
    [InlineData("haloperidol")]
    public void Common_Beers_Medications_All_Fire(string medication)
    {
        var result = BuildEngine().Evaluate(
            Elder(),
            new[] { new Medication(medication) },
            Array.Empty<Lab>(),
            Array.Empty<Condition>());
        result.Alerts.Should().ContainSingle()
            .Which.Message.Should().Contain(medication);
    }

    [Fact]
    public void Non_Beers_Medications_Do_Not_Fire()
    {
        var result = BuildEngine().Evaluate(
            Elder(),
            new[] { new Medication("metformin"), new Medication("atorvastatin"), new Medication("lisinopril") },
            Array.Empty<Lab>(),
            Array.Empty<Condition>());
        result.Alerts.Should().BeEmpty();
    }

    [Fact]
    public void Inactive_Beers_Medication_Is_Ignored()
    {
        var result = BuildEngine().Evaluate(
            Elder(),
            new[] { new Medication("diphenhydramine", Active: false) },
            Array.Empty<Lab>(),
            Array.Empty<Condition>());
        result.Alerts.Should().BeEmpty();
    }

    [Fact]
    public void Because_Tree_Carries_Age_Fact_And_Medication_Fact()
    {
        var result = BuildEngine().Evaluate(
            Elder(72),
            new[] { new Medication("zolpidem") },
            Array.Empty<Lab>(),
            Array.Empty<Condition>());

        var alert = result.Alerts.Should().ContainSingle().Subject;
        var fact = alert.Because.Should().ContainSingle().Subject;
        fact.Name.Should().Be("beers.zolpidem");
        fact.Parents.Should().HaveCount(2);
        fact.Parents.Select(p => p.Name).Should().Contain(new[] { "age_years", "medication.zolpidem" });
    }

    [Fact]
    public void Fingerprint_Stable_For_Same_Inputs()
    {
        string Run() => BuildEngine().Evaluate(
            Elder(72), new[] { new Medication("amitriptyline") },
            Array.Empty<Lab>(), Array.Empty<Condition>())
            .Alerts.Single().Fingerprint;
        Run().Should().Be(Run());
    }
}
