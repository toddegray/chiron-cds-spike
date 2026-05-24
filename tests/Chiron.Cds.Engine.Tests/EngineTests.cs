using Chiron.Cds.Engine.Primitives;
using Chiron.Cds.Engine.Rules.Renal;
using FluentAssertions;

namespace Chiron.Cds.Engine.Tests;

public class EngineTests
{
    [Fact]
    public void RegisterRule_Refuses_Rule_Without_Citations()
    {
        var bare = new Rule("test", "no citations", _ => null, Array.Empty<Citation>());
        var engine = new Engine();
        Action act = () => engine.RegisterRule(bare);
        act.Should().Throw<ArgumentException>().WithMessage("*citations*");
    }

    [Fact]
    public void RegisterRule_Refuses_Duplicate_Ids()
    {
        var c = new Citation("test", "id", new DateOnly(2026, 4, 29));
        var r1 = new Rule("dup", "x", _ => null, new[] { c });
        var r2 = new Rule("dup", "y", _ => null, new[] { c });
        var engine = new Engine().RegisterRule(r1);
        Action act = () => engine.RegisterRule(r2);
        act.Should().Throw<InvalidOperationException>().WithMessage("*already registered*");
    }

    [Fact]
    public void RegisterPack_Discovers_Static_Rule_Lists()
    {
        var engine = new Engine().RegisterPack(typeof(MetforminRenalRule).Assembly);
        engine.Rules.Select(r => r.Id).Should().Contain("metformin.renal.contraindicated");
        engine.Rules.Select(r => r.Id).Should().Contain("cha2ds2_vasc.high_risk");
        engine.Rules.Select(r => r.Id).Should().Contain("warfarin.nsaid.bleeding_risk");
        engine.Rules.Select(r => r.Id).Should().Contain("drug.allergy.collision");
        engine.Rules.Select(r => r.Id).Should().Contain("immunization.gap");
        engine.Rules.Select(r => r.Id).Should().Contain("ascvd.10y.statin_eligible");
        engine.Rules.Select(r => r.Id).Should().Contain("beers.pim.elderly");
        engine.Rules.Select(r => r.Id).Should().Contain("uspstf.mammography.gap");
        engine.Rules.Select(r => r.Id).Should().Contain("uspstf.colorectal.gap");
        engine.Rules.Select(r => r.Id).Should().Contain("hedis.dm.a1c.uncontrolled");
        engine.Rules.Select(r => r.Id).Should().Contain("hedis.htn.bp.uncontrolled");
        engine.Rules.Select(r => r.Id).Should().Contain("hedis.spc.statin_for_ascvd");
        engine.Rules.Select(r => r.Id).Should().Contain("drug.dose.renal_threshold");
    }

    [Fact]
    public void Evaluate_Skips_Rules_With_Missing_Inputs()
    {
        // Metformin rule needs creatinine. With creatinine absent it must
        // silently no-fire rather than throw. The patient is 40/M with no
        // conditions so the other rules in the pack don't fire either.
        var engine = new Engine().RegisterPack(typeof(MetforminRenalRule).Assembly);
        var patient = new Patient("p1", AgeYears: 40, Sex: "M");
        var result = engine.Evaluate(patient,
            medications: new[] { new Medication("metformin") },
            labs: Array.Empty<Lab>(),
            conditions: Array.Empty<Condition>());
        result.Alerts.Should().NotContain(a => a.RuleId == "metformin.renal.contraindicated");
    }

    [Fact]
    public void Evaluate_Returns_ElapsedMs()
    {
        var engine = new Engine().RegisterPack(typeof(MetforminRenalRule).Assembly);
        var result = engine.Evaluate(
            new Patient("p1", 40, "M"),
            Array.Empty<Medication>(),
            Array.Empty<Lab>(),
            Array.Empty<Condition>());
        result.ElapsedMs.Should().BeGreaterThanOrEqualTo(0);
    }
}
