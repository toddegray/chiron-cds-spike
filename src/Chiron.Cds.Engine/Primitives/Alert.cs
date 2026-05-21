using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Generic;
using System.Linq;

namespace Chiron.Cds.Engine.Primitives;

/// <summary>
/// A clinical decision-support alert produced by a rule. Carries the full
/// derivation graph (<see cref="Because"/>), its citations, its override
/// options, and a stable <see cref="Fingerprint"/> that the override log
/// uses to track alert fatigue.
/// </summary>
public sealed record Alert(
    string RuleId,
    Severity Severity,
    string Message,
    IReadOnlyList<Fact> Because,
    IReadOnlyList<Citation> Citations,
    IReadOnlyList<string> OverrideOptions)
{
    private string? _fingerprint;

    /// <summary>
    /// Stable 16-character hash over the rule id, severity, and the sorted
    /// fingerprints of the alert's <see cref="Because"/> facts. Identical
    /// across runs and across processes for the same inputs.
    /// </summary>
    public string Fingerprint => _fingerprint ??= ComputeFingerprint();

    private string ComputeFingerprint()
    {
        var sb = new StringBuilder();
        sb.Append("Alert{");
        sb.Append("rule=").Append(RuleId);
        sb.Append(";severity=").Append(Severity.ToString().ToLower(CultureInfo.InvariantCulture));
        sb.Append(";because=[");
        var prints = Because.Select(f => f.Fingerprint).OrderBy(s => s, StringComparer.Ordinal);
        sb.Append(string.Join(",", prints));
        sb.Append("]}");
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexStringLower(bytes.AsSpan(0, 8));
    }

    /// <summary>
    /// Render this alert as multi-line clinician-readable text. The shape
    /// mirrors the Python/TS engines so a CDS Hooks card can drop this
    /// directly into its <c>detail</c> Markdown field.
    /// </summary>
    public string Explain()
    {
        var sb = new StringBuilder();
        sb.Append("ALERT [").Append(Severity.ToString().ToUpperInvariant()).Append("]: ").AppendLine(Message);
        sb.AppendLine();
        sb.AppendLine("DERIVATION:");
        sb.Append("  rule: ").AppendLine(RuleId);
        for (int i = 0; i < Because.Count; i++)
        {
            var isLast = i == Because.Count - 1;
            RenderFact(sb, Because[i], indent: "  ", isLast: isLast);
        }
        sb.AppendLine();
        if (Citations.Count > 0)
        {
            sb.AppendLine("CITATIONS:");
            foreach (var c in Citations)
            {
                sb.Append("  • ").Append(c.Source).Append(", ").Append(c.Identifier)
                  .Append(" (accessed ").Append(c.Accessed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).AppendLine(")");
            }
            sb.AppendLine();
        }
        sb.AppendLine("OVERRIDE OPTIONS:");
        if (OverrideOptions.Count == 0)
        {
            sb.AppendLine("  (no overrides)");
        }
        else
        {
            foreach (var o in OverrideOptions) sb.Append("  • ").AppendLine(o);
        }
        sb.AppendLine();
        sb.Append("FINGERPRINT: ").Append(Fingerprint);
        return sb.ToString();
    }

    private static void RenderFact(StringBuilder sb, Fact fact, string indent, bool isLast)
    {
        var connector = isLast ? "└─ " : "├─ ";
        sb.Append(indent).Append(connector).Append(fact.Name);
        if (fact.Value is not null) sb.Append(" = ").Append(FormatValue(fact.Value));
        if (!string.IsNullOrEmpty(fact.Unit)) sb.Append(' ').Append(fact.Unit);
        sb.AppendLine();
        var childIndent = indent + (isLast ? "   " : "│  ");
        for (int i = 0; i < fact.Parents.Count; i++)
        {
            RenderFact(sb, fact.Parents[i], childIndent, i == fact.Parents.Count - 1);
        }
    }

    private static string FormatValue(object value) => value switch
    {
        bool b => b ? "true" : "false",
        double d => d.ToString("0.####", CultureInfo.InvariantCulture),
        float f => ((double)f).ToString("0.####", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };
}
