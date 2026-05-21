using Chiron.Cds.Engine.Primitives;

namespace Chiron.Cds.Engine.Rules.Interactions;

/// <summary>
/// Warfarin + NSAID drug-drug interaction: increased bleeding risk.
/// </summary>
/// <remarks>
/// In production, the NSAID list would come from a licensed drug-knowledge
/// vendor (First Databank, Lexicomp, Multum). For the spike, a hand-rolled
/// set of generic NSAID names is sufficient — the engine's contract is the
/// derivation graph, not the completeness of the drug table.
/// </remarks>
[RulePack(Name = "interactions")]
public static class WarfarinNsaidRule
{
    private static readonly Citation LexicompMonograph = new(
        Source: "Lexicomp interaction monograph",
        Identifier: "Warfarin-NSAID (illustrative; production would license FDB/Lexicomp/Multum)",
        Accessed: new DateOnly(2026, 4, 29));

    private static readonly HashSet<string> Nsaids = new(StringComparer.OrdinalIgnoreCase)
    {
        "ibuprofen", "naproxen", "ketorolac", "diclofenac", "indomethacin",
        "celecoxib", "meloxicam", "piroxicam", "etodolac", "nabumetone",
        "aspirin", // covers low-dose ASA which is a frequent co-prescription
    };

    /// <summary>Static rule pack — registered by <see cref="Engine.RegisterPack"/>.</summary>
    public static IEnumerable<Rule> Rules => new[]
    {
        new Rule(
            Id: "warfarin.nsaid.bleeding_risk",
            Description: "Concomitant warfarin + NSAID increases bleeding risk.",
            Evaluate: Evaluate,
            Citations: new[] { LexicompMonograph }),
    };

    private static Alert? Evaluate(EvaluationContext ctx)
    {
        if (!ctx.HasMedication("warfarin")) return null;
        var nsaid = ctx.Medications.FirstOrDefault(m => m.Active && Nsaids.Contains(m.Name));
        if (nsaid is null) return null;

        var warfarinFact = Fact.Input("medication.warfarin", true);
        var nsaidFact = Fact.Input($"medication.{nsaid.Name.ToLowerInvariant()}", true);

        return new Alert(
            RuleId: "warfarin.nsaid.bleeding_risk",
            Severity: Severity.High,
            Message: $"Warfarin + {nsaid.Name}: increased bleeding risk — consider alternative analgesic and monitor INR.",
            Because: new[] { warfarinFact, nsaidFact },
            Citations: new[] { LexicompMonograph },
            OverrideOptions: new[]
            {
                "short_course_only",
                "gi_protection_in_place",
                "increased_inr_monitoring",
            });
    }
}
