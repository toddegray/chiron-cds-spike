using Chiron.Cds.Engine.Primitives;
using Chiron.Cds.Engine.Rules.Renal;

namespace Chiron.Cds.Engine.Rules.Interactions;

/// <summary>
/// Generalizes the metformin/renal pattern: any active medication on the
/// renal-dose-adjustment table is flagged when the patient's eGFR is
/// below the medication-specific threshold. Derives eGFR from creatinine
/// + age + sex via the same CKD-EPI 2021 equation
/// <see cref="MetforminRenalRule.ComputeEgfrCkdEpi"/> uses, so the
/// derivation graph and fingerprint are byte-identical with the existing
/// metformin rule on overlapping inputs.
/// </summary>
[RulePack(Name = "interactions")]
public static class DrugDoseRenalRule
{
    private static readonly Citation FdaLabelLib = new(
        Source: "FDA labels (multi-drug)",
        Identifier: "Renal dose-adjustment thresholds aggregated from FDA Prescribing Information (illustrative; production would license a drug-knowledge vendor's renal-dosing matrix)",
        Accessed: new DateOnly(2026, 5, 24));

    private static readonly Citation CkdEpi2021 = new(
        Source: "NEJM 2021",
        Identifier: "10.1056/NEJMoa2102953",
        Accessed: new DateOnly(2026, 5, 24),
        Url: "https://www.nejm.org/doi/full/10.1056/NEJMoa2102953");

    /// <summary>Static rule pack — registered by <see cref="Engine.RegisterPack"/>.</summary>
    public static IEnumerable<Rule> Rules => new[]
    {
        new Rule(
            Id: "drug.dose.renal_threshold",
            Description: "Active medication contraindicated or requires dose adjustment at the patient's current eGFR.",
            Evaluate: Evaluate,
            Citations: new[] { FdaLabelLib, CkdEpi2021 }),
    };

    /// <summary>
    /// Renally-cleared medications and the eGFR threshold below which the
    /// FDA label recommends contraindication, avoidance, or significant
    /// dose adjustment. (Metformin is intentionally excluded — the
    /// dedicated <see cref="MetforminRenalRule"/> still owns its alert so
    /// the existing fixtures and fingerprint stay stable.)
    /// </summary>
    public static readonly IReadOnlyDictionary<string, RenalDoseEntry> RenalDoseTable = new Dictionary<string, RenalDoseEntry>(StringComparer.OrdinalIgnoreCase)
    {
        // SGLT-2 inhibitors — limited efficacy / safety at low eGFR
        ["empagliflozin"] = new("empagliflozin", EgfrThreshold: 20, Rationale: "Avoid initiation below eGFR 20; established cardiovascular indications may continue with lower threshold."),
        ["dapagliflozin"] = new("dapagliflozin", EgfrThreshold: 25, Rationale: "Avoid initiation below eGFR 25."),
        ["canagliflozin"] = new("canagliflozin", EgfrThreshold: 30, Rationale: "Do not initiate below eGFR 30."),
        // Direct oral anticoagulants — major dose modifications under CKD
        ["dabigatran"] = new("dabigatran", EgfrThreshold: 30, Rationale: "Avoid below eGFR 30; renal elimination > 80%."),
        ["rivaroxaban"] = new("rivaroxaban", EgfrThreshold: 30, Rationale: "Reduce dose for atrial fibrillation indication; avoid for VTE treatment."),
        ["apixaban"] = new("apixaban", EgfrThreshold: 25, Rationale: "Consider 2.5 mg BID dose adjustment at low eGFR per label."),
        // Bisphosphonates
        ["alendronate"] = new("alendronate", EgfrThreshold: 35, Rationale: "Avoid below eGFR 35 — limited efficacy data and risk of hypocalcemia."),
        // Renally cleared antimicrobials (illustrative subset)
        ["nitrofurantoin"] = new("nitrofurantoin", EgfrThreshold: 30, Rationale: "Avoid below eGFR 30 — inadequate urinary concentrations and pulmonary toxicity risk."),
        // Statins requiring renal dose limits
        ["rosuvastatin"] = new("rosuvastatin", EgfrThreshold: 30, Rationale: "Start at 5 mg and do not exceed 10 mg daily below eGFR 30."),
        // Gabapentinoids
        ["gabapentin"] = new("gabapentin", EgfrThreshold: 30, Rationale: "Reduce dose proportional to eGFR; avoid scheduled high-dose regimens below 30."),
        ["pregabalin"] = new("pregabalin", EgfrThreshold: 30, Rationale: "Dose-adjustment required; max 150 mg/day below eGFR 30."),
    };

    private static Alert? Evaluate(EvaluationContext ctx)
    {
        if (!ctx.HasLab("creatinine")) return null;

        // Find any active med whose name is in the renal-dose table.
        var hits = ctx.Medications
            .Where(m => m.Active && RenalDoseTable.ContainsKey(m.Name))
            .Select(m => new { Medication = m, Entry = RenalDoseTable[m.Name] })
            .ToArray();
        if (hits.Length == 0) return null;

        var creatFact = ctx.LabFact("creatinine");
        var ageFact = ctx.PatientFact("age_years");
        var sexFact = ctx.PatientFact("sex");
        var egfr = MetforminRenalRule.ComputeEgfrCkdEpi(
            scr: (double)creatFact.Value,
            ageYears: (double)ageFact.Value,
            sex: (string)sexFact.Value);

        var egfrFact = new Fact(
            Name: "egfr_ckd_epi",
            Value: Math.Round(egfr, 4),
            Unit: "mL/min/1.73m²",
            Parents: new[] { creatFact, ageFact, sexFact },
            Citations: new[] { CkdEpi2021 });

        // Filter to hits where the patient's eGFR is below the per-med threshold.
        var triggering = hits.Where(h => egfr < h.Entry.EgfrThreshold).ToArray();
        if (triggering.Length == 0) return null;

        var because = triggering.Select(h => new Fact(
            Name: $"renal_threshold.{h.Medication.Name.ToLowerInvariant()}",
            Value: $"egfr<{h.Entry.EgfrThreshold}",
            Unit: null,
            Parents: new[] { egfrFact, Fact.Input($"medication.{h.Medication.Name.ToLowerInvariant()}", true) },
            Citations: new[] { FdaLabelLib })).ToArray();

        var medList = string.Join(", ", triggering.Select(h => h.Medication.Name));
        var headline = triggering[0];
        var message = triggering.Length == 1
            ? $"Renal dose adjustment needed: {headline.Medication.Name} at eGFR {egfr:0.#} (label threshold <{headline.Entry.EgfrThreshold}). {headline.Entry.Rationale}"
            : $"{triggering.Length} medications require renal dose review at eGFR {egfr:0.#}: {medList}.";

        return new Alert(
            RuleId: "drug.dose.renal_threshold",
            Severity: Severity.High,
            Message: message,
            Because: because,
            Citations: new[] { FdaLabelLib, CkdEpi2021 },
            OverrideOptions: new[]
            {
                "documented_dose_adjusted_per_label",
                "consultation_with_nephrology_in_place",
                "established_cardiovascular_indication",
                "patient_on_renal_replacement_therapy",
            });
    }

    /// <summary>One renal-dose entry: the med, the FDA-label eGFR threshold, and the rationale shown in the alert.</summary>
    public sealed record RenalDoseEntry(string Medication, double EgfrThreshold, string Rationale);
}
