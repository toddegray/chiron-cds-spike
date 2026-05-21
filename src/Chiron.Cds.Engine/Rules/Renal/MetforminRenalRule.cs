using Chiron.Cds.Engine.Primitives;

namespace Chiron.Cds.Engine.Rules.Renal;

/// <summary>
/// Avoid metformin when eGFR &lt; 30 mL/min/1.73 m². Derives eGFR via the
/// CKD-EPI 2021 race-free creatinine equation. The headline rule from
/// traceable-cds, ported here so the engine has a non-trivial derivation
/// graph to show in CDS Hooks cards.
/// </summary>
[RulePack(Name = "renal")]
public static class MetforminRenalRule
{
    private static readonly Citation FdaLabelMetformin = new(
        Source: "FDA label",
        Identifier: "NDA 020357 rev 2016-04",
        Accessed: new DateOnly(2026, 4, 29),
        Url: "https://www.accessdata.fda.gov/drugsatfda_docs/label/2017/020357s037s039,021202s021s023lbl.pdf");

    private static readonly Citation CkdEpi2021 = new(
        Source: "NEJM 2021",
        Identifier: "10.1056/NEJMoa2102953",
        Accessed: new DateOnly(2026, 4, 29),
        Url: "https://www.nejm.org/doi/full/10.1056/NEJMoa2102953");

    /// <summary>Static rule pack — registered by <see cref="Engine.RegisterPack"/>.</summary>
    public static IEnumerable<Rule> Rules => new[]
    {
        new Rule(
            Id: "metformin.renal.contraindicated",
            Description: "Avoid metformin when eGFR < 30 mL/min/1.73 m² (FDA label).",
            Evaluate: Evaluate,
            Citations: new[] { FdaLabelMetformin, CkdEpi2021 }),
    };

    private static Alert? Evaluate(EvaluationContext ctx)
    {
        if (!ctx.HasMedication("metformin")) return null;
        if (!ctx.HasLab("creatinine")) return null;

        var creatFact = ctx.LabFact("creatinine");
        var ageFact = ctx.PatientFact("age_years");
        var sexFact = ctx.PatientFact("sex");

        var egfr = ComputeEgfrCkdEpi(
            scr: (double)creatFact.Value,
            ageYears: (double)ageFact.Value,
            sex: (string)sexFact.Value);

        var egfrFact = new Fact(
            Name: "egfr_ckd_epi",
            Value: Math.Round(egfr, 4),
            Unit: "mL/min/1.73m²",
            Parents: new[] { creatFact, ageFact, sexFact },
            Citations: new[] { CkdEpi2021 });

        if (egfr >= 30) return null;

        return new Alert(
            RuleId: "metformin.renal.contraindicated",
            Severity: Severity.High,
            Message: $"Avoid metformin: eGFR {egfr:0.#} mL/min/1.73m² (label limit <30).",
            Because: new[] { egfrFact },
            Citations: new[] { FdaLabelMetformin },
            OverrideOptions: new[]
            {
                "patient_on_dialysis",
                "documented_benefit_outweighs_risk",
            });
    }

    /// <summary>
    /// CKD-EPI 2021 race-free creatinine equation. Returns eGFR in
    /// mL/min/1.73 m². <paramref name="sex"/> is "F" or "M" (first letter
    /// case-insensitive).
    /// </summary>
    public static double ComputeEgfrCkdEpi(double scr, double ageYears, string sex)
    {
        var isFemale = !string.IsNullOrEmpty(sex) && char.ToUpperInvariant(sex[0]) == 'F';
        var kappa = isFemale ? 0.7 : 0.9;
        var alpha = isFemale ? -0.241 : -0.302;
        var ratio = scr / kappa;
        var minTerm = Math.Pow(Math.Min(ratio, 1.0), alpha);
        var maxTerm = Math.Pow(Math.Max(ratio, 1.0), -1.2);
        var ageTerm = Math.Pow(0.9938, ageYears);
        var sexTerm = isFemale ? 1.012 : 1.0;
        return 142.0 * minTerm * maxTerm * ageTerm * sexTerm;
    }
}
