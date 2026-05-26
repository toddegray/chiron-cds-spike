using System.Globalization;
using System.Net;
using System.Text;
using Chiron.Cds.Web.SmartLaunch;

namespace Chiron.Cds.Web.Panel;

/// <summary>Renders the per-patient Notes tab: SOAP draft form + history list.</summary>
internal static class NoteEntryRenderer
{
    public static string Render(NoteEntryView view, string navBar, IReadOnlyList<ChartTab> chartTabs)
    {
        ArgumentNullException.ThrowIfNull(view);
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.Append("<title>Notes — ").Append(WebEncode(view.PatientDisplayName)).Append("</title>");
        sb.Append(InlineCss());
        sb.Append("</head><body>");
        sb.Append("<nav class=\"navbar\">").Append(navBar).Append("</nav>");
        RenderHeader(sb, view, chartTabs);

        sb.Append("<main class=\"notes-main\">");
        switch (view.Status)
        {
            case NoteEntryStatus.SignedOk:
                RenderSignedBanner(sb, view.WrittenId, view.PatientId);
                break;
            case NoteEntryStatus.NotAuthorised:
                RenderNotAuthorised(sb, view);
                break;
            default:
                if (!string.IsNullOrEmpty(view.ChartError))
                    sb.Append("<div class=\"banner err\">").Append(WebEncode(view.ChartError)).Append("</div>");
                else if (view.Status == NoteEntryStatus.Failed)
                    sb.Append("<div class=\"banner err\">").Append(WebEncode(view.Message ?? "")).Append("</div>");
                RenderForm(sb, view);
                RenderHistory(sb, view.History);
                break;
        }
        sb.Append("</main></body></html>");
        return sb.ToString();
    }

    private static void RenderHeader(StringBuilder sb, NoteEntryView view, IReadOnlyList<ChartTab> chartTabs)
    {
        sb.Append("<header class=\"page-header\"><div class=\"page-header-inner\">");
        sb.Append("<h1>").Append(WebEncode(view.PatientDisplayName)).Append("</h1>");
        if (!string.IsNullOrEmpty(view.PatientSubline))
            sb.Append("<div class=\"demographics\"><span class=\"demo-item\">")
              .Append(WebEncode(view.PatientSubline)).Append("</span></div>");
        sb.Append("<nav class=\"chart-tabs\" aria-label=\"Chart sections\">");
        foreach (var tab in chartTabs)
        {
            sb.Append("<a class=\"chart-tab");
            if (tab.IsActive) sb.Append(" active");
            sb.Append("\" href=\"").Append(WebEncode(tab.Href)).Append("\">")
              .Append(WebEncode(tab.Label)).Append("</a>");
        }
        sb.Append("</nav></div></header>");
    }

    private static void RenderSignedBanner(StringBuilder sb, string? writtenId, string patientId)
    {
        sb.Append("<div class=\"banner ok\">Progress note signed and written to the EHR — server-assigned id ");
        sb.Append("<code>").Append(WebEncode(writtenId ?? "(unknown)")).Append("</code>.</div>");
        sb.Append("<p><a class=\"link-back\" href=\"/app/patient/")
          .Append(Uri.EscapeDataString(patientId)).Append("\">← Back to Visit Brief</a></p>");
    }

    private static void RenderNotAuthorised(StringBuilder sb, NoteEntryView view)
    {
        sb.Append("<section class=\"signin-pane\">");
        sb.Append("<h2>Sign in to save the note</h2>");
        sb.Append("<p>Saving a progress note writes a <code>DocumentReference</code> to the EHR's authenticated FHIR endpoint, ");
        sb.Append("which requires an active SMART on FHIR session.</p>");
        sb.Append("<p><a class=\"btn primary\" href=\"/smart/launch\">Start SMART launch</a> ");
        sb.Append("<a class=\"btn secondary\" href=\"/app/patient/").Append(Uri.EscapeDataString(view.PatientId)).Append("/notes\">Back to draft</a></p>");
        sb.Append("</section>");
    }

    private static void RenderForm(StringBuilder sb, NoteEntryView view)
    {
        var d = view.Draft;
        sb.Append("<form method=\"post\" action=\"/app/patient/")
          .Append(Uri.EscapeDataString(view.PatientId)).Append("/notes\" class=\"note-form\">");
        sb.Append("<section class=\"form-section\"><h2>Progress note</h2>");
        sb.Append("<p class=\"hint\">SOAP format. Assessment and Plan are pre-filled from the active chart — edit freely before signing.</p>");
        Soap(sb, "subjective", "Subjective", "Subjective",
            d.Subjective,
            "Chief complaint, HPI, ROS, social context.");
        Soap(sb, "objective", "Objective", "Objective",
            d.Objective,
            "Vitals, exam findings, lab/imaging review.");
        Soap(sb, "assessment", "Assessment", "Assessment",
            d.Assessment,
            "Diagnoses and clinical reasoning.");
        Soap(sb, "plan", "Plan", "Plan",
            d.Plan,
            "Medications, orders, referrals, follow-up.");
        sb.Append("</section>");
        sb.Append("<div class=\"form-actions\">");
        sb.Append("<button type=\"submit\" class=\"btn primary\">Sign and save note</button>");
        sb.Append("</div></form>");
    }

