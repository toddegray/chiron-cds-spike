using Chiron.Cds.Engine.Primitives;
using Chiron.Cds.Engine.Rules.Interactions;

namespace Chiron.Cds.Engine.Rules.Scores;

/// <summary>
/// 10-year atherosclerotic cardiovascular disease (ASCVD) risk per the
/// 2013 ACC/AHA Pooled Cohort Equations. Eligible: ages 40–79 without
/// established ASCVD. Fires statin-eligibility alert when 10-year risk
/// crosses the ACC/AHA 7.5% threshold.
/// </summary>
/// <remarks>
/// Spike implementation uses the "Other" (non-Black) coefficients for all
/// patients. The original PCE has separate equations for Black vs.
/// non-Black, sex-stratified; production rules should either consume the
/// patient's US Core race extension or migrate to the 2024 PREVENT
/// equations (race-free). The card's derivation tree shows every input
/// fact and citation so a reviewing clinician sees the simplification.
/// </remarks>
[RulePack(Name = "scores")]
public static class ASCVDRule
{
    private static readonly Citation AccAhaPce2013 = new(
        Source: "ACC/AHA 2013 Cholesterol Guideline",
        Identifier: "Goff DC Jr et al., Circulation. 2014;129(25 Suppl 2):S49-S73",
        Accessed: new DateOnly(2026, 5, 23),
        Url: "https://www.ahajournals.org/doi/10.1161/01.cir.0000437741.48606.98");

    private static readonly Citation Aha2018Cholesterol = new(
        Source: "ACC/AHA 2018 Cholesterol Guideline",
        Identifier: "Grundy SM et al., Circulation. 2019;139(25):e1082-e1143",
        Accessed: new DateOnly(2026, 5, 23),
        Url: "https://www.ahajournals.org/doi/10.1161/CIR.0000000000000625");

    /// <summary>Static rule pack — registered by <see cref="Engine.RegisterPack"/>.</summary>
    public static IEnumerable<Rule> Rules => new[]
    {
        new Rule(
            Id: "ascvd.10y.statin_eligible",
            Description: "10-year ASCVD risk ≥ 7.5% per Pooled Cohort Equations — statin therapy generally recommended.",
            Evaluate: Evaluate,
            Citations: new[] { AccAhaPce2013, Aha2018Cholesterol }),
    };

    private static Alert? Evaluate(EvaluationContext ctx)
    {
        var age = ctx.Patient.AgeYears;
        if (age is < 40 or > 79) return null;

        // Already on statin → don't fire the statin-eligibility alert.
        if (ctx.Medications.Any(m => m.Active && DrugAllergyRule.KnownDrugClasses.GetValueOrDefault(m.Name) == "statin"))
            return null;

        // Established ASCVD → secondary prevention, different track.
        if (ctx.HasCondition("myocardial_infarction") || ctx.HasCondition("stroke") || ctx.HasCondition("peripheral_artery_disease"))
            return null;

        if (!ctx.HasLab("total_cholesterol") || !ctx.HasLab("hdl_cholesterol") || !ctx.HasLab("systolic_bp"))
            return null;

        var totalChol = ctx.LabFact("total_cholesterol");
        var hdl = ctx.LabFact("hdl_cholesterol");
        var sbp = ctx.LabFact("systolic_bp");
        var ageFact = ctx.PatientFact("age_years");
        var sexFact = ctx.PatientFact("sex");

        var isFemale = string.Equals((string)sexFact.Value, "F", StringComparison.OrdinalIgnoreCase);
        var hasDiabetes = ctx.HasCondition("diabetes")
            || ctx.HasCondition("type_2_diabetes_mellitus")
            || ctx.HasCondition("type_1_diabetes_mellitus");
        var isSmoker = ctx.HasCondition("current_smoker") || ctx.HasCondition("tobacco_use");
        var onBpTreatment = ctx.Medications.Any(m =>
        {
            if (!m.Active) return false;
            var cls = DrugAllergyRule.KnownDrugClasses.GetValueOrDefault(m.Name);
            return cls is not null && DrugAllergyRule.AntihypertensiveClasses.Contains(cls);
        });

        var risk = ComputePooledCohortRisk(
            ageYears: (double)ageFact.Value,
            isFemale: isFemale,
            totalCholMgDl: (double)totalChol.Value,
            hdlMgDl: (double)hdl.Value,
            systolicBpMmHg: (double)sbp.Value,
            onBpTreatment: onBpTreatment,
            hasDiabetes: hasDiabetes,
            isSmoker: isSmoker);

        if (risk < 0.075) return null; // below ACC/AHA statin threshold

        var diabetesFact = Fact.Input("diabetes", hasDiabetes);
        var smokerFact = Fact.Input("current_smoker", isSmoker);
        var bpTxFact = Fact.Input("on_bp_treatment", onBpTreatment);

        var riskFact = new Fact(
            Name: "ascvd_10y_risk",
            Value: Math.Round(risk * 100, 1),
            Unit: "percent",
            Parents: new[] { ageFact, sexFact, totalChol, hdl, sbp, bpTxFact, diabetesFact, smokerFact },
            Citations: new[] { AccAhaPce2013 });

        var severity = risk switch
        {
            >= 0.20 => Severity.High,    // Per ACC/AHA: ≥ 20% = "high" — initiate high-intensity statin
            >= 0.10 => Severity.Medium,  // 7.5–20% = intermediate
            _ => Severity.Low,
        };

        return new Alert(
            RuleId: "ascvd.10y.statin_eligible",
            Severity: severity,
            Message: $"10-year ASCVD risk {risk * 100:0.#}% — statin therapy generally recommended (ACC/AHA 2018).",
            Because: new[] { riskFact },
            Citations: new[] { AccAhaPce2013, Aha2018Cholesterol },
            OverrideOptions: new[]
            {
                "patient_declined_statin",
                "documented_statin_intolerance",
                "lifestyle_modifications_in_progress",
                "shared_decision_making_completed",
            });
    }

