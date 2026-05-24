using Chiron.Cds.Engine.Primitives;
using Chiron.Cds.Engine.Rules.Quality;
using FluentAssertions;

namespace Chiron.Cds.Engine.Tests;

public class HEDISTests
{
    private static Engine BuildA1cEngine() =>
        new Engine().RegisterRule(HEDISRules.Rules.Single(r => r.Id == "hedis.dm.a1c.uncontrolled"));

    private static Engine BuildBpEngine() =>
        new Engine().RegisterRule(HEDISRules.Rules.Single(r => r.Id == "hedis.htn.bp.uncontrolled"));

    private static Engine BuildStatinEngine() =>
        new Engine().RegisterRule(HEDISRules.Rules.Single(r => r.Id == "hedis.spc.statin_for_ascvd"));

    private static Patient Adult() => new("p", AgeYears: 60, Sex: "F");

    // -------- DM A1c control --------

    [Fact]
    public void Non_Diabetic_With_High_A1c_Does_Not_Fire()
    {
        var result = BuildA1cEngine().Evaluate(
            Adult(),
            Array.Empty<Medication>(),
            new[] { new Lab("hemoglobin_a1c", 10.5, "%") },
            Array.Empty<Condition>());
        result.Alerts.Should().BeEmpty();
    }

    [Fact]
    public void Diabetic_Without_A1c_Does_Not_Fire()
    {
        var result = BuildA1cEngine().Evaluate(
            Adult(),
            Array.Empty<Medication>(),
            Array.Empty<Lab>(),
            new[] { new Condition("diabetes") });
        result.Alerts.Should().BeEmpty();
    }

    [Fact]
    public void Diabetic_With_Controlled_A1c_Does_Not_Fire()
    {
        var result = BuildA1cEngine().Evaluate(
            Adult(),
            Array.Empty<Medication>(),
            new[] { new Lab("hemoglobin_a1c", 7.2, "%") },
            new[] { new Condition("diabetes") });
        result.Alerts.Should().BeEmpty();
    }

    [Fact]
    public void Diabetic_With_A1c_Over_9_Fires_Medium()
    {
        var result = BuildA1cEngine().Evaluate(
            Adult(),
            Array.Empty<Medication>(),
            new[] { new Lab("hemoglobin_a1c", 10.5, "%") },
            new[] { new Condition("diabetes") });
        var alert = result.Alerts.Should().ContainSingle().Subject;
        alert.Severity.Should().Be(Severity.Medium);
        alert.Message.Should().Contain("10.5");
        alert.Message.Should().Contain("HbA1c");
    }

    [Fact]
    public void Diabetic_With_A1c_Exactly_9_Does_Not_Fire()
    {
        // HEDIS measures "poor control" as A1c > 9.0 (strictly greater).
        var result = BuildA1cEngine().Evaluate(
            Adult(),
            Array.Empty<Medication>(),
            new[] { new Lab("hemoglobin_a1c", 9.0, "%") },
            new[] { new Condition("diabetes") });
        result.Alerts.Should().BeEmpty();
    }

    // -------- HTN BP control --------

    [Fact]
    public void Non_Hypertensive_With_High_Bp_Does_Not_Fire()
    {
        var result = BuildBpEngine().Evaluate(
            Adult(),
            Array.Empty<Medication>(),
            new[] { new Lab("systolic_bp", 160, "mmHg"), new Lab("diastolic_bp", 95, "mmHg") },
            Array.Empty<Condition>());
        result.Alerts.Should().BeEmpty();
    }

    [Fact]
    public void Hypertensive_Without_Bp_Does_Not_Fire()
    {
        var result = BuildBpEngine().Evaluate(
            Adult(),
            Array.Empty<Medication>(),
            Array.Empty<Lab>(),
            new[] { new Condition("hypertension") });
        result.Alerts.Should().BeEmpty();
    }

