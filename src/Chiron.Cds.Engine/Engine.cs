using System.Diagnostics;
using System.Reflection;

using Chiron.Cds.Engine.Primitives;

namespace Chiron.Cds.Engine;

/// <summary>
/// The reasoning engine. Holds a set of registered <see cref="Rule"/>s and
/// applies them all to an evaluation. Stateless across calls; one engine
/// instance handles concurrent evaluations safely.
/// </summary>
public sealed class Engine
{
    private readonly Dictionary<string, Rule> _rules = new(StringComparer.Ordinal);

    /// <summary>Registered rules in insertion order.</summary>
    public IReadOnlyCollection<Rule> Rules => _rules.Values;

    /// <summary>
    /// Register a single rule. Throws if the rule has no citations or if
    /// the rule id is already taken.
    /// </summary>
    public Engine RegisterRule(Rule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (string.IsNullOrWhiteSpace(rule.Id))
            throw new ArgumentException("Rule id must be non-empty.", nameof(rule));
        if (rule.Citations.Count == 0)
            throw new ArgumentException(
                $"Rule '{rule.Id}' has no citations. Every clinical claim must cite a source.",
                nameof(rule));
        if (!_rules.TryAdd(rule.Id, rule))
            throw new InvalidOperationException($"Rule '{rule.Id}' is already registered.");
        return this;
    }

    /// <summary>
    /// Discover rule packs in an assembly. A "rule pack" is any public
    /// static class exposing a static <c>IEnumerable&lt;Rule&gt; Rules</c>
    /// property. The convention keeps rule files declarative and avoids
    /// runtime <c>new()</c> calls for stateless rules.
    /// </summary>
    public Engine RegisterPack(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        foreach (var type in assembly.GetExportedTypes())
        {
            if (!type.IsAbstract || !type.IsSealed) continue; // static classes only
            var prop = type.GetProperty("Rules",
                BindingFlags.Public | BindingFlags.Static);
            if (prop is null) continue;
            if (prop.GetValue(null) is not IEnumerable<Rule> packRules) continue;
            foreach (var rule in packRules) RegisterRule(rule);
        }
        return this;
    }

    /// <summary>
    /// Evaluate every registered rule against the inputs. A rule that
    /// throws <see cref="MissingInputException"/> is silently skipped
    /// (no-fire) — that exception type is the rule's contract for
    /// "this input is missing, so I don't apply." All other exceptions
    /// propagate.
    /// </summary>
    public EvaluationResult Evaluate(
        Primitives.Patient patient,
        IEnumerable<Primitives.Medication> medications,
        IEnumerable<Primitives.Lab> labs,
        IEnumerable<Primitives.Condition> conditions,
        IEnumerable<Primitives.Allergy>? allergies = null,
        IEnumerable<Primitives.Immunization>? immunizations = null,
        IEnumerable<Primitives.Procedure>? procedures = null)
    {
        var ctx = new EvaluationContext(patient, medications, labs, conditions, allergies, immunizations, procedures);
        var stopwatch = Stopwatch.StartNew();
        var alerts = new List<Alert>();
        foreach (var rule in _rules.Values)
        {
            try
            {
                var alert = rule.Evaluate(ctx);
                if (alert is not null) alerts.Add(alert);
            }
            catch (MissingInputException)
            {
                // Rule asked for an input that wasn't present. Treat as no-fire.
            }
        }
        stopwatch.Stop();
        return new EvaluationResult(alerts, stopwatch.Elapsed.TotalMilliseconds);
    }
}

/// <summary>
/// Output of an <see cref="Engine.Evaluate"/> call.
/// </summary>
public sealed record EvaluationResult(IReadOnlyList<Alert> Alerts, double ElapsedMs);
