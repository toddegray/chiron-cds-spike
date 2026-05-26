using System.Net;
using System.Text;

namespace Chiron.Cds.Web.Panel;

/// <summary>
/// Renders the left-side vertical workflow rail used on every per-patient
/// chart page. Each step is a numbered link; the active step shows a solid
/// dot, the others are hollow. Pure HTML + inline-class hooks; the renderer
/// that calls into this also supplies the matching CSS.
/// </summary>
internal static class ChartRail
{
    /// <summary>Canonical ordered steps in the visit workflow.</summary>
    public enum Step { Brief, Results, Orders, Notes, SignOff }

    private static readonly IReadOnlyList<(Step Step, string Label, string Slug)> Steps = new[]
    {
        (Step.Brief, "Visit brief", ""),
        (Step.Results, "Results", "/results"),
        (Step.Orders, "Orders", "/orders"),
        (Step.Notes, "Notes", "/notes"),
        (Step.SignOff, "Sign off", "/signoff"),
    };

    private static void Render(StringBuilder sb, string patientId, Step active)
    {
        ArgumentNullException.ThrowIfNull(sb);
        var escapedId = Uri.EscapeDataString(patientId);
        sb.Append("<aside class=\"chart-rail\" aria-label=\"Visit workflow\">");
        sb.Append("<ol class=\"rail-steps\">");
        for (var i = 0; i < Steps.Count; i++)
        {
            var (step, label, slug) = Steps[i];
            var isActive = step == active;
            sb.Append("<li class=\"rail-step");
            if (isActive) sb.Append(" active");
            sb.Append("\"><a href=\"/app/patient/").Append(escapedId).Append(WebUtility.HtmlEncode(slug)).Append("\">");
            sb.Append("<span class=\"rail-num\">").Append(i + 1).Append("</span>");
            sb.Append("<span class=\"rail-label\">").Append(WebUtility.HtmlEncode(label)).Append("</span>");
            sb.Append("</a></li>");
        }
        sb.Append("</ol>");
        sb.Append("</aside>");
    }

    /// <summary>
    /// "Next step" footer link the renderer appends below the main content.
    /// Sign-off's next is back to the panel — the doctor has completed this
    /// visit and is ready for the next patient.
    /// </summary>
    private static void RenderNextLink(StringBuilder sb, string patientId, Step current)
    {
        ArgumentNullException.ThrowIfNull(sb);
        var (href, label) = NextTarget(patientId, current);
        sb.Append("<div class=\"rail-next\"><a href=\"").Append(WebUtility.HtmlEncode(href)).Append("\">");
        sb.Append(WebUtility.HtmlEncode(label)).Append(" →</a></div>");
    }

    private static (string Href, string Label) NextTarget(string patientId, Step current)
    {
        var escapedId = Uri.EscapeDataString(patientId);
        return current switch
        {
            Step.Brief => ($"/app/patient/{escapedId}/results", "Next: Results"),
            Step.Results => ($"/app/patient/{escapedId}/orders", "Next: Orders"),
            Step.Orders => ($"/app/patient/{escapedId}/notes", "Next: Notes"),
            Step.Notes => ($"/app/patient/{escapedId}/signoff", "Next: Sign off"),
            Step.SignOff => ("/app/panel", "Next patient"),
            _ => throw new ArgumentOutOfRangeException(nameof(current)),
        };
    }

    /// <summary>
    /// Open the chart-shell grid (rail in the left column, page main
    /// content in the right). Each per-page renderer must call
    /// <see cref="CloseShell"/> after writing its main content.
    /// </summary>
    public static void OpenShell(StringBuilder sb, string patientId, Step active)
    {
        ArgumentNullException.ThrowIfNull(sb);
        sb.Append("<div class=\"chart-shell\">");
        Render(sb, patientId, active);
    }

    /// <summary>Close the chart-shell and append the next-step link.</summary>
    public static void CloseShell(StringBuilder sb, string patientId, Step active)
    {
        ArgumentNullException.ThrowIfNull(sb);
        RenderNextLink(sb, patientId, active);
        sb.Append("</div>");
    }

    /// <summary>
    /// Inline CSS for the rail and its "next step" footer link. Each
    /// per-page renderer copies this into its own &lt;style&gt; block so
    /// the rail looks identical across the visit workflow without
    /// needing a shared stylesheet asset.
    /// </summary>
    public const string SharedCss = """
        .chart-shell { max-width: 1280px; margin: 1.5rem auto 0; padding: 0 1.5rem;
                       display: grid; grid-template-columns: 220px minmax(0, 1fr); gap: 1.5rem;
                       align-items: start; }
        .chart-shell > main { margin: 0; padding: 0; max-width: none; }
        .chart-rail { position: sticky; top: 1rem; align-self: start; padding: 1rem .5rem; }
        .rail-steps { list-style: none; padding: 0; margin: 0; display: grid; gap: .15rem; }
        .rail-step a { display: flex; align-items: center; gap: .65rem; padding: .55rem .75rem;
                       text-decoration: none; color: var(--ink-soft); border-radius: 10px;
                       transition: background .15s ease, color .15s ease; }
        .rail-step a:hover { background: var(--surface); color: var(--ink); }
        .rail-step.active a { background: var(--ink); color: #fff; }
        .rail-num { width: 1.6rem; height: 1.6rem; border-radius: 50%; background: var(--rule);
                    color: var(--ink-soft); display: inline-flex; align-items: center; justify-content: center;
                    font-size: .8rem; font-weight: 700; }
        .rail-step.active .rail-num { background: var(--info); color: #fff; }
        .rail-step a:hover .rail-num { background: var(--ink-soft); color: #fff; }
        .rail-step.active a:hover .rail-num { background: var(--info); }
        .rail-label { font-size: .92rem; font-weight: 500; }
        .rail-next { grid-column: 2; margin-top: 1.5rem; padding-top: 1rem;
                     border-top: 1px solid var(--rule); text-align: right; }
        .rail-next a { color: var(--info); text-decoration: none; font-weight: 600; font-size: .95rem; }
        .rail-next a:hover { text-decoration: underline; }
        @media (max-width: 880px) {
            .chart-rail { position: static; padding: .25rem 0; }
            .rail-steps { grid-auto-flow: column; grid-auto-columns: minmax(0, 1fr); overflow-x: auto; }
            .rail-next { grid-column: 1; text-align: left; }
        }
        """;
}
