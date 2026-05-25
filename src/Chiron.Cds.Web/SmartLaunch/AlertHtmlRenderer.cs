using System.Text;

using Chiron.Cds.Web.CdsHooks.Models;
using Markdig;

namespace Chiron.Cds.Web.SmartLaunch;

internal static class AlertHtmlRenderer
{
    // DisableHtml() is load-bearing: the markdown source can contain
    // FHIR-derived strings (e.g. Observation.value.unit copied from a
    // remote FHIR server) and Markdig would otherwise pass <script> tags
    // through verbatim into the page.
    private static readonly MarkdownPipeline MarkdownPipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    /// <summary>
    /// Renders the post-launch / demo Visit Brief. Three-column on
    /// desktop (patient header / card stack / quick actions), single
    /// column on mobile. Apple-Health-style: calm palette, hero numbers,
    /// soft shadows, generous whitespace.
    /// </summary>
    public static string Render(
        string heading,
        string subline,
        IReadOnlyList<CdsCard> cards,
        string? banner = null,
        string? navBar = null,
        PatientHeader? patient = null)
    {
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.Append("<title>").Append(WebEncode(heading)).Append("</title>");
        sb.Append(InlineCss());
        sb.Append("</head><body>");

        if (!string.IsNullOrEmpty(navBar))
            sb.Append("<nav class=\"navbar\">").Append(navBar).Append("</nav>");

        sb.Append("<header class=\"page-header\"><div class=\"page-header-inner\">");
        sb.Append("<h1>").Append(WebEncode(heading)).Append("</h1>");
        // When patient context is available, render a proper chart-banner
        // demographics line beneath the name (age · sex · DOB · MRN).
        // Without it, fall back to the plain text subline the caller supplied.
        if (patient is not null)
            RenderDemographics(sb, patient);
        if (!string.IsNullOrWhiteSpace(subline))
            sb.Append("<p class=\"subline\">").Append(WebEncode(subline)).Append("</p>");
        if (!string.IsNullOrEmpty(banner))
            sb.Append("<div class=\"banner\">").Append(WebEncode(banner)).Append("</div>");
        sb.Append("</div></header>");

        sb.Append("<main class=\"brief\">");

        // Left rail — patient header
        sb.Append("<aside class=\"patient-rail\">");
        if (patient is not null)
            RenderPatientRail(sb, patient);
        else
            sb.Append("<div class=\"no-patient\">No patient context</div>");
        sb.Append("</aside>");

        // Center column — card stack
        sb.Append("<section class=\"cards\">");
        sb.Append("<div class=\"cards-meta\">");
        sb.Append("<span class=\"cards-count\">").Append(cards.Count).Append("</span> ");
        sb.Append("alert").Append(cards.Count == 1 ? "" : "s").Append(" today");
        sb.Append("</div>");
        if (cards.Count == 0)
        {
            sb.Append("<div class=\"empty-state\">");
            sb.Append("<div class=\"empty-glyph\">✓</div>");
            sb.Append("<div class=\"empty-title\">Nothing needs your attention</div>");
            sb.Append("<div class=\"empty-detail\">The engine evaluated every applicable rule against this chart and found no open gaps.</div>");
            sb.Append("</div>");
        }
        else
        {
            foreach (var c in cards) RenderCard(sb, c);
        }
        sb.Append("</section>");

        // Right rail — Chiron meta + a soft legend
        sb.Append("<aside class=\"meta-rail\">");
        sb.Append("<div class=\"meta-card\">");
        sb.Append("<div class=\"meta-label\">About these recommendations</div>");
        sb.Append("<p>Every card carries the full derivation graph from input data back to the clinical guideline. Click a card title to expand the reasoning.</p>");
        sb.Append("<ul class=\"meta-list\">");
        sb.Append("<li><span class=\"sev-dot critical\"></span> Critical — act now</li>");
        sb.Append("<li><span class=\"sev-dot warning\"></span> Warning — action recommended</li>");
        sb.Append("<li><span class=\"sev-dot info\"></span> Info — gap or risk to address</li>");
        sb.Append("</ul>");
        sb.Append("</div>");
        sb.Append("</aside>");

        sb.Append("</main></body></html>");
        return sb.ToString();
    }

