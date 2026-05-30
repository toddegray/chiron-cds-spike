using System.Net;
using System.Text;

using Chiron.Cds.Engine.Primitives;
using Chiron.Cds.Web.CdsHooks.Models;
using Chiron.Cds.Web.Mappers;
using Chiron.Cds.Web.SmartLaunch;
using Markdig;

namespace Chiron.Cds.Web.Panel;

/// <summary>
/// Renders the Summary tab's content for the patient Visit Brief — a
/// three-column body of Problem List / Medications / Allergies, Key Labs, and
/// the Clinical Decision Support panel where Chiron's cards surface — wrapped
/// in the shared <see cref="ChartShell"/> (top bar, icon rail, tab strip).
/// </summary>
internal static class EhrChartRenderer
{
    private static readonly MarkdownPipeline Markdown = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    // Display labels for the engine's canonical lab names.
    private static readonly IReadOnlyDictionary<string, string> LabLabels = new Dictionary<string, string>
    {
        ["creatinine"] = "Creatinine",
        ["egfr"] = "eGFR",
        ["hemoglobin_a1c"] = "Hemoglobin A1c",
        ["inr"] = "INR",
    };

    public static string Render(
        string patientId,
        string displayName,
        string ageSex,
        string? dateOfBirth,
        string? mrn,
        EngineInputs inputs,
        IReadOnlyList<CdsCard> cards)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(cards);
        var id = Uri.EscapeDataString(patientId);
        var sb = new StringBuilder();

        sb.Append("<div class=\"chart-title-row\"><h1>Patient Chart</h1></div>");
        sb.Append("<div class=\"grid\">");

        // Column 1 — problems / meds / allergies
        sb.Append("<div class=\"col\">");
        RenderProblems(sb, inputs.Conditions);
        RenderMedications(sb, inputs.Medications, id);
        RenderAllergies(sb, inputs.Allergies);
        sb.Append("</div>");

        // Column 2 — key labs (+ notes placeholder for the next iteration)
        sb.Append("<div class=\"col\">");
        RenderKeyLabs(sb, inputs.Labs);
        sb.Append("</div>");

        // Column 3 — clinical decision support
        sb.Append("<div class=\"col cds-col\">");
        RenderDecisionSupport(sb, cards, id);
        sb.Append("</div>");
        sb.Append("</div>");   // grid

