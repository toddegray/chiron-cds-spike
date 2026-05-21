using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Generic;
using System.Linq;

namespace Chiron.Cds.Engine.Primitives;

/// <summary>
/// A frozen value with provenance. Every rule produces alerts whose
/// <see cref="Alert.Because"/> tree is built entirely of <c>Fact</c>s, so
/// the derivation that fired an alert can be walked back to the input
/// resources that supported it.
/// </summary>
/// <param name="Name">The fact's identifier (e.g. <c>creatinine</c>, <c>egfr_ckd_epi</c>).</param>
/// <param name="Value">The fact's value. <see cref="double"/>, <see cref="bool"/>, and <see cref="string"/> are the supported primitives.</param>
/// <param name="Unit">Optional unit string (e.g. <c>mg/dL</c>).</param>
/// <param name="Parents">Upstream facts this fact was derived from. Empty for input facts.</param>
/// <param name="Citations">Citations that justify the derivation rule that produced this fact. Empty for input facts.</param>
/// <param name="ObservedAt">When this fact was observed or derived.</param>
public sealed record Fact(
    string Name,
    object Value,
    string? Unit,
    IReadOnlyList<Fact> Parents,
    IReadOnlyList<Citation> Citations,
    DateTimeOffset? ObservedAt = null)
{
    /// <summary>
    /// Convenience for input facts (no parents, no citations).
    /// </summary>
    public static Fact Input(string name, object value, string? unit = null, DateTimeOffset? observedAt = null) =>
        new(name, value, unit, Array.Empty<Fact>(), Array.Empty<Citation>(), observedAt);

    private string? _fingerprint;

    /// <summary>
    /// A stable 16-character lowercase hex hash of this fact's name, value, unit,
    /// and the sorted fingerprints of its parents. Two facts produced from the
    /// same inputs always produce the same fingerprint, in this run and any
    /// other process running the same engine.
    /// </summary>
    public string Fingerprint => _fingerprint ??= ComputeFingerprint();

    private string ComputeFingerprint()
    {
        var canonical = new StringBuilder();
        canonical.Append("Fact{");
        canonical.Append("name=").Append(Name);
        canonical.Append(";value=").Append(CanonicalizeValue(Value));
        canonical.Append(";unit=").Append(Unit ?? "");
        canonical.Append(";parents=[");
        var parentPrints = Parents.Select(p => p.Fingerprint).OrderBy(s => s, StringComparer.Ordinal);
        canonical.Append(string.Join(",", parentPrints));
        canonical.Append("]}");
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));
        // Take the first 8 bytes -> 16 hex chars. Matches the traceable-cds
        // fingerprint width so cross-language fixtures stay short.
        return Convert.ToHexStringLower(bytes.AsSpan(0, 8));
    }

    private static string CanonicalizeValue(object value) => value switch
    {
        null => "null",
        bool b => b ? "true" : "false",
        string s => s,
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        float f => ((double)f).ToString("R", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };
}
