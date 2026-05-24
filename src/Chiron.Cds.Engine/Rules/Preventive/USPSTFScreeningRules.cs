using Chiron.Cds.Engine.Primitives;

namespace Chiron.Cds.Engine.Rules.Preventive;

/// <summary>
/// USPSTF cancer-screening surveillance rules. Each rule encodes one
/// USPSTF Grade A/B recommendation: age band, sex eligibility, and the
/// surveillance interval. Fires when the patient is in the eligible band
/// and has either never had the procedure or it was performed more than
/// one interval ago.
/// </summary>
[RulePack(Name = "preventive")]
public static class USPSTFScreeningRules
{
    private static readonly Citation UspstfBreastCancer = new(
        Source: "USPSTF",
        Identifier: "Breast Cancer: Screening (2024 update; Grade B for women 40-74)",
        Accessed: new DateOnly(2026, 5, 24),
        Url: "https://www.uspreventiveservicestaskforce.org/uspstf/recommendation/breast-cancer-screening");

    private static readonly Citation UspstfColorectalCancer = new(
        Source: "USPSTF",
        Identifier: "Colorectal Cancer: Screening (2021; Grade A for 50-75, Grade B for 45-49)",
        Accessed: new DateOnly(2026, 5, 24),
        Url: "https://www.uspreventiveservicestaskforce.org/uspstf/recommendation/colorectal-cancer-screening");

    /// <summary>Static rule pack — registered by <see cref="Engine.RegisterPack"/>.</summary>
    public static IEnumerable<Rule> Rules => new[]
    {
        new Rule(
            Id: "uspstf.mammography.gap",
            Description: "Mammography overdue or never performed for women 40-74 per USPSTF 2024.",
            Evaluate: EvaluateMammography,
            Citations: new[] { UspstfBreastCancer }),
        new Rule(
            Id: "uspstf.colorectal.gap",
            Description: "Colorectal cancer screening overdue or never performed for adults 45-75 per USPSTF 2021.",
            Evaluate: EvaluateColorectal,
            Citations: new[] { UspstfColorectalCancer }),
    };

    private static readonly TimeSpan TwoYears = TimeSpan.FromDays(730);
    private static readonly TimeSpan TenYears = TimeSpan.FromDays(3650);

    private static Alert? EvaluateMammography(EvaluationContext ctx)
    {
        var age = ctx.Patient.AgeYears;
        var isFemale = char.ToUpperInvariant(ctx.Patient.Sex.FirstOrDefault()) == 'F';
        if (!isFemale) return null;
        if (age is < 40 or > 74) return null;

        var last = ctx.LatestProcedure("mammography");
        if (last is not null && DateTimeOffset.UtcNow - last.PerformedAt < TwoYears) return null;

        var ageFact = ctx.PatientFact("age_years");
        var sexFact = ctx.PatientFact("sex");
        var parents = new List<Fact> { ageFact, sexFact };
        if (last is not null)
            parents.Add(Fact.Input("last_mammography_at", last.PerformedAt.ToString("yyyy-MM-dd")));

        var gapFact = new Fact(
            Name: "gap.mammography",
            Value: last is null ? "never_performed" : "overdue",
            Unit: null,
            Parents: parents,
            Citations: Array.Empty<Citation>());

        var message = last is null
            ? "Mammography overdue: no record of prior screening. USPSTF recommends biennial mammography for women ages 40-74."
            : $"Mammography overdue: last performed {last.PerformedAt:yyyy-MM-dd}, USPSTF interval is 2 years.";

        return new Alert(
            RuleId: "uspstf.mammography.gap",
            Severity: Severity.Low,
            Message: message,
            Because: new[] { gapFact },
            Citations: new[] { UspstfBreastCancer },
            OverrideOptions: new[]
            {
                "patient_declined",
                "limited_life_expectancy",
                "documented_outside_records",
                "high_risk_alternate_imaging_in_use",
            });
    }

    private static Alert? EvaluateColorectal(EvaluationContext ctx)
    {
        var age = ctx.Patient.AgeYears;
        if (age is < 45 or > 75) return null;

        // Spike conservatively counts only colonoscopy (10y interval).
        // Production needs the full USPSTF modality matrix (FIT annual,
        // sigmoidoscopy 5y, etc.); the override option "fit_or_sigmoidoscopy_in_lieu"
        // is the clinician's escape valve until that lands.
        var last = ctx.LatestProcedure("colonoscopy");
        if (last is not null && DateTimeOffset.UtcNow - last.PerformedAt < TenYears) return null;

        var ageFact = ctx.PatientFact("age_years");
        var parents = new List<Fact> { ageFact };
        if (last is not null)
            parents.Add(Fact.Input("last_colonoscopy_at", last.PerformedAt.ToString("yyyy-MM-dd")));

        var gapFact = new Fact(
            Name: "gap.colorectal_screening",
            Value: last is null ? "never_performed" : "overdue",
            Unit: null,
            Parents: parents,
            Citations: Array.Empty<Citation>());

        var message = last is null
            ? "Colorectal cancer screening overdue: no record of prior colonoscopy. USPSTF recommends screening from age 45."
            : $"Colorectal cancer screening overdue: last colonoscopy {last.PerformedAt:yyyy-MM-dd}, interval >10y.";

        return new Alert(
            RuleId: "uspstf.colorectal.gap",
            Severity: Severity.Low,
            Message: message,
            Because: new[] { gapFact },
            Citations: new[] { UspstfColorectalCancer },
            OverrideOptions: new[]
            {
                "patient_declined",
                "limited_life_expectancy",
                "fit_or_sigmoidoscopy_in_lieu",
                "alternate_documentation",
            });
    }
}