    [Fact]
    public void Hypertensive_With_Controlled_Bp_Does_Not_Fire()
    {
        var result = BuildBpEngine().Evaluate(
            Adult(),
            Array.Empty<Medication>(),
            new[] { new Lab("systolic_bp", 128, "mmHg"), new Lab("diastolic_bp", 82, "mmHg") },
            new[] { new Condition("hypertension") });
        result.Alerts.Should().BeEmpty();
    }

    [Fact]
    public void Hypertensive_With_High_Systolic_Fires()
    {
        var result = BuildBpEngine().Evaluate(
            Adult(),
            Array.Empty<Medication>(),
            new[] { new Lab("systolic_bp", 152, "mmHg"), new Lab("diastolic_bp", 85, "mmHg") },
            new[] { new Condition("hypertension") });
        var alert = result.Alerts.Should().ContainSingle().Subject;
        alert.Message.Should().Contain("152/85");
    }

    [Fact]
    public void Hypertensive_With_High_Diastolic_Fires()
    {
        var result = BuildBpEngine().Evaluate(
            Adult(),
            Array.Empty<Medication>(),
            new[] { new Lab("systolic_bp", 130, "mmHg"), new Lab("diastolic_bp", 95, "mmHg") },
            new[] { new Condition("hypertension") });
        var alert = result.Alerts.Should().ContainSingle().Subject;
        alert.Message.Should().Contain("130/95");
    }

    // -------- Statin for ASCVD --------

    [Fact]
    public void No_Ascvd_Does_Not_Fire_Statin_Gap()
    {
        var result = BuildStatinEngine().Evaluate(
            Adult(),
            Array.Empty<Medication>(),
            Array.Empty<Lab>(),
            new[] { new Condition("hypertension") });
        result.Alerts.Should().BeEmpty();
    }

    [Fact]
    public void Established_Ascvd_Without_Statin_Fires_High()
    {
        var result = BuildStatinEngine().Evaluate(
            Adult(),
            Array.Empty<Medication>(),
            Array.Empty<Lab>(),
            new[] { new Condition("myocardial_infarction") });
        var alert = result.Alerts.Should().ContainSingle().Subject;
        alert.Severity.Should().Be(Severity.High);
        alert.Message.Should().Contain("myocardial infarction");
    }

    [Fact]
    public void Established_Ascvd_On_Statin_Does_Not_Fire()
    {
        var result = BuildStatinEngine().Evaluate(
            Adult(),
            new[] { new Medication("atorvastatin") },
            Array.Empty<Lab>(),
            new[] { new Condition("myocardial_infarction") });
        result.Alerts.Should().BeEmpty();
    }

    [Theory]
    [InlineData("myocardial_infarction")]
    [InlineData("stroke")]
    [InlineData("peripheral_artery_disease")]
    public void Each_Ascvd_Subtype_Triggers_Statin_Gap(string condition)
    {
        var result = BuildStatinEngine().Evaluate(
            Adult(),
            Array.Empty<Medication>(),
            Array.Empty<Lab>(),
            new[] { new Condition(condition) });
        result.Alerts.Should().ContainSingle();
    }

    [Fact]
    public void Inactive_Statin_Does_Not_Satisfy_Spc()
    {
        var result = BuildStatinEngine().Evaluate(
            Adult(),
            new[] { new Medication("atorvastatin", Active: false) },
            Array.Empty<Lab>(),
            new[] { new Condition("stroke") });
        result.Alerts.Should().ContainSingle();
    }

    [Fact]
    public void All_Three_HEDIS_Rules_Register_Via_Pack_Discovery()
    {
        var engine = new Engine().RegisterPack(typeof(HEDISRules).Assembly);
        engine.Rules.Select(r => r.Id).Should().Contain(new[]
        {
            "hedis.dm.a1c.uncontrolled",
            "hedis.htn.bp.uncontrolled",
            "hedis.spc.statin_for_ascvd",
        });
    }
}
