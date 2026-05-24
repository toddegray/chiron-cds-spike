using Chiron.Cds.Engine.Primitives;

namespace Chiron.Cds.Engine.Rules.Interactions;

/// <summary>
/// Drug-allergy collision: an active medication matches one of the
/// patient's documented allergies, either by exact substance or by drug
/// class (e.g. patient allergic to penicillin and amoxicillin is on the
/// active med list — cross-reactivity in class).
/// </summary>
/// <remarks>
/// In production, the substance ↔ class mapping would come from a
/// licensed drug-knowledge vendor (First Databank, Lexicomp, Multum) with
/// the cross-reactivity matrix updated as the literature evolves. The
/// spike uses a hand-rolled drug-class table — the same table appears on
/// the FHIR mapper side so the engine and mapper agree without the
/// engine depending on the Web project.
/// </remarks>
[RulePack(Name = "interactions")]
public static class DrugAllergyRule
{
    private static readonly Citation IsmpDrugAllergyAlert = new(
        Source: "ISMP Safe Practice Guidelines",
        Identifier: "Drug-Allergy Cross-Sensitivity (illustrative; production would license FDB/Lexicomp/Multum)",
        Accessed: new DateOnly(2026, 5, 22));

    /// <summary>Static rule pack — registered by <see cref="Engine.RegisterPack"/>.</summary>
    public static IEnumerable<Rule> Rules => new[]
    {
        new Rule(
            Id: "drug.allergy.collision",
            Description: "Active medication conflicts with a documented patient allergy.",
            Evaluate: Evaluate,
            Citations: new[] { IsmpDrugAllergyAlert }),
    };

