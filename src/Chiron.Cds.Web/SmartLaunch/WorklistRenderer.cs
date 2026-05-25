using System.Text;

namespace Chiron.Cds.Web.SmartLaunch;

/// <summary>
/// Renders the Day View / worklist — Apple-Health-style row-per-patient
/// summary of who needs the clinician's attention today. Sortable by
/// time / severity / alert count. Click a row → drill into the Visit
/// Brief for that patient.
/// </summary>
internal static class WorklistRenderer
{
    public static string Render(
        string heading,
        string subline,
        IReadOnlyList<WorklistRow> rows,
        string? banner = null,
        string? navBar = null,
        string drillBaseUrl = "/app/demo")
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
        sb.Append("<div class=\"day-label\">").Append(WebEncode(DateTime.Today.ToString("dddd, MMMM d"))).Append("</div>");
        sb.Append("<h1>").Append(WebEncode(heading)).Append("</h1>");
        sb.Append("<p class=\"subline\">").Append(WebEncode(subline)).Append("</p>");
        if (!string.IsNullOrEmpty(banner))
            sb.Append("<div class=\"banner\">").Append(WebEncode(banner)).Append("</div>");

        sb.Append("<div class=\"summary-stats\">");
        var attention = rows.Count(r => r.AlertCount > 0);
        var clean = rows.Count - attention;
        AppendSummary(sb, rows.Count, "Patients today");
        AppendSummary(sb, attention, "Need attention");
        AppendSummary(sb, clean, "Clean charts");
        sb.Append("</div>");
        sb.Append("</div></header>");