    private static void Soap(StringBuilder sb, string id, string name, string label, string? value, string hint)
    {
        sb.Append("<label class=\"field\" for=\"").Append(id).Append("\">");
        sb.Append("<span class=\"field-label\">").Append(WebEncode(label)).Append("</span>");
        sb.Append("<textarea id=\"").Append(id).Append("\" name=\"").Append(name).Append("\" rows=\"5\">")
          .Append(WebEncode(value ?? string.Empty)).Append("</textarea>");
        sb.Append("<span class=\"hint\">").Append(WebEncode(hint)).Append("</span>");
        sb.Append("</label>");
    }

    private static void RenderHistory(StringBuilder sb, IReadOnlyList<NoteSummary> history)
    {
        sb.Append("<aside class=\"history-pane\"><h2>Prior notes</h2>");
        if (history.Count == 0)
        {
            sb.Append("<div class=\"empty\">No prior notes on file for this patient.</div>");
        }
        else
        {
            sb.Append("<ul class=\"note-list\">");
            foreach (var n in history)
            {
                sb.Append("<li class=\"note\">");
                sb.Append("<div class=\"note-head\">");
                sb.Append("<span class=\"note-title\">").Append(WebEncode(n.Title)).Append("</span>");
                sb.Append("<span class=\"note-status status-").Append(WebEncode(n.Status)).Append("\">")
                  .Append(WebEncode(n.Status)).Append("</span>");
                sb.Append("</div>");
                sb.Append("<div class=\"note-meta\">");
                if (!string.IsNullOrEmpty(n.Category))
                    sb.Append("<span class=\"note-category\">").Append(WebEncode(n.Category)).Append("</span>");
                sb.Append("<span class=\"note-when\">").Append(WebEncode(FormatWhen(n.AuthoredAt))).Append("</span>");
                sb.Append("</div></li>");
            }
            sb.Append("</ul>");
        }
        sb.Append("</aside>");
    }

