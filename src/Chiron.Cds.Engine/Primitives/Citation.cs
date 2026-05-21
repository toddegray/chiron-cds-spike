namespace Chiron.Cds.Engine.Primitives;

/// <summary>
/// A reference back to the source of truth for a clinical claim. Every rule
/// and every score must produce alerts that carry at least one citation;
/// the engine refuses to register a rule without one.
/// </summary>
/// <param name="Source">Publisher or document family, e.g. "FDA label" or "AHA/ACC/HRS 2019".</param>
/// <param name="Identifier">A specific identifier within that source, e.g. NDA number or DOI.</param>
/// <param name="Accessed">The date the cited document was consulted by the rule's author.</param>
/// <param name="Url">Optional canonical URL.</param>
public sealed record Citation(
    string Source,
    string Identifier,
    DateOnly Accessed,
    string? Url = null);
