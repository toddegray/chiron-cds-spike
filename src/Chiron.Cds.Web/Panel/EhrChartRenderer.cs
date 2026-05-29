using System.Net;
using System.Text;

using Chiron.Cds.Engine.Primitives;
using Chiron.Cds.Web.CdsHooks.Models;
using Chiron.Cds.Web.Mappers;
using Chiron.Cds.Web.SmartLaunch;
using Markdig;

namespace Chiron.Cds.Web.Panel;

/// <summary>
/// Renders the patient Visit Brief as a clinician-facing EHR summary: a dark
/// patient-identity banner, a left icon rail, a tab strip across the chart
/// sections, and a three-column body — Problem List / Medications / Allergies,
/// Key Labs, and the Clinical Decision Support panel where Chiron's cards
/// surface. Server-rendered HTML, no JavaScript.
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

        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.Append("<title>").Append(Enc(displayName)).Append(" — Chiron</title>");
        sb.Append(Css());
        sb.Append("</head><body>");

        RenderTopBar(sb, displayName, ageSex, dateOfBirth, mrn);

        sb.Append("<div class=\"shell\">");
        RenderIconRail(sb);

        sb.Append("<div class=\"workspace\">");
        RenderTabs(sb, id);

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
        sb.Append("</div>");   // workspace
        sb.Append("</div>");   // shell
        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static void RenderTopBar(StringBuilder sb, string name, string ageSex, string? dob, string? mrn)
    {
        sb.Append("<header class=\"topbar\">");
        sb.Append("<div class=\"brand\"><span class=\"brand-mark\">✚</span> Chiron</div>");
        sb.Append("<div class=\"patient-id\">");
        sb.Append("<div class=\"pid-name\">").Append(Enc(name)).Append("</div>");
        sb.Append("<div class=\"pid-sub\">");
        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(ageSex)) parts.Add(ageSex);
        if (!string.IsNullOrWhiteSpace(dob)) parts.Add("DOB " + dob);
        if (!string.IsNullOrWhiteSpace(mrn)) parts.Add("MRN " + mrn);
        sb.Append(Enc(string.Join("  •  ", parts)));
        sb.Append("</div></div>");
        sb.Append("<div class=\"topbar-meta\">");
        sb.Append("<div>Epic Sandbox · SMART on FHIR</div>");
        sb.Append("<div class=\"meta-dim\">Last updated ")
          .Append(Enc(DateTimeOffset.Now.ToString("MM/dd/yyyy HH:mm")))
          .Append("</div>");
        sb.Append("</div>");
        sb.Append("</header>");
    }

    private static void RenderIconRail(StringBuilder sb)
    {
        sb.Append("<nav class=\"icon-rail\" aria-label=\"Primary\">");
        RailIcon(sb, "/app/panel", "Panel", "☰");
        RailIcon(sb, "/app/search", "Find patient", "⌕");
        RailIcon(sb, "/cds-services", "CDS Hooks", "✚");
        sb.Append("</nav>");
    }

    private static void RailIcon(StringBuilder sb, string href, string label, string glyph)
    {
        sb.Append("<a class=\"rail-icon\" href=\"").Append(Enc(href)).Append("\" title=\"")
          .Append(Enc(label)).Append("\" aria-label=\"").Append(Enc(label)).Append("\">")
          .Append(glyph).Append("</a>");
    }

    private static void RenderTabs(StringBuilder sb, string id)
    {
        sb.Append("<nav class=\"tabs\" aria-label=\"Chart sections\">");
        Tab(sb, $"/app/patient/{id}", "Summary", active: true);
        Tab(sb, $"/app/patient/{id}/results", "Labs & Results", active: false);
        Tab(sb, $"/app/patient/{id}/orders", "Orders", active: false);
        Tab(sb, $"/app/patient/{id}/notes", "Notes", active: false);
        Tab(sb, $"/app/patient/{id}/signoff", "Sign off", active: false);
        sb.Append("</nav>");
    }

    private static void Tab(StringBuilder sb, string href, string label, bool active)
    {
        sb.Append("<a class=\"tab").Append(active ? " active" : "").Append("\" href=\"")
          .Append(Enc(href)).Append("\">").Append(Enc(label)).Append("</a>");
    }

    private static void RenderProblems(StringBuilder sb, IReadOnlyList<Condition> conditions)
    {
        var active = conditions.Where(c => c.Active)
            .Select(c => PatientHeader.Humanize(c.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        OpenCard(sb, "Problem List", active.Length);
        if (active.Length == 0)
        {
            EmptyRow(sb, "No active problems on file.");
        }
        else
        {
            sb.Append("<ul class=\"list\">");
            foreach (var name in active)
            {
                sb.Append("<li class=\"list-row\"><span class=\"dot problem\"></span><span class=\"row-text\">")
                  .Append(Enc(name)).Append("</span><span class=\"chevron\">›</span></li>");
            }
            sb.Append("</ul>");
        }
        CloseCard(sb);
    }

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
        OpenCard(sb, "Key Labs", labs.Count);
        if (labs.Count == 0)
        {
            EmptyRow(sb, "No decision-relevant labs on file. See Labs & Results for the full panel.");
        }
        else
        {
            sb.Append("<ul class=\"lab-list\">");
            foreach (var lab in labs)
            {
                var label = LabLabels.TryGetValue(lab.Name, out var l) ? l : PatientHeader.Humanize(lab.Name);
                sb.Append("<li class=\"lab-row\">");
                sb.Append("<span class=\"lab-name\">").Append(Enc(label)).Append("</span>");
                sb.Append("<span class=\"lab-value\">").Append(Enc(FormatValue(lab.Value)));
                if (!string.IsNullOrWhiteSpace(lab.Unit))
                    sb.Append(" <span class=\"lab-unit\">").Append(Enc(lab.Unit)).Append("</span>");
                sb.Append("</span>");
                if (lab.TakenAt is { } when)
                    sb.Append("<span class=\"lab-when\">").Append(Enc(when.ToString("MM/dd/yyyy"))).Append("</span>");
                sb.Append("</li>");
            }
            sb.Append("</ul>");
            sb.Append("<a class=\"card-foot-link\" href=\"results\">View full results &amp; trends →</a>");
        }
        CloseCard(sb);
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

    private static string Css() => @"<style>
        :root {
            --navy: #14294d; --navy-2: #1c3a63; --rail: #0f2038;
            --bg: #eef1f5; --surface: #ffffff; --ink: #1d2733; --ink-soft: #51606f;
            --ink-muted: #8593a3; --rule: #e2e7ee; --accent: #1f6fd6;
            --crit: #d92121; --crit-soft: #fdecec; --warn: #c2640a; --warn-soft: #fdf2e3;
            --info: #1f6fd6; --info-soft: #e9f2fd; --ok: #1f8a47;
        }
        * { box-sizing: border-box; }
        body { margin: 0; background: var(--bg); color: var(--ink); line-height: 1.45;
               font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', 'Inter', system-ui, sans-serif;
               -webkit-font-smoothing: antialiased; font-size: 14px; }

        .topbar { display: flex; align-items: center; gap: 1.5rem; background: var(--navy);
                  color: #fff; padding: .55rem 1.1rem; }
        .brand { font-weight: 700; font-size: 1.05rem; letter-spacing: -.01em; display: flex;
                 align-items: center; gap: .4rem; white-space: nowrap; }
        .brand-mark { color: #5fb0ff; }
        .patient-id { border-left: 1px solid rgba(255,255,255,.2); padding-left: 1.5rem; }
        .pid-name { font-weight: 700; font-size: 1.05rem; }
        .pid-sub { font-size: .82rem; color: #b9c6d6; margin-top: .05rem; }
        .topbar-meta { margin-left: auto; text-align: right; font-size: .8rem; color: #cdd8e5; }
        .topbar-meta .meta-dim { color: #8da0b6; font-size: .76rem; }

        .shell { display: flex; min-height: calc(100vh - 56px); }
        .icon-rail { width: 52px; background: var(--rail); flex-shrink: 0; display: flex;
                     flex-direction: column; align-items: center; padding-top: .6rem; gap: .35rem; }
        .rail-icon { width: 38px; height: 38px; border-radius: 9px; display: flex; align-items: center;
                     justify-content: center; color: #9fb2c9; text-decoration: none; font-size: 1.15rem; }
        .rail-icon:hover { background: rgba(255,255,255,.08); color: #fff; }

        .workspace { flex: 1; min-width: 0; padding: 0 1.25rem 2rem; }
        .tabs { display: flex; gap: .25rem; border-bottom: 1px solid var(--rule); background: var(--surface);
                margin: 0 -1.25rem 0; padding: 0 1.25rem; position: sticky; top: 0; z-index: 2; }
        .tab { padding: .8rem .9rem; font-size: .8rem; font-weight: 600; letter-spacing: .03em;
               text-transform: uppercase; color: var(--ink-muted); text-decoration: none;
               border-bottom: 2.5px solid transparent; }
        .tab:hover { color: var(--ink-soft); }
        .tab.active { color: var(--accent); border-bottom-color: var(--accent); }

        .chart-title-row h1 { font-size: 1.15rem; font-weight: 700; margin: 1rem 0 .9rem; }

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
        .chevron { color: var(--ink-muted); font-size: 1.1rem; }

        .lab-list { list-style: none; margin: 0; padding: .15rem 0 .5rem; }
        .lab-row { display: grid; grid-template-columns: 1fr auto auto; align-items: baseline; gap: .6rem;
                   padding: .5rem 1rem; border-top: 1px solid var(--rule); }
        .lab-row:first-child { border-top: 0; }
        .lab-name { font-weight: 500; }
        .lab-value { font-weight: 700; font-size: 1.05rem; letter-spacing: -.01em; }
        .lab-unit { font-weight: 500; font-size: .78rem; color: var(--ink-muted); }
        .lab-when { font-size: .76rem; color: var(--ink-muted); }
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
    </style>";
}
