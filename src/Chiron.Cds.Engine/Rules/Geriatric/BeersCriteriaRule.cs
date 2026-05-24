using Chiron.Cds.Engine.Primitives;

namespace Chiron.Cds.Engine.Rules.Geriatric;

/// <summary>
/// 2023 American Geriatrics Society Beers Criteria for Potentially
/// Inappropriate Medication Use in Older Adults (65+). Surfaces active
/// medications that are flagged "avoid" or "use with caution" by class.
/// </summary>
[RulePack(Name = "geriatric")]
public static class BeersCriteriaRule
{
    private static readonly Citation Beers2023 = new(
        Source: "American Geriatrics Society",
        Identifier: "2023 Updated AGS Beers Criteria, J Am Geriatr Soc 2023;71(7):2052-2081",
        Accessed: new DateOnly(2026, 5, 23),
        Url: "https://agsjournals.onlinelibrary.wiley.com/doi/10.1111/jgs.18372");

    /// <summary>Static rule pack — registered by <see cref="Engine.RegisterPack"/>.</summary>
    public static IEnumerable<Rule> Rules => new[]
    {
        new Rule(
            Id: "beers.pim.elderly",
            Description: "Active medication appears on the 2023 AGS Beers Potentially Inappropriate Medications list.",
            Evaluate: Evaluate,
            Citations: new[] { Beers2023 }),
    };

    /// <summary>
    /// Beers entries used by the spike — categories are inline-documented
    /// in the table below. Production would consume AGS's full XLSX
    /// schedule and refresh on each annual update; this hand-rolled set
    /// covers the most clinically impactful PIMs that the Cerner sandbox
    /// patients actually carry.
    /// </summary>
    private static readonly IReadOnlyList<BeersEntry> Entries = new[]
    {
        // Strong anticholinergics + first-gen antihistamines
        new BeersEntry("diphenhydramine", "Strong anticholinergic — sedation, confusion, falls; consider cetirizine or loratadine."),
        new BeersEntry("hydroxyzine", "Strong anticholinergic — sedation, confusion; non-benzodiazepine anxiolytic alternatives preferred."),
        new BeersEntry("chlorpheniramine", "First-generation antihistamine; anticholinergic risk."),
        new BeersEntry("promethazine", "Strong anticholinergic + sedation; ondansetron preferred for nausea."),

        // Skeletal muscle relaxants (most are anticholinergic + sedating)
        new BeersEntry("cyclobenzaprine", "Skeletal muscle relaxant — anticholinergic, sedation, fall risk in elderly."),
        new BeersEntry("methocarbamol", "Skeletal muscle relaxant — sedation, weakness in elderly."),
        new BeersEntry("carisoprodol", "Skeletal muscle relaxant — sedation, dependence."),

        // Benzodiazepines (avoid as a class; long-acting agents especially)
        new BeersEntry("diazepam", "Long-acting benzodiazepine — sedation, falls, cognitive impairment."),
        new BeersEntry("clonazepam", "Long-acting benzodiazepine — falls, cognitive impairment."),
        new BeersEntry("alprazolam", "Benzodiazepine — falls, fractures, cognitive impairment."),
        new BeersEntry("lorazepam", "Benzodiazepine — falls, fractures, cognitive impairment."),
        new BeersEntry("temazepam", "Benzodiazepine — sleep agent, falls in elderly."),

        // Z-drug hypnotics (avoid >90 days)
        new BeersEntry("zolpidem", "Z-drug hypnotic — falls, fractures; sleep hygiene first-line."),
        new BeersEntry("eszopiclone", "Z-drug hypnotic — falls, cognitive effects; sleep hygiene first-line."),
        new BeersEntry("zaleplon", "Z-drug hypnotic — falls, cognitive effects."),

        // Tricyclic antidepressants (strongly anticholinergic)
        new BeersEntry("amitriptyline", "TCA — strong anticholinergic, orthostatic hypotension; SSRI or SNRI preferred."),
        new BeersEntry("nortriptyline", "TCA — anticholinergic, sedation in elderly."),
        new BeersEntry("imipramine", "TCA — anticholinergic, orthostasis."),
        new BeersEntry("doxepin", "TCA — strongly anticholinergic at doses > 6 mg."),

        // Sulfonylureas (long-acting — severe / prolonged hypoglycemia)
        new BeersEntry("glyburide", "Long-acting sulfonylurea — prolonged hypoglycemia in elderly; glipizide or DPP-4/GLP-1 preferred."),
        new BeersEntry("chlorpropamide", "Long-acting sulfonylurea — severe hypoglycemia, SIADH."),

        // Antipsychotics (use only when behavioural management failed)
        new BeersEntry("haloperidol", "Antipsychotic — extrapyramidal effects, increased mortality in dementia."),
        new BeersEntry("risperidone", "Antipsychotic — increased mortality in dementia; non-pharmacologic management first-line."),
        new BeersEntry("quetiapine", "Antipsychotic — increased mortality in dementia; reserve for documented psychotic disorders."),

        // Bladder antimuscarinics (anticholinergic)
        new BeersEntry("oxybutynin", "Antimuscarinic — anticholinergic burden, cognitive effects; behavioural therapy first-line."),
        new BeersEntry("tolterodine", "Antimuscarinic — anticholinergic burden."),

        // Indomethacin & ketorolac (highest GI bleed and CKD risk among NSAIDs)
        new BeersEntry("indomethacin", "NSAID with the highest CNS adverse-effect rate among NSAIDs."),
        new BeersEntry("ketorolac", "NSAID — GI bleed, AKI risk in elderly; short-term use only."),

        // Meperidine (toxic metabolite, especially in renal impairment)
        new BeersEntry("meperidine", "Opioid — neurotoxic metabolite (normeperidine); other opioids preferred."),
    };

    private static readonly IReadOnlyDictionary<string, BeersEntry> EntriesByName =
        Entries.ToDictionary(e => e.Medication, StringComparer.OrdinalIgnoreCase);

    private static Alert? Evaluate(EvaluationContext ctx)
    {
        if (ctx.Patient.AgeYears < 65) return null;

        var hits = ctx.Medications
            .Where(m => m.Active && EntriesByName.ContainsKey(m.Name))
            .Select(m => new { Medication = m, Entry = EntriesByName[m.Name] })
            .ToArray();

        if (hits.Length == 0) return null;

        var ageFact = ctx.PatientFact("age_years");
        var because = hits.Select(h => new Fact(
            Name: $"beers.{h.Medication.Name.ToLowerInvariant()}",
            Value: h.Entry.Rationale,
            Unit: null,
            Parents: new[] { ageFact, Fact.Input($"medication.{h.Medication.Name.ToLowerInvariant()}", true) },
            Citations: Array.Empty<Citation>())).ToArray();

        var medNames = string.Join(", ", hits.Select(h => h.Medication.Name));
        var message = hits.Length == 1
            ? $"Potentially inappropriate medication for age ≥65: {hits[0].Medication.Name}. {hits[0].Entry.Rationale}"
            : $"{hits.Length} potentially inappropriate medications for age ≥65: {medNames}.";

        var severity = hits.Length >= 3 ? Severity.High : Severity.Medium;

        return new Alert(
            RuleId: "beers.pim.elderly",
            Severity: severity,
            Message: message,
            Because: because,
            Citations: new[] { Beers2023 },
            OverrideOptions: new[]
            {
                "documented_benefit_outweighs_risk_in_this_patient",
                "tapering_plan_in_place",
                "alternative_failed_or_contraindicated",
                "short_term_use_only",
            });
    }

    private sealed record BeersEntry(string Medication, string Rationale);
}
