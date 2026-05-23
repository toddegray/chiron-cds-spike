using Chiron.Cds.Engine.Primitives;

namespace Chiron.Cds.Engine.Rules.Preventive;

/// <summary>Missing or overdue adult immunizations per ACIP schedule.</summary>
[RulePack(Name = "preventive")]
public static class ImmunizationGapRule
{
    private static readonly Citation AcipAdultSchedule = new(
        Source: "CDC ACIP",
        Identifier: "Adult Immunization Schedule (current year)",
        Accessed: new DateOnly(2026, 5, 23),
        Url: "https://www.cdc.gov/vaccines/hcp/imz-schedules/adult-age.html");

    private static readonly TimeSpan OneYear = TimeSpan.FromDays(365);
    private static readonly TimeSpan TenYears = TimeSpan.FromDays(3650);

    /// <summary>Static rule pack — registered by <see cref="Engine.RegisterPack"/>.</summary>
    public static IEnumerable<Rule> Rules => new[]
    {
        new Rule(
            Id: "immunization.gap",
            Description: "Missing or overdue adult immunization per ACIP schedule.",
            Evaluate: Evaluate,
            Citations: new[] { AcipAdultSchedule }),
    };

    /// <summary>Adult vaccine recommendations (vaccine, min age, recurrence). Order = surfacing priority when multiple gaps exist.</summary>
    private static readonly IReadOnlyList<Recommendation> Schedule = new[]
    {
        // Annual influenza for everyone 6 months and older.
        new Recommendation("influenza", MinAgeYears: 0, Recurrence: OneYear, DisplayName: "Influenza"),
        // Tdap booster every 10 years (assumes primary series complete).
        new Recommendation("tdap", MinAgeYears: 18, Recurrence: TenYears, DisplayName: "Tdap"),
        // Zoster (Shingrix) at 50+. Spike simplifies the two-dose Shingrix
        // series to "any dose in the last 10 years" — production must
        // enforce the two-dose primary plus 2–6 month spacing.
        new Recommendation("zoster_recombinant", MinAgeYears: 50, Recurrence: TenYears, DisplayName: "Zoster (Shingrix)"),
        // Pneumococcal at 65+, one-time PCV20 (or PCV15+PPSV23).
        new Recommendation("pneumococcal_pcv20", MinAgeYears: 65, Recurrence: null, DisplayName: "Pneumococcal (PCV20)"),
        // COVID-19 booster annually for everyone 6m+.
        new Recommendation("covid19", MinAgeYears: 0, Recurrence: OneYear, DisplayName: "COVID-19"),
    };

    private static Alert? Evaluate(EvaluationContext ctx)
    {
        var ageYears = ctx.Patient.AgeYears;
        var now = DateTimeOffset.UtcNow;

        var gaps = new List<VaccineGap>();
        foreach (var recommendation in Schedule)
        {
            if (ageYears < recommendation.MinAgeYears) continue;

            var last = ctx.LatestImmunization(recommendation.Vaccine);
            if (last is null)
            {
                gaps.Add(new VaccineGap(recommendation, LastAdministered: null, Reason: GapReason.NeverAdministered));
                continue;
            }
            if (recommendation.Recurrence is TimeSpan recurrence
                && last.AdministeredAt + recurrence < now)
            {
                gaps.Add(new VaccineGap(recommendation, LastAdministered: last.AdministeredAt, Reason: GapReason.Overdue));
            }
        }

        if (gaps.Count == 0) return null;

        var because = gaps.Select(BuildGapFact).ToArray();
        var headline = gaps[0];
        var headlinePhrase = headline.Reason == GapReason.NeverAdministered
            ? $"{headline.Recommendation.DisplayName} has never been administered"
            : $"{headline.Recommendation.DisplayName} last given {headline.LastAdministered:yyyy-MM-dd} — overdue";

        var message = gaps.Count == 1
            ? $"Immunization gap: {headlinePhrase}."
            : $"{gaps.Count} immunization gaps; headline: {headlinePhrase}.";

        return new Alert(
            RuleId: "immunization.gap",
            Severity: Severity.Low,
            Message: message,
            Because: because,
            Citations: new[] { AcipAdultSchedule },
            OverrideOptions: new[]
            {
                "patient_declined",
                "documented_contraindication",
                "received_outside_records_unverified",
                "deferred_per_clinical_judgment",
            });
    }

    private static Fact BuildGapFact(VaccineGap gap)
    {
        var parents = new List<Fact> { Fact.Input("age_meets_min", true) };
        if (gap.LastAdministered is DateTimeOffset last)
        {
            parents.Add(Fact.Input("last_dose_at", last.ToString("yyyy-MM-dd")));
        }
        return new Fact(
            Name: $"gap.{gap.Recommendation.Vaccine}",
            Value: gap.Reason == GapReason.NeverAdministered ? "never_administered" : "overdue",
            Unit: null,
            Parents: parents,
            Citations: Array.Empty<Citation>());
    }

    private enum GapReason { NeverAdministered, Overdue }

    private sealed record Recommendation(
        string Vaccine,
        int MinAgeYears,
        TimeSpan? Recurrence,
        string DisplayName);

    private sealed record VaccineGap(
        Recommendation Recommendation,
        DateTimeOffset? LastAdministered,
        GapReason Reason);
}
