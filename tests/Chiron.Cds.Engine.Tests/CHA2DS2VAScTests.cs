using Chiron.Cds.Engine.Primitives;
using Chiron.Cds.Engine.Rules.Scores;
using FluentAssertions;

namespace Chiron.Cds.Engine.Tests;

public class CHA2DS2VAScTests
{
    private static Engine BuildEngine() =>
        new Engine().RegisterRule(CHA2DS2VAScRule.Rules.Single());

    [Fact]
    public void Score_Below_2_Does_Not_Fire()
    {
        var patient = new Patient("p", 55, "M");
        var result = BuildEngine().Evaluate(patient,
            Array.Empty<Medication>(),
            Array.Empty<Lab>(),
            Array.Empty<Condition>());
        result.Alerts.Should().BeEmpty();
    }

    [Fact]
    public void Score_2_Fires_Medium()
    {
        // 55yo female with hypertension = female(1) + htn(1) = 2.
        var patient = new Patient("p", 55, "F");
        var result = BuildEngine().Evaluate(patient,
            Array.Empty<Medication>(),
            Array.Empty<Lab>(),
            new[] { new Condition("hypertension") });
        result.Alerts.Should().ContainSingle()
            .Which.Severity.Should().Be(Severity.Medium);
    }

    [Fact]
    public void Score_4_Plus_Fires_High()
    {
        // 78yo female with htn, diabetes = age75(2) + female(1) + htn(1) + diabetes(1) = 5.
        var patient = new Patient("p", 78, "F");
        var result = BuildEngine().Evaluate(patient,
            Array.Empty<Medication>(),
            Array.Empty<Lab>(),
            new[]
            {
                new Condition("hypertension"),
                new Condition("diabetes"),
            });
        var alert = result.Alerts.Should().ContainSingle().Subject;
        alert.Severity.Should().Be(Severity.High);
        alert.Because.Should().ContainSingle();
        alert.Because[0].Name.Should().Be("cha2ds2_vasc.total");
        ((double)alert.Because[0].Value).Should().Be(5.0);
    }

    [Fact]
    public void Stroke_History_Adds_Two_Points()
    {
        // 55yo male with prior stroke = stroke(2). Below threshold (=2) wait, 2 ≥ 2.
        var patient = new Patient("p", 55, "M");
        var result = BuildEngine().Evaluate(patient,
            Array.Empty<Medication>(),
            Array.Empty<Lab>(),
            new[] { new Condition("stroke") });
        result.Alerts.Should().ContainSingle()
            .Which.Severity.Should().Be(Severity.Medium);
    }

    [Fact]
    public void Inactive_Conditions_Are_Ignored()
    {
        var patient = new Patient("p", 55, "F");
        var result = BuildEngine().Evaluate(patient,
            Array.Empty<Medication>(),
            Array.Empty<Lab>(),
            new[] { new Condition("hypertension", Active: false) });
        // Female (1) + htn ignored = 1, below threshold.
        result.Alerts.Should().BeEmpty();
    }
}