    /// <summary>
    /// Pooled Cohort Equation 10-year ASCVD risk, "Other" (non-Black)
    /// coefficients. Returns a probability in [0, 1].
    /// </summary>
    public static double ComputePooledCohortRisk(
        double ageYears,
        bool isFemale,
        double totalCholMgDl,
        double hdlMgDl,
        double systolicBpMmHg,
        bool onBpTreatment,
        bool hasDiabetes,
        bool isSmoker)
    {
        // Coefficients from Goff et al. 2013 Appendix 7 (non-Black women)
        // and Appendix 8 (non-Black men).
        double sum;
        double baselineSurvival;
        double meanCoefficient;

        var lnAge = Math.Log(ageYears);
        var lnTotal = Math.Log(totalCholMgDl);
        var lnHdl = Math.Log(hdlMgDl);
        var lnSbp = Math.Log(systolicBpMmHg);

        if (isFemale)
        {
            sum =
                -29.799 * lnAge
                + 4.884 * (lnAge * lnAge)
                + 13.540 * lnTotal
                + -3.114 * (lnAge * lnTotal)
                + -13.578 * lnHdl
                + 3.149 * (lnAge * lnHdl)
                + (onBpTreatment ? 2.019 * lnSbp : 1.957 * lnSbp)
                + (isSmoker ? 7.574 : 0.0)
                + (isSmoker ? -1.665 * lnAge : 0.0)
                + (hasDiabetes ? 0.661 : 0.0);
            baselineSurvival = 0.9665;
            meanCoefficient = -29.18;
        }
        else
        {
            sum =
                12.344 * lnAge
                + 11.853 * lnTotal
                + -2.664 * (lnAge * lnTotal)
                + -7.990 * lnHdl
                + 1.769 * (lnAge * lnHdl)
                + (onBpTreatment ? 1.797 * lnSbp : 1.764 * lnSbp)
                + (isSmoker ? 7.837 : 0.0)
                + (isSmoker ? -1.795 * lnAge : 0.0)
                + (hasDiabetes ? 0.658 : 0.0);
            baselineSurvival = 0.9144;
            meanCoefficient = 61.18;
        }

        var risk = 1.0 - Math.Pow(baselineSurvival, Math.Exp(sum - meanCoefficient));
        return Math.Clamp(risk, 0.0, 1.0);
    }
}
