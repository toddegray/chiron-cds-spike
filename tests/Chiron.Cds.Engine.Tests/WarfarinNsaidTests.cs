using Chiron.Cds.Engine.Primitives;
using Chiron.Cds.Engine.Rules.Interactions;
using FluentAssertions;

namespace Chiron.Cds.Engine.Tests;

public class WarfarinNsaidTests
{
    private static Engine BuildEngine() =>
        new Engine().RegisterRule(WarfarinNsaidRule.Rules.Single());

    [Theory]
    [InlineData("ibuprofen")]
    [InlineData("naproxen")]
    [InlineData("ketorolac")]
    [InlineData("aspirin")]
    public void Fires_For_Common_Nsaids(string nsaid)
    {
        var patient = new Patient("p", 60, "M");
        var result = BuildEngine().Evaluate(patient,
            new[] { new Medication("warfarin"), new Medication(nsaid) },
            Array.Empty<Lab>(),
            Array.Empty<Condition>());
        result.Alerts.Should().ContainSingle()
            .Which.Message.Should().Contain(nsaid);
    }

    [Fact]
    public void Does_Not_Fire_Without_Warfarin()
    {
        var patient = new Patient("p", 60, "M");
        var result = BuildEngine().Evaluate(patient,
            new[] { new Medication("ibuprofen") },
            Array.Empty<Lab>(),
            Array.Empty<Condition>());
        result.Alerts.Should().BeEmpty();
    }

    [Fact]
    public void Does_Not_Fire_For_Non_Nsaid()
    {
        var patient = new Patient("p", 60, "F");
        var result = BuildEngine().Evaluate(patient,
            new[] { new Medication("warfarin"), new Medication("acetaminophen") },
            Array.Empty<Lab>(),
            Array.Empty<Condition>());
        result.Alerts.Should().BeEmpty();
    }

    [Fact]
    public void Inactive_Medications_Are_Ignored()
    {
        var patient = new Patient("p", 60, "M");
        var result = BuildEngine().Evaluate(patient,
            new[] { new Medication("warfarin"), new Medication("ibuprofen", Active: false) },
            Array.Empty<Lab>(),
            Array.Empty<Condition>());
        result.Alerts.Should().BeEmpty();
    }
}
