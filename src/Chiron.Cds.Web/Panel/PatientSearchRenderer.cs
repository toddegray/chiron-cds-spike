using System.Net;
using System.Text;

namespace Chiron.Cds.Web.Panel;

/// <summary>
/// Renders the patient search surface: search box at the top, ranked
/// matches as Apple-Health-style rows below. Pure HTML; no JavaScript
/// required — the form posts back to the same route via GET so back/forward
/// preserves query state.
/// </summary>
internal static class PatientSearchRenderer
{
    public static string Render(
        PatientSearchCriteria criteria,
        IReadOnlyList<PatientSearchHit> hits,
        string? warning,
        string navBar,
        string drillBaseUrl)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        ArgumentNullException.ThrowIfNull(hits);
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.Append("<title>Search patients</title>");
        sb.Append(InlineCss());
        sb.Append("</head><body>");
        sb.Append("<nav class=\"navbar\">").Append(navBar).Append("</nav>");

        sb.Append("<header class=\"page-header\"><div class=\"page-header-inner\">");
        sb.Append("<h1>Find a patient</h1>");
        sb.Append("<p class=\"subline\">Look up a patient by MRN, by name + date of birth, or by encounter id. Fill whichever you have — MRN alone is enough.</p>");

        sb.Append("<form method=\"get\" action=\"/app/search\" class=\"search-form\">");
        Field(sb, "mrn", "text", "MRN", criteria.Mrn, autofocus: true);
        Field(sb, "name", "text", "Name (e.g. Lopez or Camila Lopez)", criteria.Name, autofocus: false);
        Field(sb, "dob", "date", "Date of birth", criteria.BirthDate, autofocus: false);
        Field(sb, "encounter", "text", "Encounter id", criteria.EncounterId, autofocus: false);
        sb.Append("<button type=\"submit\">Search</button>");
        sb.Append("</form>");
        if (!string.IsNullOrEmpty(warning))
        {
            sb.Append("<div class=\"warn\">").Append(WebEncode(warning)).Append("</div>");
        }
        sb.Append("</div></header>");