        sb.Append("<main class=\"worklist\">");
        if (rows.Count == 0)
        {
            sb.Append("<div class=\"empty-state\">");
            sb.Append("<div class=\"empty-glyph\">∅</div>");
            sb.Append("<div class=\"empty-title\">No patients on today's schedule</div>");
            sb.Append("</div>");
        }
        else
        {
            foreach (var row in rows) RenderRow(sb, row, drillBaseUrl);
        }
        sb.Append("</main></body></html>");
        return sb.ToString();
    }

    private static void AppendSummary(StringBuilder sb, int value, string label)
    {
        sb.Append("<div class=\"summary-stat\">");
        sb.Append("<div class=\"summary-num\">").Append(value).Append("</div>");
        sb.Append("<div class=\"summary-label\">").Append(WebEncode(label)).Append("</div>");
        sb.Append("</div>");
    }

    private static void RenderRow(StringBuilder sb, WorklistRow row, string drillBaseUrl)
    {
        var severityClass = row.HeadlineSeverity switch
        {
            "critical" => "critical",
            "warning" => "warning",
            "info" => "info",
            _ => "clean",
        };
        // PatientId is a URL path segment — escape with Uri.EscapeDataString
        // so a value containing '/' or '?' cannot inject extra path segments
        // or a query string into the href.
        sb.Append("<a class=\"row ").Append(severityClass).Append("\" href=\"")
          .Append(WebEncode(drillBaseUrl)).Append('/').Append(Uri.EscapeDataString(row.PatientId)).Append("\">");

        sb.Append("<div class=\"row-stripe\"></div>");

        sb.Append("<div class=\"row-time\">");
        if (!string.IsNullOrEmpty(row.AppointmentTime))
            sb.Append("<div class=\"time\">").Append(WebEncode(row.AppointmentTime)).Append("</div>");
        else
            sb.Append("<div class=\"time placeholder\">—</div>");
        sb.Append("</div>");

        sb.Append("<div class=\"row-patient\">");
        sb.Append("<div class=\"patient-name\">").Append(WebEncode(row.DisplayName)).Append("</div>");
        sb.Append("<div class=\"patient-age-sex\">").Append(WebEncode(row.AgeSex)).Append("</div>");
        if (!string.IsNullOrEmpty(row.ChiefComplaint))
            sb.Append("<div class=\"complaint\">").Append(WebEncode(row.ChiefComplaint)).Append("</div>");
        sb.Append("</div>");

        sb.Append("<div class=\"row-flag\">");
        if (!string.IsNullOrEmpty(row.HeadlineFlag))
        {
            sb.Append("<div class=\"flag\">").Append(WebEncode(row.HeadlineFlag)).Append("</div>");
            sb.Append("<div class=\"flag-meta\">");
            sb.Append(row.AlertCount).Append(" alert").Append(row.AlertCount == 1 ? "" : "s");
            sb.Append("</div>");
        }
        else
        {
            sb.Append("<div class=\"flag clean-flag\">Clean</div>");
        }
        sb.Append("</div>");

        sb.Append("<div class=\"row-chevron\">›</div>");
        sb.Append("</a>");
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
            --ok: #1f8a47; --ok-soft: #e6f4ec;
        }
        * { box-sizing: border-box; }
        body { font-family: -apple-system, BlinkMacSystemFont, 'SF Pro Text', 'Inter', system-ui, sans-serif;
               margin: 0; background: var(--bg); color: var(--ink);
               line-height: 1.5; -webkit-font-smoothing: antialiased; }

        .navbar { background: var(--ink); color: #fff; padding: .65rem 1.5rem;
                  display: flex; gap: 1.25rem; align-items: center; font-size: .92rem; font-weight: 500; }
        .navbar a { color: #fff; text-decoration: none; opacity: .75; transition: opacity .15s; }
        .navbar a:hover { opacity: 1; }
        .navbar .brand { font-weight: 600; opacity: 1; letter-spacing: -.01em; }

        .page-header { background: linear-gradient(180deg, #fff 0%, var(--bg) 100%);
                       border-bottom: 1px solid var(--rule); }
        .page-header-inner { max-width: 920px; margin: 0 auto; padding: 2rem 1.5rem 1.5rem; }
        .day-label { font-size: .85rem; color: var(--ink-muted); text-transform: uppercase;
                     letter-spacing: .06em; font-weight: 600; margin-bottom: .25rem; }
        h1 { font-size: 1.85rem; letter-spacing: -.02em; font-weight: 700; margin: 0 0 .4rem; }
        .subline { color: var(--ink-soft); margin: 0; font-size: .95rem; max-width: 60ch; }
        .banner { background: var(--warn-soft); border: 1px solid #f0c46a; padding: .6rem .9rem;
                  border-radius: 8px; margin-top: 1rem; font-size: .88rem; color: var(--warn);
                  max-width: 60ch; }

        .summary-stats { display: flex; gap: 2rem; margin-top: 1.5rem; }
        .summary-stat { display: flex; flex-direction: column; }
        .summary-num { font-size: 2rem; font-weight: 700; letter-spacing: -.03em; line-height: 1; color: var(--ink); }
        .summary-label { font-size: .75rem; color: var(--ink-muted); text-transform: uppercase;
                         letter-spacing: .05em; font-weight: 600; margin-top: .25rem; }

        .worklist { max-width: 920px; margin: 1.5rem auto 3rem; padding: 0 1.5rem; }
        .empty-state { background: var(--surface); border-radius: 16px; padding: 3rem 1.5rem;
                       text-align: center; box-shadow: 0 1px 2px rgba(0,0,0,.04); }
        .empty-glyph { font-size: 3rem; color: var(--ink-muted); line-height: 1; }
        .empty-title { font-weight: 600; margin-top: .8rem; color: var(--ink-soft); }

        .row { display: grid; grid-template-columns: 4px 80px 1fr minmax(180px, 280px) 24px;
               gap: 1rem; align-items: center; padding: 1rem 1.25rem;
               background: var(--surface); border-radius: 14px; margin-bottom: .75rem;
               box-shadow: 0 1px 2px rgba(0,0,0,.04); text-decoration: none; color: inherit;
               transition: transform .15s ease, box-shadow .15s ease; }
        .row:hover { transform: translateY(-1px); box-shadow: 0 4px 10px rgba(0,0,0,.06); }
        .row-stripe { align-self: stretch; border-radius: 2px; background: var(--info); }
        .row.warning .row-stripe { background: var(--warn); }
        .row.critical .row-stripe { background: var(--crit); }
        .row.clean .row-stripe { background: var(--ok); }
        .row.clean { opacity: .85; }

        .row-time .time { font-size: 1.05rem; font-weight: 700; letter-spacing: -.01em; }
        .row-time .time.placeholder { color: var(--ink-muted); font-weight: 400; }

        .row-patient { min-width: 0; }
        .patient-name { font-size: 1.05rem; font-weight: 600; letter-spacing: -.01em;
                        white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
        .patient-age-sex { font-size: .85rem; color: var(--ink-muted); margin-top: .1rem; }
        .complaint { font-size: .88rem; color: var(--ink-soft); margin-top: .25rem;
                     white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }

        .row-flag { text-align: right; }
        .flag { font-size: .9rem; color: var(--ink); font-weight: 500;
                white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
        .row.warning .flag { color: var(--warn); }
        .row.critical .flag { color: var(--crit); font-weight: 600; }
        .flag-meta { font-size: .75rem; color: var(--ink-muted); margin-top: .2rem;
                     text-transform: uppercase; letter-spacing: .04em; font-weight: 600; }
        .clean-flag { color: var(--ok) !important; font-weight: 600; }

        .row-chevron { color: var(--ink-muted); font-size: 1.5rem; text-align: right;
                       transition: transform .15s, color .15s; }
        .row:hover .row-chevron { transform: translateX(2px); color: var(--ink); }

        @media (max-width: 720px) {
            .row { grid-template-columns: 4px 60px 1fr 24px; gap: .75rem; }
            .row-flag { display: none; }
            .summary-stats { gap: 1.5rem; }
        }
    </style>";

    private static string WebEncode(string s) => System.Net.WebUtility.HtmlEncode(s);
}