    private static string FormatWhen(DateTimeOffset? when) =>
        when is null ? "—" : when.Value.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string InlineCss() => @"<style>
        :root { --bg:#f5f5f7; --surface:#fff; --ink:#1d1d1f; --ink-soft:#515154; --ink-muted:#86868b;
                --rule:#e5e5e7; --info:#1170d2; --warn:#c25e04; --crit:#d92121;
                --warn-soft:#fff4e3; --info-soft:#e8f1fc; --crit-soft:#fde8e8; --ok:#1f8a47; --ok-soft:#e6f4ec; }
        * { box-sizing: border-box; }
        body { font-family: -apple-system, BlinkMacSystemFont, 'SF Pro Text', system-ui, sans-serif;
               margin:0; background:var(--bg); color:var(--ink); line-height:1.5; -webkit-font-smoothing:antialiased; }
        .navbar { background:var(--ink); color:#fff; padding:.65rem 1.5rem; display:flex; gap:1.25rem;
                  align-items:center; font-size:.92rem; font-weight:500; }
        .navbar a { color:#fff; text-decoration:none; opacity:.75; }
        .navbar a:hover { opacity:1; } .navbar .brand { font-weight:600; opacity:1; letter-spacing:-.01em; }
        .page-header { background:linear-gradient(180deg,#fff 0%,var(--bg) 100%); border-bottom:1px solid var(--rule); }
        .page-header-inner { max-width:1280px; margin:0 auto; padding:1.25rem 1.5rem 1.25rem; }
        h1 { font-size:1.65rem; letter-spacing:-.02em; font-weight:700; margin:0 0 .35rem; }
        .demographics { color:var(--ink-soft); font-size:.92rem; }
        .chart-tabs { display:flex; gap:.25rem; margin-top:1rem; border-bottom:1px solid var(--rule); }
        .chart-tab { padding:.55rem .9rem; font-size:.92rem; color:var(--ink-soft);
                     text-decoration:none; border-radius:8px 8px 0 0; border-bottom:2px solid transparent; }
        .chart-tab:hover { color:var(--ink); }
        .chart-tab.active { color:var(--ink); font-weight:600; border-bottom-color:var(--info); }

        .notes-main { max-width:1280px; margin:1.5rem auto 3rem; padding:0 1.5rem;
                      display:grid; grid-template-columns: minmax(0, 1fr) 320px; gap:1.5rem; }
        .note-form { display:flex; flex-direction:column; gap:1rem; }
        .form-section { background:var(--surface); border-radius:14px; padding:1rem 1.25rem;
                        box-shadow:0 1px 2px rgba(0,0,0,.04); }
        .form-section h2 { font-size:1rem; margin:0 0 .25rem; }
        .field { display:flex; flex-direction:column; margin-bottom:1rem; }
        .field-label { font-size:.85rem; font-weight:600; color:var(--ink); margin-bottom:.25rem;
                       text-transform:uppercase; letter-spacing:.04em; }
        .field textarea { padding:.6rem .75rem; font-size:.92rem; border:1px solid var(--rule);
                          border-radius:8px; background:#fff; color:var(--ink); font-family:inherit;
                          resize:vertical; line-height:1.45; }
        .field textarea:focus { outline:2px solid var(--info); outline-offset:1px; }
        .hint { font-size:.78rem; color:var(--ink-muted); margin-top:.2rem; }
        .form-actions { display:flex; gap:.75rem; justify-content:flex-end; padding-top:.25rem; }
        .btn { padding:.55rem 1.1rem; font-size:.92rem; font-weight:600; border:0; border-radius:8px; cursor:pointer; }
        .btn.primary { background:var(--info); color:#fff; }
        .btn.primary:hover { background:#0c5fb5; }
        .btn.secondary { background:var(--surface); color:var(--ink); border:1px solid var(--rule); }
        .btn.secondary:hover { background:var(--bg); }

        .history-pane { display:flex; flex-direction:column; gap:.75rem; }
        .history-pane h2 { font-size:.78rem; text-transform:uppercase; letter-spacing:.06em;
                           color:var(--ink-muted); font-weight:600; margin:0; }
        .note-list { list-style:none; padding:0; margin:0; display:grid; gap:.55rem; }
        .note { background:var(--surface); border-radius:14px; padding:.75rem 1rem;
                box-shadow:0 1px 2px rgba(0,0,0,.04); }
        .note-head { display:flex; justify-content:space-between; align-items:baseline; gap:.5rem; }
        .note-title { font-weight:600; font-size:.95rem; }
        .note-status { font-size:.62rem; font-weight:700; padding:.12rem .45rem; border-radius:6px;
                       letter-spacing:.05em; text-transform:uppercase;
                       background:var(--bg); color:var(--ink-soft); }
        .note-status.status-current, .note-status.status-final { background:var(--ok-soft); color:var(--ok); }
        .note-status.status-entered-in-error { background:var(--crit-soft); color:var(--crit); }
        .note-status.status-superseded { background:var(--warn-soft); color:var(--warn); }
        .note-meta { display:flex; gap:.6rem; font-size:.8rem; color:var(--ink-muted); margin-top:.2rem; }
        .empty { background:var(--surface); border-radius:14px; padding:1rem 1.25rem;
                 color:var(--ink-muted); font-size:.92rem; box-shadow:0 1px 2px rgba(0,0,0,.04); }

        .signin-pane { grid-column: 1 / -1; background:var(--surface); border-radius:14px;
                       padding:1.5rem 1.75rem; box-shadow:0 1px 2px rgba(0,0,0,.04); max-width:60ch; }
        .signin-pane h2 { font-size:1.1rem; margin:0 0 .5rem; }
        .signin-pane p { margin:.4rem 0; color:var(--ink-soft); }
        .signin-pane .btn { display:inline-block; text-decoration:none; margin-right:.5rem; }

        .banner { padding:.7rem 1rem; border-radius:10px; margin-bottom:1rem; font-size:.9rem;
                  grid-column: 1 / -1; }
        .banner.ok { background:var(--ok-soft); color:var(--ok); }
        .banner.err { background:var(--crit-soft); color:var(--crit); }
        .banner code { font-family:ui-monospace,'SF Mono',Menlo,monospace; font-size:.85rem;
                       background:rgba(0,0,0,.06); padding:.05rem .35rem; border-radius:4px; }
        .link-back { display:inline-block; margin-top:.5rem; color:var(--info); text-decoration:none; }
        .link-back:hover { text-decoration:underline; }

        @media (max-width: 880px) { .notes-main { grid-template-columns: 1fr; } }
    </style>";

    private static string WebEncode(string? s) => WebUtility.HtmlEncode(s ?? string.Empty);
}

public sealed record NoteEntryView(
    string PatientId,
    string PatientDisplayName,
    string? PatientSubline,
    NoteDraft Draft,
    IReadOnlyList<NoteSummary> History,
    NoteEntryStatus Status,
    string? Message,
    string? ChartError,
    string? WrittenId);

public enum NoteEntryStatus { Empty, NotAuthorised, Failed, SignedOk }