        sb.Append("<main class=\"results\">");
        if (criteria.IsEmpty)
        {
            sb.Append("<div class=\"hint\">Start typing above — enter an <em>MRN</em>, a <em>name + date of birth</em>, or an <em>encounter id</em>. Results come back live from the connected FHIR endpoint.</div>");
        }
        else if (hits.Count == 0)
        {
            sb.Append("<div class=\"empty-state\">");
            sb.Append("<div class=\"empty-glyph\">∅</div>");
            sb.Append("<div class=\"empty-title\">No patients matched</div>");
            sb.Append("<div class=\"empty-detail\">The connected sandbox returned no results. Double-check the MRN, or pair a name with a date of birth.</div>");
            sb.Append("</div>");
        }
        else
        {
            sb.Append("<div class=\"results-meta\"><span class=\"results-count\">").Append(hits.Count)
              .Append("</span> match").Append(hits.Count == 1 ? "" : "es").Append("</div>");
            foreach (var hit in hits) RenderRow(sb, hit, drillBaseUrl);
        }
        sb.Append("</main></body></html>");
        return sb.ToString();
    }

    private static void Field(StringBuilder sb, string name, string type, string placeholder, string? value, bool autofocus)
    {
        sb.Append("<input type=\"").Append(type).Append("\" name=\"").Append(name).Append('"');
        sb.Append(" placeholder=\"").Append(WebEncode(placeholder)).Append('"');
        if (autofocus) sb.Append(" autofocus");
        sb.Append(" value=\"").Append(WebEncode(value ?? string.Empty)).Append("\" />");
    }

    private static void RenderRow(StringBuilder sb, PatientSearchHit hit, string drillBaseUrl)
    {
        // The id is a URL path segment, not an HTML text node — Uri.EscapeDataString
        // is the right encoder. HtmlEncode would leave '/', '?', '#' alone and let
        // them inject extra path segments / a query string.
        var hrefSegment = Uri.EscapeDataString(hit.PatientId);
        sb.Append("<a class=\"row\" href=\"").Append(WebEncode(drillBaseUrl)).Append('/')
          .Append(hrefSegment).Append("\">");
        sb.Append("<div class=\"row-patient\">");
        sb.Append("<div class=\"patient-name\">").Append(WebEncode(hit.DisplayName)).Append("</div>");
        sb.Append("<div class=\"patient-meta\">");
        if (!string.IsNullOrEmpty(hit.Gender))
            sb.Append(WebEncode(Capitalize(hit.Gender)));
        if (!string.IsNullOrEmpty(hit.BirthDate))
        {
            if (!string.IsNullOrEmpty(hit.Gender)) sb.Append(" · ");
            sb.Append("Born ").Append(WebEncode(hit.BirthDate));
        }
        sb.Append("</div>");
        sb.Append("</div>");
        sb.Append("<div class=\"row-id\">");
        if (!string.IsNullOrEmpty(hit.Mrn))
            sb.Append("MRN ").Append(WebEncode(hit.Mrn));
        sb.Append("</div>");
        sb.Append("<div class=\"row-chevron\">›</div>");
        sb.Append("</a>");
    }

    private static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static string InlineCss() => @"<style>
        :root {
            --bg: #f5f5f7; --surface: #ffffff; --ink: #1d1d1f; --ink-soft: #515154;
            --ink-muted: #86868b; --rule: #e5e5e7; --accent: #1170d2;
        }
        * { box-sizing: border-box; }
        body { font-family: -apple-system, BlinkMacSystemFont, 'SF Pro Text', 'Inter', system-ui, sans-serif;
               margin: 0; background: var(--bg); color: var(--ink); line-height: 1.5; -webkit-font-smoothing: antialiased; }
        .navbar { background: var(--ink); color: #fff; padding: .65rem 1.5rem;
                  display: flex; gap: 1.25rem; align-items: center; font-size: .92rem; font-weight: 500; }
        .navbar a { color: #fff; text-decoration: none; opacity: .75; }
        .navbar a:hover { opacity: 1; }
        .navbar .brand { font-weight: 600; opacity: 1; letter-spacing: -.01em; }

        .page-header { background: linear-gradient(180deg, #fff 0%, var(--bg) 100%); border-bottom: 1px solid var(--rule); }
        .page-header-inner { max-width: 920px; margin: 0 auto; padding: 2rem 1.5rem 1.5rem; }
        h1 { font-size: 1.85rem; letter-spacing: -.02em; font-weight: 700; margin: 0 0 .4rem; }
        .subline { color: var(--ink-soft); margin: 0; font-size: .95rem; max-width: 60ch; }

        .search-form { display: flex; flex-wrap: wrap; gap: .75rem; margin-top: 1.5rem; }
        .search-form input {
            flex: 1 1 180px; min-width: 0; padding: .75rem 1rem; font-size: 1rem; border: 1px solid var(--rule);
            border-radius: 10px; background: #fff; color: var(--ink);
        }
        .search-form input:focus { outline: 2px solid var(--accent); outline-offset: 1px; }
        .search-form button {
            flex: 0 0 auto; padding: 0 1.4rem; font-size: .95rem; font-weight: 600; border: 0; border-radius: 10px;
            background: var(--accent); color: #fff; cursor: pointer; transition: background .15s;
        }
        .search-form button:hover { background: #0c5fb5; }
        .warn { margin-top: .8rem; padding: .6rem .9rem; background: #fff4e3; border: 1px solid #f0c46a;
                color: #7a4500; border-radius: 8px; font-size: .88rem; max-width: 60ch; }

        .results { max-width: 920px; margin: 1.5rem auto 3rem; padding: 0 1.5rem; }
        .hint { color: var(--ink-muted); padding: 2rem 0; text-align: center; }
        .results-meta { font-size: .8rem; color: var(--ink-muted); text-transform: uppercase;
                        letter-spacing: .06em; font-weight: 600; margin-bottom: .75rem; }
        .results-count { color: var(--ink); font-weight: 700; }

        .empty-state { background: var(--surface); border-radius: 16px; padding: 2.5rem 1.5rem;
                       text-align: center; box-shadow: 0 1px 2px rgba(0,0,0,.04); }
        .empty-glyph { font-size: 3rem; color: var(--ink-muted); line-height: 1; }
        .empty-title { font-weight: 600; margin-top: .8rem; color: var(--ink-soft); }
        .empty-detail { font-size: .9rem; color: var(--ink-muted); margin-top: .4rem; }

        .row { display: grid; grid-template-columns: 1fr 120px 24px; gap: 1rem; align-items: center;
               padding: .9rem 1.25rem; background: var(--surface); border-radius: 14px;
               margin-bottom: .6rem; box-shadow: 0 1px 2px rgba(0,0,0,.04);
               text-decoration: none; color: inherit;
               transition: transform .15s ease, box-shadow .15s ease; }
        .row:hover { transform: translateY(-1px); box-shadow: 0 4px 10px rgba(0,0,0,.06); }
        .patient-name { font-size: 1.05rem; font-weight: 600; letter-spacing: -.01em; }
        .patient-meta { font-size: .85rem; color: var(--ink-muted); margin-top: .15rem; }
        .row-id { font-size: .8rem; color: var(--ink-muted); text-align: right;
                  min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
        .row-chevron { color: var(--ink-muted); font-size: 1.5rem; text-align: right; }
        .row:hover .row-chevron { color: var(--ink); }
    </style>";

    private static string WebEncode(string s) => WebUtility.HtmlEncode(s);
}