    private static void RenderDemographics(StringBuilder sb, PatientHeader patient)
    {
        sb.Append("<div class=\"demographics\">");
        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(patient.AgeSex)) parts.Add(patient.AgeSex);
        if (!string.IsNullOrWhiteSpace(patient.DateOfBirth)) parts.Add("Born " + patient.DateOfBirth);
        if (!string.IsNullOrWhiteSpace(patient.Mrn)) parts.Add("MRN " + patient.Mrn);
        for (var i = 0; i < parts.Count; i++)
        {
            if (i > 0) sb.Append("<span class=\"demo-sep\"> · </span>");
            sb.Append("<span class=\"demo-item\">").Append(WebEncode(parts[i])).Append("</span>");
        }
        sb.Append("</div>");
    }

    private static void RenderPatientRail(StringBuilder sb, PatientHeader patient)
    {
        sb.Append("<div class=\"patient-stats\">");
        AppendStat(sb, "Conditions", patient.ActiveConditions.Count);
        AppendStat(sb, "Medications", patient.ActiveMedicationCount);
        AppendStat(sb, patient.ActiveAllergies.Count == 1 ? "Allergy" : "Allergies", patient.ActiveAllergies.Count);
        AppendStat(sb, "Immunizations", patient.CompletedImmunizationCount);
        AppendStat(sb, "Procedures", patient.CompletedProcedureCount);
        sb.Append("</div>");

        if (patient.ActiveConditions.Count > 0)
        {
            sb.Append("<div class=\"patient-section\">");
            sb.Append("<div class=\"patient-section-label\">Conditions</div>");
            sb.Append("<ul class=\"chip-list\">");
            foreach (var cond in patient.ActiveConditions.Take(8))
                sb.Append("<li class=\"chip\">").Append(WebEncode(cond)).Append("</li>");
            sb.Append("</ul></div>");
        }
        if (patient.ActiveAllergies.Count > 0)
        {
            sb.Append("<div class=\"patient-section\">");
            sb.Append("<div class=\"patient-section-label\">Allergies</div>");
            sb.Append("<ul class=\"chip-list allergy-chips\">");
            foreach (var allergy in patient.ActiveAllergies.Take(6))
                sb.Append("<li class=\"chip allergy\">").Append(WebEncode(allergy)).Append("</li>");
            sb.Append("</ul></div>");
        }
    }

    private static void AppendStat(StringBuilder sb, string label, int value)
    {
        sb.Append("<div class=\"stat\">");
        sb.Append("<div class=\"stat-num\">").Append(value).Append("</div>");
        sb.Append("<div class=\"stat-label\">").Append(WebEncode(label)).Append("</div>");
        sb.Append("</div>");
    }

    private static void RenderCard(StringBuilder sb, CdsCard card)
    {
        var severityClass = card.Indicator switch
        {
            "critical" => "critical",
            "warning" => "warning",
            _ => "info",
        };
        sb.Append("<article class=\"card ").Append(severityClass).Append("\">");

        sb.Append("<div class=\"card-stripe\"></div>");
        sb.Append("<div class=\"card-body\">");

        sb.Append("<header class=\"card-header\">");
        sb.Append("<span class=\"badge ").Append(severityClass).Append("\">")
          .Append(card.Indicator.ToUpperInvariant()).Append("</span>");
        sb.Append("<h2 class=\"card-title\">").Append(WebEncode(card.Summary)).Append("</h2>");
        sb.Append("</header>");

        if (!string.IsNullOrEmpty(card.Detail))
        {
            sb.Append("<details class=\"derivation\" open>");
            sb.Append("<summary>Derivation &amp; citations</summary>");
            sb.Append("<div class=\"derivation-body\">")
              .Append(Markdown.ToHtml(card.Detail, MarkdownPipeline))
              .Append("</div>");
            sb.Append("</details>");
        }

        if (card.OverrideReasons is { Count: > 0 })
        {
            sb.Append("<div class=\"overrides\">");
            sb.Append("<div class=\"overrides-label\">If you override, document the reason:</div>");
            sb.Append("<ul>");
            foreach (var o in card.OverrideReasons)
            {
                sb.Append("<li><span class=\"override-text\">").Append(WebEncode(o.Display)).Append("</span>");
                sb.Append("<code class=\"override-code\">").Append(WebEncode(o.Code)).Append("</code></li>");
            }
            sb.Append("</ul>");
            sb.Append("</div>");
        }

        sb.Append("</div></article>");
    }

    private static string InlineCss() => @"<style>
        :root {
            --bg: #f5f5f7;
            --surface: #ffffff;
            --ink: #1d1d1f;
            --ink-soft: #515154;
            --ink-muted: #86868b;
            --rule: #e5e5e7;
            --crit: #d92121; --crit-soft: #fde8e8;
            --warn: #c25e04; --warn-soft: #fff4e3;
            --info: #1170d2; --info-soft: #e8f1fc;
            --ok: #1f8a47;
        }
        * { box-sizing: border-box; }
        body { font-family: -apple-system, BlinkMacSystemFont, 'SF Pro Text', 'Inter', system-ui, sans-serif;
               margin: 0; background: var(--bg); color: var(--ink);
               line-height: 1.5; -webkit-font-smoothing: antialiased; }

        .navbar { background: var(--ink); color: #fff; padding: .65rem 1.5rem;
                  display: flex; gap: 1.25rem; align-items: center; font-size: .92rem;
                  font-weight: 500; }
        .navbar a { color: #fff; text-decoration: none; opacity: .75; transition: opacity .15s; }
        .navbar a:hover { opacity: 1; }
        .navbar .brand { font-weight: 600; opacity: 1; letter-spacing: -.01em; }

        .page-header { background: linear-gradient(180deg, #fff 0%, var(--bg) 100%);
                       border-bottom: 1px solid var(--rule); }
        .page-header-inner { max-width: 1280px; margin: 0 auto; padding: 2rem 1.5rem 1.5rem; }
        h1 { font-size: 1.75rem; letter-spacing: -.02em; font-weight: 700; margin: 0 0 .4rem; }
        /* Chart-banner demographics: small inline pills under the name,
           middle-dot separated. Apple-Health-style ink-soft tone. */
        .demographics { color: var(--ink-soft); font-size: .92rem; margin: 0 0 .4rem;
                        display: flex; flex-wrap: wrap; align-items: baseline;
                        column-gap: .35rem; row-gap: .15rem; }
        .demographics .demo-item { color: var(--ink-soft); }
        .demographics .demo-sep { color: var(--ink-muted); }
        .subline { color: var(--ink-muted); margin: 0; font-size: .85rem; max-width: 70ch; }
        .banner { background: var(--warn-soft); border: 1px solid #f0c46a; padding: .6rem .9rem;
                  border-radius: 8px; margin-top: 1rem; font-size: .88rem; color: var(--warn);
                  max-width: 70ch; }

        .brief { max-width: 1280px; margin: 1.5rem auto 3rem; padding: 0 1.5rem;
                 display: grid; grid-template-columns: 280px minmax(0, 1fr) 280px; gap: 1.5rem; }

        /* ---------- Patient rail (left) — chip lists + counts only.
           Name + age + DOB + MRN render in the page-header banner. ---------- */
        .patient-rail { position: sticky; top: 1rem; align-self: start; }
        .no-patient { background: var(--surface); border-radius: 16px; padding: 1.25rem;
                      box-shadow: 0 1px 2px rgba(0,0,0,.04); color: var(--ink-muted); font-size: .9rem; }
        /* Vertical list of stats — one row per metric so labels never overflow
           the 280px rail. Apple-Health-style: big number left, label right,
           gentle 1px row dividers, last row uncapped. */
        .patient-stats { margin-top: .75rem; background: var(--surface); border-radius: 16px;
                         padding: .25rem .75rem; box-shadow: 0 1px 2px rgba(0,0,0,.04); }
        .stat { display: flex; align-items: baseline; justify-content: space-between;
                gap: .75rem; padding: .6rem .25rem; border-bottom: 1px solid var(--rule); }
        .stat:last-child { border-bottom: 0; }
        .stat-num { font-size: 1.35rem; font-weight: 700; letter-spacing: -.02em;
                    color: var(--ink); min-width: 1.5ch; text-align: right; }
        .stat-label { font-size: .85rem; color: var(--ink-soft); font-weight: 500;
                      text-align: right; }
        .patient-section { margin-top: 1rem; background: var(--surface); border-radius: 16px;
                           padding: .9rem 1rem; box-shadow: 0 1px 2px rgba(0,0,0,.04); }
        .patient-section-label { font-size: .7rem; text-transform: uppercase; color: var(--ink-muted);
                                 letter-spacing: .05em; font-weight: 600; margin-bottom: .5rem; }
        .chip-list { list-style: none; margin: 0; padding: 0;
                     display: flex; flex-wrap: wrap; gap: .35rem; }
        .chip { background: var(--bg); border: 1px solid var(--rule); border-radius: 999px;
                padding: .2rem .6rem; font-size: .78rem; color: var(--ink-soft); }
        .chip.allergy { background: var(--crit-soft); border-color: #f4caca; color: var(--crit); font-weight: 500; }

        /* ---------- Card stack (center) ---------- */
        .cards { min-width: 0; }
        .cards-meta { font-size: .82rem; color: var(--ink-muted); margin: .2rem 0 .9rem .1rem;
                      text-transform: uppercase; letter-spacing: .04em; font-weight: 600; }
        .cards-count { color: var(--ink); font-size: 1.05rem; font-weight: 700;
                       letter-spacing: -.01em; text-transform: none; }
        .empty-state { background: var(--surface); border-radius: 16px; padding: 2.5rem 1.5rem;
                       text-align: center; box-shadow: 0 1px 2px rgba(0,0,0,.04); }
        .empty-glyph { color: var(--ok); font-size: 2.5rem; line-height: 1; }
        .empty-title { font-weight: 600; margin-top: .5rem; }
        .empty-detail { color: var(--ink-muted); font-size: .9rem; margin-top: .3rem;
                        max-width: 38ch; margin-left: auto; margin-right: auto; }

        .card { background: var(--surface); border-radius: 16px; margin-bottom: 1rem;
                box-shadow: 0 1px 2px rgba(0,0,0,.04); overflow: hidden;
                display: grid; grid-template-columns: 6px 1fr; }
        .card-stripe { background: var(--info); }
        .card.warning .card-stripe { background: var(--warn); }
        .card.critical .card-stripe { background: var(--crit); }
        .card-body { padding: 1.1rem 1.25rem; min-width: 0; }
        .card-header { display: flex; align-items: center; gap: .65rem; margin-bottom: .5rem; }
        .badge { font-size: .65rem; font-weight: 700; padding: .2rem .55rem;
                 border-radius: 6px; letter-spacing: .05em; }
        .badge.info { background: var(--info-soft); color: var(--info); }
        .badge.warning { background: var(--warn-soft); color: var(--warn); }
        .badge.critical { background: var(--crit-soft); color: var(--crit); }
        .card-title { font-size: 1.05rem; font-weight: 600; margin: 0;
                      letter-spacing: -.01em; line-height: 1.35; }

        .derivation { margin: .5rem 0; }
        .derivation summary { cursor: pointer; padding: .35rem 0; font-weight: 600;
                              color: var(--info); user-select: none; font-size: .9rem; }
        .derivation summary:hover { text-decoration: underline; }
        .derivation-body { padding: .75rem 1rem; background: var(--bg); border-radius: 10px;
                           margin-top: .4rem; font-size: .9rem; line-height: 1.55; }
        .derivation-body h3 { margin: .9rem 0 .25rem; font-size: .78rem; color: var(--ink-muted);
                              text-transform: uppercase; letter-spacing: .04em; font-weight: 600; }
        .derivation-body h3:first-child { margin-top: 0; }
        .derivation-body ul { padding-left: 1.25rem; margin: .3rem 0; }
        .derivation-body code { background: var(--rule); padding: .05rem .3rem; border-radius: 4px;
                                font-family: ui-monospace, 'SF Mono', Menlo, monospace; font-size: .85em; }
        .derivation-body a { color: var(--info); text-decoration: none; }
        .derivation-body a:hover { text-decoration: underline; }
        .derivation-body p { margin: .35rem 0; }

        .overrides { background: var(--bg); border-radius: 10px; padding: .65rem .9rem;
                     margin: .65rem 0; font-size: .85rem; }
        .overrides-label { font-weight: 600; margin-bottom: .3rem; color: var(--ink-soft); }
        .overrides ul { list-style: none; margin: 0; padding: 0; display: grid; gap: .25rem; }
        .overrides li { display: flex; align-items: center; gap: .5rem; }
        .override-code { font-family: ui-monospace, 'SF Mono', Menlo, monospace;
                         font-size: .72rem; color: var(--ink-muted);
                         background: var(--surface); padding: .05rem .35rem; border-radius: 4px; }

        /* ---------- Meta rail (right) ---------- */
        .meta-rail { position: sticky; top: 1rem; align-self: start; }
        .meta-card { background: var(--surface); border-radius: 16px; padding: 1rem 1.1rem;
                     box-shadow: 0 1px 2px rgba(0,0,0,.04); font-size: .87rem;
                     color: var(--ink-soft); }
        .meta-label { font-size: .7rem; text-transform: uppercase; color: var(--ink-muted);
                      letter-spacing: .05em; font-weight: 600; margin-bottom: .5rem; }
        .meta-card p { margin: 0 0 .8rem; line-height: 1.5; }
        .meta-list { list-style: none; margin: 0; padding: 0; display: grid; gap: .35rem; }
        .meta-list li { display: flex; align-items: center; gap: .55rem; font-size: .85rem; }
        .sev-dot { width: 9px; height: 9px; border-radius: 50%; display: inline-block; flex-shrink: 0; }
        .sev-dot.critical { background: var(--crit); }
        .sev-dot.warning { background: var(--warn); }
        .sev-dot.info { background: var(--info); }

        @media (max-width: 980px) {
            .brief { grid-template-columns: 1fr; }
            .patient-rail, .meta-rail { position: static; }
            .patient-stats { grid-template-columns: repeat(5, 1fr); }
        }
        @media (max-width: 540px) {
            .patient-stats { grid-template-columns: repeat(3, 1fr); }
        }
    </style>";

    private static string WebEncode(string s) => System.Net.WebUtility.HtmlEncode(s);
}
