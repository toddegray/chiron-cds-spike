using System.Collections.Concurrent;

using Chiron.Cds.Engine.Primitives;

namespace Chiron.Cds.Engine;

/// <summary>
/// Tracks when alerts fire and when clinicians override them. Keyed by
/// <see cref="Alert.Fingerprint"/> so a single alert's fatigue history
/// is queryable across all the patients on which it fired. Two
/// implementations: <see cref="InMemoryOverrideLog"/> for tests and the
/// development demo, and the SQLite-backed implementation in the web
/// project for production durability.
/// </summary>
public interface IOverrideLog
{
    void RecordFire(Alert alert);
    void RecordOverride(string fingerprint, string overriddenBy, string? reason = null);
    /// <summary>Override-fatigue summary; rows ordered by override rate descending.</summary>
    IReadOnlyList<FatigueRow> FatigueReport();
}

/// <summary>
/// In-memory implementation. Suitable for tests, dev, and single-process
/// demo — resets on restart. Use the SQLite-backed implementation in
/// production.
/// </summary>
public sealed class InMemoryOverrideLog : IOverrideLog
{
    private readonly ConcurrentDictionary<string, FingerprintStats> _stats = new();

    public void RecordFire(Alert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);
        _stats.AddOrUpdate(alert.Fingerprint,
            _ => new FingerprintStats(alert.RuleId, Fires: 1, Overrides: 0),
            (_, existing) => existing with { Fires = existing.Fires + 1 });
    }

    public void RecordOverride(string fingerprint, string overriddenBy, string? reason = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(overriddenBy);
        _stats.AddOrUpdate(fingerprint,
            _ => new FingerprintStats(RuleId: "<unknown>", Fires: 0, Overrides: 1),
            (_, existing) => existing with { Overrides = existing.Overrides + 1 });
    }

    public IReadOnlyList<FatigueRow> FatigueReport() =>
        _stats
            .Select(kv => new FatigueRow(
                Fingerprint: kv.Key,
                RuleId: kv.Value.RuleId,
                Fires: kv.Value.Fires,
                Overrides: kv.Value.Overrides,
                OverrideRate: kv.Value.Fires == 0 ? 0 : (double)kv.Value.Overrides / kv.Value.Fires))
            .OrderByDescending(r => r.OverrideRate)
            .ThenBy(r => r.Fingerprint, StringComparer.Ordinal)
            .ToArray();

    private sealed record FingerprintStats(string RuleId, int Fires, int Overrides);
}

/// <summary>One row in a fatigue report. <see cref="OverrideRate"/> is the spike's signal.</summary>
public sealed record FatigueRow(
    string Fingerprint,
    string RuleId,
    int Fires,
    int Overrides,
    double OverrideRate);