        var header = new ChartShell.Header(patientId, displayName, ageSex, dateOfBirth, mrn);
        return ChartShell.Page(header, ChartShell.Tab.Summary, displayName, sb.ToString(), ContentCss);
    }

    private static void RenderProblems(StringBuilder sb, IReadOnlyList<Condition> conditions)
    {
        // Collapse duplicate codings of the same problem, then order by date so
        // the clinician sees what happened and when — most recent first. Onset
        // is preferred; recorded date is the fallback when onset is absent.
        var problems = conditions.Where(c => c.Active)
            .GroupBy(c => PatientHeader.Humanize(c.Name), StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var onset = g.Select(c => c.Onset).FirstOrDefault(o => o is not null);
                var recorded = g.Select(c => c.RecordedDate).FirstOrDefault(r => r is not null);
                return (Name: g.Key, Date: onset ?? recorded, Label: ProblemDateLabel(onset, recorded));
            })
            .OrderByDescending(p => p.Date ?? DateTimeOffset.MinValue)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        OpenCard(sb, "Problem List", problems.Length);
        if (problems.Length == 0)
        {
            EmptyRow(sb, "No active problems on file.");
        }
        else
        {
            sb.Append("<ul class=\"list\">");
            foreach (var p in problems)
            {
                sb.Append("<li class=\"list-row\"><span class=\"dot problem\"></span>")
                  .Append("<span class=\"row-text\">").Append(Enc(p.Name)).Append("</span>");
                if (p.Label is not null)
                    sb.Append("<span class=\"row-date\">").Append(Enc(p.Label)).Append("</span>");
                sb.Append("<span class=\"chevron\">›</span></li>");
            }
            sb.Append("</ul>");
        }
        CloseCard(sb);
    }

    private static string? ProblemDateLabel(DateTimeOffset? onset, DateTimeOffset? recorded) =>
        onset is { } o ? "Onset " + o.ToString("yyyy", System.Globalization.CultureInfo.InvariantCulture)
        : recorded is { } r ? "Recorded " + r.ToString("MMM yyyy", System.Globalization.CultureInfo.InvariantCulture)
        : null;

    private static void RenderMedications(StringBuilder sb, IReadOnlyList<Medication> meds, string id)
    {
        var active = meds.Where(m => m.Active)
            .Select(m => PatientHeader.Humanize(m.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        OpenCard(sb, "Medications", active.Length);
        if (active.Length == 0)
        {
            EmptyRow(sb, "No active medications on file.");
        }
        else
        {
            sb.Append("<ul class=\"list\">");
            foreach (var name in active)
            {
                sb.Append("<li class=\"list-row\"><span class=\"dot med\"></span><span class=\"row-text\">")
                  .Append(Enc(name)).Append("</span>")
                  .Append("<a class=\"row-action\" href=\"/app/patient/").Append(id).Append("/orders\">Add order</a>")
                  .Append("<span class=\"chevron\">›</span></li>");
            }
            sb.Append("</ul>");
        }
        CloseCard(sb);
    }

    private static void RenderAllergies(StringBuilder sb, IReadOnlyList<Allergy> allergies)
    {
        var active = allergies.Where(a => a.Active)
            .Select(a => PatientHeader.Humanize(a.Substance))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        OpenCard(sb, "Allergies", active.Length);
        if (active.Length == 0)
        {
            EmptyRow(sb, "No known allergies.");
        }
        else
        {
            sb.Append("<ul class=\"list\">");
            foreach (var name in active)
            {
                sb.Append("<li class=\"list-row\"><span class=\"dot allergy\"></span><span class=\"row-text\">")
                  .Append(Enc(name)).Append("</span><span class=\"chevron\">›</span></li>");
            }
            sb.Append("</ul>");
        }
        CloseCard(sb);
    }

    private static void RenderKeyLabs(StringBuilder sb, IReadOnlyList<Lab> labs)
    {
        // One row per distinct lab showing its most recent value — the only
        // value worth a textual headline. Labs with history collapse into an
        // expandable list of every reading by date (no JS; native details).
        var groups = labs
            .GroupBy(l => l.Name)
            .Select(g => (
                Label: LabLabels.TryGetValue(g.Key, out var l) ? l : PatientHeader.Humanize(g.Key),
                Series: (IReadOnlyList<Lab>)g.OrderByDescending(x => x.TakenAt ?? DateTimeOffset.MinValue).ToArray()))
            .OrderBy(x => x.Label, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        OpenCard(sb, "Key Labs", groups.Length);
        if (groups.Length == 0)
        {
            EmptyRow(sb, "No decision-relevant labs on file. See Labs & Results for the full panel.");
        }
        else
        {
            foreach (var (label, series) in groups) RenderLabGroup(sb, label, series);
            sb.Append("<a class=\"card-foot-link\" href=\"results\">View full results &amp; trends →</a>");
        }
        CloseCard(sb);
    }

    private static void RenderLabGroup(StringBuilder sb, string label, IReadOnlyList<Lab> series)
    {
        var latest = series[0];
        if (series.Count == 1)
        {
            sb.Append("<div class=\"lab-row\">");
            RenderLabHead(sb, label, latest, readingCount: 1);
            sb.Append("</div>");
            return;
        }

        sb.Append("<details class=\"lab-detail\"><summary class=\"lab-row\">");
        RenderLabHead(sb, label, latest, readingCount: series.Count);
        sb.Append("</summary><table class=\"lab-history\">");
        foreach (var pt in series)
        {
            sb.Append("<tr><td class=\"lh-date\">")
              .Append(Enc(pt.TakenAt is { } w ? w.ToString("MM/dd/yyyy") : "—"))
              .Append("</td><td class=\"lh-val\">").Append(Enc(FormatValue(pt.Value)));
            if (!string.IsNullOrWhiteSpace(pt.Unit)) sb.Append(' ').Append(Enc(pt.Unit));
            sb.Append("</td></tr>");
        }
        sb.Append("</table></details>");
    }

    private static void RenderLabHead(StringBuilder sb, string label, Lab latest, int readingCount)
    {
        sb.Append("<span class=\"lab-name\">").Append(Enc(label)).Append("</span>");
        sb.Append("<span class=\"lab-value\">").Append(Enc(FormatValue(latest.Value)));
        if (!string.IsNullOrWhiteSpace(latest.Unit))
            sb.Append(" <span class=\"lab-unit\">").Append(Enc(latest.Unit)).Append("</span>");
        sb.Append("</span>");
        if (latest.TakenAt is { } when)
            sb.Append("<span class=\"lab-when\">").Append(Enc(when.ToString("MM/dd/yyyy"))).Append("</span>");
        if (readingCount > 1)
            sb.Append("<span class=\"lab-count\">").Append(readingCount).Append(" readings ›</span>");
    }

    private static void RenderDecisionSupport(StringBuilder sb, IReadOnlyList<CdsCard> cards, string id)
    {
        sb.Append("<section class=\"cds-panel\">");
        sb.Append("<header class=\"cds-head\">Clinical Decision Support <span class=\"cds-count\">")
          .Append(cards.Count).Append("</span></header>");

        if (cards.Count == 0)
        {
            sb.Append("<div class=\"cds-empty\"><span class=\"cds-ok\">✓</span> No open recommendations. ");
            sb.Append("Every applicable rule was evaluated against this chart.</div>");
        }
        else
        {
            var n = 0;
            foreach (var card in cards) RenderCdsCard(sb, card, ++n, id);
        }
        sb.Append("</section>");
    }

    private static void RenderCdsCard(StringBuilder sb, CdsCard card, int number, string id)
    {
        var (sev, glyph) = card.Indicator switch
        {
            "critical" => ("critical", "⚠"),
            "warning" => ("warning", "⚠"),
            _ => ("info", "💡"),
        };
        sb.Append("<article class=\"cds-card ").Append(sev).Append("\">");
        sb.Append("<header class=\"cds-card-head\">");
        sb.Append("<span class=\"cds-num\">").Append(number).Append("</span>");
        sb.Append("<span class=\"cds-title\">").Append(Enc(card.Summary)).Append("</span>");
        sb.Append("<span class=\"cds-glyph ").Append(sev).Append("\">").Append(glyph).Append("</span>");
        sb.Append("</header>");

        if (!string.IsNullOrWhiteSpace(card.Detail))
        {
            sb.Append("<div class=\"cds-detail\">")
              .Append(Markdig.Markdown.ToHtml(card.Detail, Markdown))
              .Append("</div>");
        }

        sb.Append("<div class=\"cds-actions\">");
        sb.Append("<a class=\"btn btn-primary\" href=\"/app/patient/").Append(id).Append("/orders\">Add order</a>");
        if (!string.IsNullOrWhiteSpace(card.Source.Label))
            sb.Append("<span class=\"cds-source\">").Append(Enc(card.Source.Label)).Append("</span>");
        sb.Append("</div>");
        sb.Append("</article>");
    }

    private static void OpenCard(StringBuilder sb, string title, int count)
    {
        sb.Append("<section class=\"panel\"><header class=\"panel-head\"><h2>").Append(Enc(title)).Append("</h2>");
        if (count > 0) sb.Append("<span class=\"panel-count\">").Append(count).Append("</span>");
        sb.Append("</header>");
    }

    private static void CloseCard(StringBuilder sb) => sb.Append("</section>");

    private static void EmptyRow(StringBuilder sb, string text) =>
        sb.Append("<div class=\"empty-row\">").Append(Enc(text)).Append("</div>");

    private static string FormatValue(double v) =>
        v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    private static string Enc(string s) => WebUtility.HtmlEncode(s);

    // Content-only styles; the shell chrome (vars, body, topbar, rail, tabs) lives in ChartShell.
    private const string ContentCss = @"
        .grid { display: grid; grid-template-columns: minmax(0,1fr) minmax(0,1fr) minmax(0,1.1fr); gap: 1rem; align-items: start; }
        .col { display: flex; flex-direction: column; gap: 1rem; min-width: 0; }

        .panel { background: var(--surface); border: 1px solid var(--rule); border-radius: 12px;
                 box-shadow: 0 1px 2px rgba(16,40,80,.04); overflow: hidden; }
        .panel-head { display: flex; align-items: center; gap: .5rem; padding: .8rem 1rem .5rem; }
        .panel-head h2 { font-size: 1rem; font-weight: 700; margin: 0; letter-spacing: -.01em; }
        .panel-count { font-size: .72rem; font-weight: 700; color: var(--ink-muted);
                       background: var(--bg); border-radius: 999px; padding: .05rem .5rem; }
        .empty-row { padding: .25rem 1rem 1rem; color: var(--ink-muted); font-size: .85rem; }

        .list { list-style: none; margin: 0; padding: .15rem 0 .5rem; }
        .list-row { display: flex; align-items: center; gap: .6rem; padding: .55rem 1rem;
                    border-top: 1px solid var(--rule); }
        .list-row:first-child { border-top: 0; }
        .dot { width: 9px; height: 9px; border-radius: 50%; flex-shrink: 0; }
        .dot.problem { background: #b1455b; } .dot.med { background: #2f7bcc; } .dot.allergy { background: var(--crit); }
        .row-text { flex: 1; min-width: 0; font-weight: 500; }
        .row-action { font-size: .72rem; font-weight: 700; color: var(--accent); text-decoration: none;
                      border: 1px solid #c5dcf5; border-radius: 6px; padding: .12rem .5rem; white-space: nowrap; }
        .row-action:hover { background: var(--info-soft); }
        .row-date { font-size: .76rem; color: var(--ink-muted); white-space: nowrap; }
        .chevron { color: var(--ink-muted); font-size: 1.1rem; }

        .lab-row { display: flex; align-items: baseline; gap: .6rem; padding: .5rem 1rem;
                   border-top: 1px solid var(--rule); }
        .lab-row:first-child, .lab-detail:first-child .lab-row { border-top: 0; }
        .lab-name { flex: 1; min-width: 0; font-weight: 500; }
        .lab-value { font-weight: 700; font-size: 1.05rem; letter-spacing: -.01em; }
        .lab-unit { font-weight: 500; font-size: .78rem; color: var(--ink-muted); }
        .lab-when { font-size: .76rem; color: var(--ink-muted); white-space: nowrap; }
        .lab-count { font-size: .72rem; font-weight: 700; color: var(--accent); white-space: nowrap; }
        .lab-detail summary { cursor: pointer; list-style: none; }
        .lab-detail summary::-webkit-details-marker { display: none; }
        .lab-detail summary:hover { background: var(--bg); }
        .lab-detail[open] > summary { background: var(--bg); }
        .lab-history { width: 100%; border-collapse: collapse; background: var(--bg); }
        .lab-history td { padding: .3rem 1rem; font-size: .82rem; border-top: 1px solid var(--rule); }
        .lab-history .lh-date { color: var(--ink-muted); }
        .lab-history .lh-val { text-align: right; font-weight: 600; }
        .card-foot-link { display: block; padding: .55rem 1rem .8rem; font-size: .8rem; font-weight: 600;
                          color: var(--accent); text-decoration: none; }
        .card-foot-link:hover { text-decoration: underline; }

        /* ---- Clinical Decision Support ---- */
        .cds-panel { background: var(--surface); border: 1px solid var(--rule); border-radius: 12px;
                     box-shadow: 0 1px 2px rgba(16,40,80,.04); overflow: hidden; }
        .cds-head { background: var(--navy-2); color: #fff; padding: .75rem 1rem; font-size: .82rem;
                    font-weight: 700; letter-spacing: .05em; text-transform: uppercase;
                    display: flex; align-items: center; gap: .5rem; }
        .cds-count { margin-left: auto; background: rgba(255,255,255,.18); border-radius: 999px;
                     padding: .05rem .55rem; font-size: .78rem; }
        .cds-empty { padding: 1.5rem 1rem; color: var(--ink-soft); font-size: .9rem; }
        .cds-ok { color: var(--ok); font-weight: 700; margin-right: .25rem; }

        .cds-card { padding: .85rem 1rem; border-top: 1px solid var(--rule); border-left: 3px solid var(--info); }
        .cds-card.warning { border-left-color: var(--warn); }
        .cds-card.critical { border-left-color: var(--crit); }
        .cds-card-head { display: flex; align-items: flex-start; gap: .5rem; }
        .cds-num { font-weight: 700; color: var(--ink-muted); font-size: .85rem; }
        .cds-title { flex: 1; font-weight: 700; font-size: .92rem; line-height: 1.35; }
        .cds-glyph { font-size: .95rem; } .cds-glyph.info { color: var(--info); }
        .cds-glyph.warning { color: var(--warn); } .cds-glyph.critical { color: var(--crit); }
        .cds-detail { font-size: .85rem; color: var(--ink-soft); margin: .4rem 0 .1rem; padding-left: 1.4rem; }
        .cds-detail ul { margin: .25rem 0; padding-left: 1.1rem; } .cds-detail p { margin: .3rem 0; }
        .cds-detail h3 { font-size: .72rem; text-transform: uppercase; letter-spacing: .04em;
                         color: var(--ink-muted); margin: .5rem 0 .15rem; }
        .cds-detail code { background: var(--bg); padding: .03rem .3rem; border-radius: 4px;
                           font-family: ui-monospace, Menlo, monospace; font-size: .85em; }
        .cds-actions { display: flex; align-items: center; gap: .6rem; margin-top: .55rem; padding-left: 1.4rem; }
        .btn { font-size: .78rem; font-weight: 700; text-decoration: none; border-radius: 7px;
               padding: .32rem .7rem; cursor: pointer; }
        .btn-primary { background: var(--accent); color: #fff; }
        .btn-primary:hover { background: #195fbb; }
        .cds-source { font-size: .72rem; color: var(--ink-muted); }

        @media (max-width: 1000px) { .grid { grid-template-columns: 1fr; } }
    ";
}
