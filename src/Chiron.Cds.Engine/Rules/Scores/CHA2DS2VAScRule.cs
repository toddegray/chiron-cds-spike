using Chiron.Cds.Engine.Primitives;

namespace Chiron.Cds.Engine.Rules.Scores;

/// <summary>
/// CHA₂DS₂-VASc atrial-fibrillation stroke-risk score, composed as a
/// rule so the score's components show up in <see cref="Alert.Because"/>
/// the same way a hand-written rule's derivation does.
/// </summary>
[RulePack(Name = "scores")]
public static class CHA2DS2VAScRule
{
    private static readonly Citation AhaAccHrs2019 = new(
        Source: "AHA/ACC/HRS guideline",
        Identifier: "2019 Focused Update; Circulation 2019;140:e125-e151",
        Accessed: new DateOnly(2026, 4, 29),
        Url: "https://www.ahajournals.org/doi/10.1161/CIR.0000000000000665");

    /// <summary>Static rule pack — registered by <see cref="Engine.RegisterPack"/>.</summary>
    public static IEnumerable<Rule> Rules => new[]
    {
        new Rule(
            Id: "cha2ds2_vasc.high_risk",
            Description: "CHA₂DS₂-VASc ≥ 2 — anticoagulation generally recommended.",
            Evaluate: Evaluate,
            Citations: new[] { AhaAccHrs2019 }),
    };

    private static Alert? Evaluate(EvaluationContext ctx)
    {
        var components = new List<(string Name, int Weight, bool Triggered)>
        {
            ("congestive_heart_failure",  1, ctx.HasCondition("heart_failure") || ctx.HasCondition("congestive_heart_failure")),
            ("hypertension",              1, ctx.HasCondition("hypertension")),
            ("age_75_or_older",           2, ctx.Patient.AgeYears >= 75),
            ("diabetes",                  1, ctx.HasCondition("diabetes") || ctx.HasCondition("type_2_diabetes_mellitus") || ctx.HasCondition("type_1_diabetes_mellitus")),
            ("prior_stroke_or_tia",       2, ctx.HasCondition("stroke") || ctx.HasCondition("tia") || ctx.HasCondition("stroke_or_tia")),
            ("vascular_disease",          1, ctx.HasCondition("vascular_disease") || ctx.HasCondition("myocardial_infarction") || ctx.HasCondition("peripheral_artery_disease")),
            ("age_65_to_74",              1, ctx.Patient.AgeYears is >= 65 and < 75),
            ("female_sex",                1, char.ToUpperInvariant(ctx.Patient.Sex.FirstOrDefault()) == 'F'),
        };

        var componentFacts = components
            .Where(c => c.Triggered)
            .Select(c => Fact.Input(c.Name, true))
            .ToList();

        var total = components.Where(c => c.Triggered).Sum(c => c.Weight);

        var totalFact = new Fact(
            Name: "cha2ds2_vasc.total",
            Value: (double)total,
            Unit: "points",
            Parents: componentFacts,
            Citations: new[] { AhaAccHrs2019 });

        if (total < 2) return null;

        return new Alert(
            RuleId: "cha2ds2_vasc.high_risk",
            Severity: total >= 4 ? Severity.High : Severity.Medium,
            Message: $"CHA₂DS₂-VASc score {total}: anticoagulation generally recommended.",
            Because: new[] { totalFact },
            Citations: new[] { AhaAccHrs2019 },
            OverrideOptions: new[]
            {
                "bleeding_risk_outweighs_benefit",
                "patient_declined_anticoagulation",
                "active_bleeding",
            });
    }
}
