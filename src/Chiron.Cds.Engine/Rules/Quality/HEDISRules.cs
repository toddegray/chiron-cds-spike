using Chiron.Cds.Engine.Primitives;
using Chiron.Cds.Engine.Rules.Interactions;

namespace Chiron.Cds.Engine.Rules.Quality;

/// <summary>
/// NCQA HEDIS quality-measure gap rules. Each rule encodes a single
/// HEDIS measure and fires when the patient is in the denominator but
/// not the numerator (the measure is "open"). Severity Medium because
/// these are revenue-aligned gap-closures, not patient-safety alerts.
/// </summary>
[RulePack(Name = "quality")]
public static class HEDISRules
{
    private static readonly Citation HedisCdc9 = new(
        Source: "NCQA HEDIS",
        Identifier: "Comprehensive Diabetes Care: HbA1c Poor Control (>9.0%) — HEDIS Measurement Year",
        Accessed: new DateOnly(2026, 5, 24),
        Url: "https://www.ncqa.org/hedis/measures/comprehensive-diabetes-care/");

    private static readonly Citation HedisCbp = new(
        Source: "NCQA HEDIS",
        Identifier: "Controlling High Blood Pressure (CBP): <140/90 mmHg",
        Accessed: new DateOnly(2026, 5, 24),
        Url: "https://www.ncqa.org/hedis/measures/controlling-high-blood-pressure/");

    private static readonly Citation HedisSpc = new(
        Source: "NCQA HEDIS",
        Identifier: "Statin Therapy for Patients with Cardiovascular Disease (SPC)",
        Accessed: new DateOnly(2026, 5, 24),
        Url: "https://www.ncqa.org/hedis/measures/statin-therapy-for-patients-with-cardiovascular-disease/");

    /// <summary>Static rule pack — registered by <see cref="Engine.RegisterPack"/>.</summary>
    public static IEnumerable<Rule> Rules => new[]
    {
        new Rule(
            Id: "hedis.dm.a1c.uncontrolled",
            Description: "Diabetic patient with most recent HbA1c > 9.0% — HEDIS Comprehensive Diabetes Care gap.",
            Evaluate: EvaluateA1cControl,
            Citations: new[] { HedisCdc9 }),
        new Rule(
            Id: "hedis.htn.bp.uncontrolled",
            Description: "Hypertensive patient with most recent BP ≥ 140/90 mmHg — HEDIS Controlling High Blood Pressure gap.",
            Evaluate: EvaluateBpControl,
            Citations: new[] { HedisCbp }),
        new Rule(
            Id: "hedis.spc.statin_for_ascvd",
            Description: "Patient with established ASCVD not on statin therapy — HEDIS Statin Therapy for Patients with Cardiovascular Disease gap.",
            Evaluate: EvaluateStatinForAscvd,
            Citations: new[] { HedisSpc }),
    };

    private static Alert? EvaluateA1cControl(EvaluationContext ctx)
    {
        if (!HasDiabetes(ctx)) return null;
        if (!ctx.HasLab("hemoglobin_a1c")) return null;

        var a1cFact = ctx.LabFact("hemoglobin_a1c");
        var a1cValue = (double)a1cFact.Value;
        if (a1cValue <= 9.0) return null;

        var dmFact = Fact.Input("condition.diabetes", true);
        var because = new Fact(
            Name: "a1c_uncontrolled",
            Value: a1cValue,
            Unit: "percent",
            Parents: new[] { a1cFact, dmFact },
            Citations: Array.Empty<Citation>());

        return new Alert(
            RuleId: "hedis.dm.a1c.uncontrolled",
            Severity: Severity.Medium,
            Message: $"Diabetes uncontrolled: HbA1c {a1cValue:0.#}% (HEDIS goal < 9.0%).",
            Because: new[] { because },
            Citations: new[] { HedisCdc9 },
            OverrideOptions: new[]
            {
                "patient_at_end_of_life",
                "documented_advanced_complications",
                "patient_declined_intensification",
                "shared_decision_making_completed",
            });
    }

    private static Alert? EvaluateBpControl(EvaluationContext ctx)
    {
        if (!ctx.HasCondition("hypertension")) return null;
        if (!ctx.HasLab("systolic_bp") || !ctx.HasLab("diastolic_bp")) return null;

        var sbpFact = ctx.LabFact("systolic_bp");
        var dbpFact = ctx.LabFact("diastolic_bp");
        var sbp = (double)sbpFact.Value;
        var dbp = (double)dbpFact.Value;
        if (sbp < 140 && dbp < 90) return null;

        var htnFact = Fact.Input("condition.hypertension", true);
        var because = new Fact(
            Name: "bp_uncontrolled",
            Value: $"{sbp:0}/{dbp:0}",
            Unit: "mmHg",
            Parents: new[] { sbpFact, dbpFact, htnFact },
            Citations: Array.Empty<Citation>());

        return new Alert(
            RuleId: "hedis.htn.bp.uncontrolled",
            Severity: Severity.Medium,
            Message: $"Hypertension uncontrolled: BP {sbp:0}/{dbp:0} mmHg (HEDIS goal < 140/90).",
            Because: new[] { because },
            Citations: new[] { HedisCbp },
            OverrideOptions: new[]
            {
                "white_coat_hypertension_documented",
                "home_bp_in_target",
                "intensification_in_progress",
                "patient_declined_intensification",
            });
    }

    private static Alert? EvaluateStatinForAscvd(EvaluationContext ctx)
    {
        var hasAscvd = ctx.HasCondition("myocardial_infarction")
            || ctx.HasCondition("stroke")
            || ctx.HasCondition("peripheral_artery_disease");
        if (!hasAscvd) return null;

        var alreadyOnStatin = ctx.Medications.Any(m =>
            m.Active && DrugAllergyRule.KnownDrugClasses.GetValueOrDefault(m.Name) == "statin");
        if (alreadyOnStatin) return null;

        var triggeringCondition = ctx.HasCondition("myocardial_infarction") ? "myocardial_infarction"
            : ctx.HasCondition("stroke") ? "stroke"
            : "peripheral_artery_disease";
        var ascvdFact = Fact.Input($"condition.{triggeringCondition}", true);
        var noStatinFact = Fact.Input("on_statin", false);

        var because = new Fact(
            Name: "statin_omitted_for_ascvd",
            Value: triggeringCondition,
            Unit: null,
            Parents: new[] { ascvdFact, noStatinFact },
            Citations: Array.Empty<Citation>());

        return new Alert(
            RuleId: "hedis.spc.statin_for_ascvd",
            Severity: Severity.High,
            Message: $"Established ASCVD ({triggeringCondition.Replace('_', ' ')}) but no active statin — HEDIS SPC gap.",
            Because: new[] { because },
            Citations: new[] { HedisSpc },
            OverrideOptions: new[]
            {
                "documented_statin_intolerance",
                "patient_declined_statin",
                "limited_life_expectancy",
                "high_intensity_dose_being_titrated",
            });
    }

    private static bool HasDiabetes(EvaluationContext ctx) =>
        ctx.HasCondition("diabetes")
        || ctx.HasCondition("type_2_diabetes_mellitus")
        || ctx.HasCondition("type_1_diabetes_mellitus");
}