    /// <summary>
    /// Substance → drug class lookup. Single source of truth used both by
    /// this rule (via <see cref="EvaluationContext"/>) and by the FHIR
    /// mapper (which imports this table to classify
    /// <c>AllergyIntolerance</c> entries and active medications). Production
    /// would license this from FDB / Lexicomp / Multum; the hand-rolled
    /// spike table covers the meds Cerner's sandbox patients carry.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> KnownDrugClasses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        // Penicillin family
        ["penicillin"] = "penicillin",
        ["amoxicillin"] = "penicillin",
        ["ampicillin"] = "penicillin",
        ["dicloxacillin"] = "penicillin",
        ["nafcillin"] = "penicillin",
        ["oxacillin"] = "penicillin",
        ["piperacillin"] = "penicillin",
        ["ticarcillin"] = "penicillin",
        ["augmentin"] = "penicillin",
        ["unasyn"] = "penicillin",
        ["zosyn"] = "penicillin",
        // Cephalosporins (cross-reactive with penicillin allergy in some cases)
        ["cephalexin"] = "cephalosporin",
        ["cefazolin"] = "cephalosporin",
        ["cefuroxime"] = "cephalosporin",
        ["cefdinir"] = "cephalosporin",
        ["cefepime"] = "cephalosporin",
        ["ceftriaxone"] = "cephalosporin",
        // Sulfa
        ["sulfamethoxazole"] = "sulfa",
        ["bactrim"] = "sulfa",
        ["septra"] = "sulfa",
        ["sulfadiazine"] = "sulfa",
        // NSAIDs
        ["ibuprofen"] = "nsaid",
        ["naproxen"] = "nsaid",
        ["ketorolac"] = "nsaid",
        ["diclofenac"] = "nsaid",
        ["indomethacin"] = "nsaid",
        ["celecoxib"] = "nsaid",
        ["meloxicam"] = "nsaid",
        ["aspirin"] = "nsaid",
        // Opioids
        ["morphine"] = "opioid",
        ["oxycodone"] = "opioid",
        ["hydrocodone"] = "opioid",
        ["codeine"] = "opioid",
        ["fentanyl"] = "opioid",
        ["tramadol"] = "opioid",
        // Statins
        ["atorvastatin"] = "statin",
        ["simvastatin"] = "statin",
        ["rosuvastatin"] = "statin",
        ["pravastatin"] = "statin",
        ["lovastatin"] = "statin",
        // ACE-I
        ["lisinopril"] = "ace_inhibitor",
        ["enalapril"] = "ace_inhibitor",
        ["ramipril"] = "ace_inhibitor",
        ["captopril"] = "ace_inhibitor",
        ["benazepril"] = "ace_inhibitor",
        ["quinapril"] = "ace_inhibitor",
        ["fosinopril"] = "ace_inhibitor",
        // ARBs (angiotensin receptor blockers)
        ["losartan"] = "arb",
        ["valsartan"] = "arb",
        ["irbesartan"] = "arb",
        ["olmesartan"] = "arb",
        ["telmisartan"] = "arb",
        ["candesartan"] = "arb",
        // Beta blockers
        ["metoprolol"] = "beta_blocker",
        ["atenolol"] = "beta_blocker",
        ["carvedilol"] = "beta_blocker",
        ["bisoprolol"] = "beta_blocker",
        ["propranolol"] = "beta_blocker",
        ["labetalol"] = "beta_blocker",
        // Calcium channel blockers
        ["amlodipine"] = "ccb",
        ["nifedipine"] = "ccb",
        ["diltiazem"] = "ccb",
        ["verapamil"] = "ccb",
        // Thiazide diuretics (treat as BP medication)
        ["hydrochlorothiazide"] = "thiazide",
        ["chlorthalidone"] = "thiazide",
        ["chlorothiazide"] = "thiazide",
        ["indapamide"] = "thiazide",
    };

    /// <summary>Drug classes that count as antihypertensive therapy for risk-score rules.</summary>
    public static readonly IReadOnlySet<string> AntihypertensiveClasses = new HashSet<string>(StringComparer.Ordinal)
    {
        "ace_inhibitor", "arb", "beta_blocker", "ccb", "thiazide",
    };

    private static Alert? Evaluate(EvaluationContext ctx)
    {
        if (ctx.Allergies.Count == 0) return null;
        if (ctx.Medications.Count == 0) return null;

        var collisions = new List<DrugAllergyCollision>();
        foreach (var med in ctx.Medications.Where(m => m.Active))
        {
            var exactAllergy = ctx.Allergy(med.Name);
            if (exactAllergy is not null)
            {
                collisions.Add(new DrugAllergyCollision(med, exactAllergy, MatchKind.Exact));
                continue;
            }
            var medClass = KnownDrugClasses.GetValueOrDefault(med.Name);
            if (medClass is not null)
            {
                var classAllergy = ctx.Allergies
                    .FirstOrDefault(a => a.Active && string.Equals(a.Class, medClass, StringComparison.OrdinalIgnoreCase));
                if (classAllergy is not null)
                {
                    collisions.Add(new DrugAllergyCollision(med, classAllergy, MatchKind.Class));
                }
            }
        }

        if (collisions.Count == 0) return null;

        // Headline collision: prefer exact-match over class-match so the alert
        // message reflects the strongest documented evidence first.
        var headline = collisions.FirstOrDefault(c => c.Kind == MatchKind.Exact) ?? collisions[0];
        var anyCritical = collisions.Any(c => c.Allergy.Critical);

        var because = collisions.Select(BuildCollisionFact).ToArray();

        var reactionSuffix = string.IsNullOrEmpty(headline.Allergy.Reaction)
            ? string.Empty
            : $" (documented reaction: {headline.Allergy.Reaction})";
        var matchPhrase = headline.Kind == MatchKind.Exact
            ? $"\"{headline.Medication.Name}\""
            : $"\"{headline.Medication.Name}\" (cross-reactive with the {headline.Allergy.Class} class)";

        var message = collisions.Count == 1
            ? $"Active medication {matchPhrase} collides with a documented {headline.Allergy.Substance} allergy{reactionSuffix}."
            : $"{collisions.Count} active medications collide with documented allergies; headline: {matchPhrase} vs {headline.Allergy.Substance}{reactionSuffix}.";

        return new Alert(
            RuleId: "drug.allergy.collision",
            Severity: anyCritical ? Severity.Critical : Severity.High,
            Message: message,
            Because: because,
            Citations: new[] { IsmpDrugAllergyAlert },
            OverrideOptions: new[]
            {
                "documented_tolerance_at_lower_dose",
                "allergy_history_unconfirmed_or_outdated",
                "benefit_outweighs_risk_per_specialist",
                "patient_premedicated_per_protocol",
            });
    }

    private static Fact BuildCollisionFact(DrugAllergyCollision collision)
    {
        var medFact = Fact.Input($"medication.{collision.Medication.Name.ToLowerInvariant()}", true);
        var allergyFact = Fact.Input(
            $"allergy.{collision.Allergy.Substance.ToLowerInvariant()}",
            collision.Allergy.Critical ? "critical" : "active");
        var name = collision.Kind == MatchKind.Exact
            ? "collision.exact"
            : "collision.cross_reactive_class";
        return new Fact(
            Name: name,
            Value: $"{collision.Medication.Name} ↔ {collision.Allergy.Substance}",
            Unit: null,
            Parents: new[] { medFact, allergyFact },
            Citations: Array.Empty<Citation>());
    }

    private enum MatchKind { Exact, Class }

    private sealed record DrugAllergyCollision(
        Medication Medication,
        Allergy Allergy,
        MatchKind Kind);
}
