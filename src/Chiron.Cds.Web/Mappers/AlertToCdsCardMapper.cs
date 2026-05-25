using System.Globalization;
using System.Text;

using Chiron.Cds.Engine.Primitives;
using Chiron.Cds.Web.CdsHooks.Models;

namespace Chiron.Cds.Web.Mappers;

/// <summary>
/// Projects engine <see cref="Alert"/>s into CDS Hooks <see cref="CdsCard"/>s.
/// The card's <c>detail</c> field is rendered as Markdown so the EHR's
/// CDS Hooks UI displays the alert's full derivation graph inline.
/// </summary>
public sealed class AlertToCdsCardMapper
{
    private const string ChironSystem = "https://chiron.health/cds";
    private const string ChironName = "Chiron Clinical Reasoning";

    public CdsCard Map(Alert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);

        return new CdsCard(
            Summary: alert.Message,
            Indicator: MapIndicator(alert.Severity),
            Source: new CdsCardSource(
                Label: ChironName,
                Url: "https://chiron.health/cds/" + alert.RuleId),
            Detail: RenderMarkdownDetail(alert),
            Uuid: alert.Fingerprint,
            OverrideReasons: alert.OverrideOptions
                .Select(o => new CdsCoding(
                    Code: o,
                    System: ChironSystem + "/override-reasons",
                    Display: HumanizeOverride(o)))
                .ToArray());
    }

    private static string MapIndicator(Severity s) => s switch
    {
        Severity.Critical => "critical",
        Severity.High => "warning",
        Severity.Medium => "warning",
        Severity.Low => "info",
        Severity.Info => "info",
        _ => "info",
    };

    private static string RenderMarkdownDetail(Alert alert)
    {
        // The card header (rendered by the Visit Brief) already shows the
        // severity badge + title. Rule id + severity + fingerprint duplicate
        // that for clinicians and read as dev-toolbox noise. CDS Hooks JSON
        // consumers still receive them structurally via Indicator + Uuid.
        var sb = new StringBuilder();

        sb.AppendLine("### Derivation");
        sb.AppendLine();
        foreach (var fact in alert.Because)
            RenderFactMarkdown(sb, fact, depth: 0);
        sb.AppendLine();

        if (alert.Citations.Count > 0)
        {
            sb.AppendLine("### Citations");
            sb.AppendLine();
            foreach (var c in alert.Citations)
            {
                sb.Append("- ").Append(c.Source).Append(", ").Append(c.Identifier)
                  .Append(" (accessed ").Append(c.Accessed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(')');
                if (!string.IsNullOrEmpty(c.Url))
                    sb.Append(" — [link](").Append(c.Url).Append(')');
                sb.AppendLine();
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static void RenderFactMarkdown(StringBuilder sb, Fact fact, int depth)
    {
        for (var i = 0; i < depth; i++) sb.Append("  ");
        sb.Append("- **").Append(fact.Name).Append("**");
        if (fact.Value is not null)
            sb.Append(" = `").Append(FormatValue(fact.Value)).Append('`');
        if (!string.IsNullOrEmpty(fact.Unit))
            sb.Append(' ').Append(fact.Unit);
        sb.AppendLine();
        foreach (var parent in fact.Parents)
            RenderFactMarkdown(sb, parent, depth + 1);
    }

    private static string FormatValue(object value) => value switch
    {
        bool b => b ? "true" : "false",
        double d => d.ToString("0.####", CultureInfo.InvariantCulture),
        float f => ((double)f).ToString("0.####", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    private static string HumanizeOverride(string code) =>
        string.Join(' ', code.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
}
