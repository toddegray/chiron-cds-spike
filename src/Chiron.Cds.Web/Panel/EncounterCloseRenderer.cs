using System.Globalization;
using System.Net;
using System.Text;
using Chiron.Cds.Web.SmartLaunch;

namespace Chiron.Cds.Web.Panel;

/// <summary>Renders the per-patient Sign-off tab.</summary>
internal static class EncounterCloseRenderer
{
    public static string Render(SignOffView view, string navBar, IReadOnlyList<ChartTab> chartTabs)
    {
        ArgumentNullException.ThrowIfNull(view);
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.Append("<title>Sign off — ").Append(WebEncode(view.PatientDisplayName)).Append("</title>");
        sb.Append(InlineCss());
        sb.Append("</head><body>");
        sb.Append("<nav class=\"navbar\">").Append(navBar).Append("</nav>");
        RenderHeader(sb, view, chartTabs);

        sb.Append("<main class=\"signoff-main\">");
        switch (view.Status)
        {
            case SignOffStatus.ClosedOk:
                RenderClosedBanner(sb, view);
                break;
            case SignOffStatus.NotAuthorised:
                RenderNotAuthorised(sb, view);
                break;
            default:
                if (!string.IsNullOrEmpty(view.Message))
                {
                    var cls = view.Status switch
                    {
                        SignOffStatus.Failed => "err",
                        SignOffStatus.AlreadyClosed => "info",
                        _ => "info",
                    };
                    sb.Append("<div class=\"banner ").Append(cls).Append("\">")
                      .Append(WebEncode(view.Message)).Append("</div>");
                }
                if (!string.IsNullOrEmpty(view.PageError))
                    sb.Append("<div class=\"banner err\">").Append(WebEncode(view.PageError)).Append("</div>");
                RenderEncounters(sb, view);
                break;
        }
        sb.Append("</main></body></html>");
        return sb.ToString();
    }

    private static void RenderHeader(StringBuilder sb, SignOffView view, IReadOnlyList<ChartTab> chartTabs)
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

    private static void RenderClosedBanner(StringBuilder sb, SignOffView view)
    {
        sb.Append("<div class=\"banner ok\">Encounter <code>").Append(WebEncode(view.WrittenId ?? "(unknown)"))
          .Append("</code> closed.</div>");
        sb.Append("<p><a class=\"link-back\" href=\"/app/patient/")
          .Append(Uri.EscapeDataString(view.PatientId)).Append("\">← Back to Visit Brief</a> ");
        sb.Append("<a class=\"link-back\" href=\"/app/panel\">→ Next patient</a></p>");
    }

    private static void RenderNotAuthorised(StringBuilder sb, SignOffView view)
    {
        sb.Append("<section class=\"signin-pane\">");
        sb.Append("<h2>Sign in to close encounters</h2>");
        sb.Append("<p>Updating an <code>Encounter</code> resource requires an active SMART on FHIR session.</p>");
        sb.Append("<p><a class=\"btn primary\" href=\"/smart/launch\">Start SMART launch</a> ");
        sb.Append("<a class=\"btn secondary\" href=\"/app/patient/")
          .Append(Uri.EscapeDataString(view.PatientId)).Append("/signoff\">Back</a></p>");
        sb.Append("</section>");
    }

    private static void RenderEncounters(StringBuilder sb, SignOffView view)
    {
        sb.Append("<section class=\"encounters-pane\">");
        sb.Append("<h2>Recent encounters</h2>");
        if (view.Encounters.Count == 0)
        {
            sb.Append("<div class=\"empty\">No encounters on file for this patient.</div>");
            sb.Append("</section>");
            return;
        }
        sb.Append("<ul class=\"enc-list\">");
        foreach (var e in view.Encounters) RenderEncounter(sb, e, view.PatientId);
        sb.Append("</ul></section>");
    }

    private static void RenderEncounter(StringBuilder sb, EncounterSummary e, string patientId)
    {
        sb.Append("<li class=\"enc");
        if (e.IsInProgress) sb.Append(" active");
        sb.Append("\">");
        sb.Append("<div class=\"enc-head\">");
        sb.Append("<span class=\"enc-type\">").Append(WebEncode(e.Type)).Append("</span>");
        sb.Append("<span class=\"enc-status status-").Append(WebEncode(e.Status.ToLowerInvariant()))
          .Append("\">").Append(WebEncode(e.Status)).Append("</span>");
        sb.Append("</div>");
        sb.Append("<div class=\"enc-meta\">");
        if (!string.IsNullOrEmpty(e.Class))
            sb.Append("<span>").Append(WebEncode(e.Class)).Append("</span>");
        sb.Append("<span>").Append(WebEncode(FormatRange(e.PeriodStart, e.PeriodEnd))).Append("</span>");
        sb.Append("</div>");
        if (e.IsInProgress)
        {
            sb.Append("<form method=\"post\" action=\"/app/patient/")
              .Append(Uri.EscapeDataString(patientId)).Append("/signoff\">");
            sb.Append("<input type=\"hidden\" name=\"EncounterId\" value=\"").Append(WebEncode(e.EncounterId)).Append("\" />");
            sb.Append("<button type=\"submit\" class=\"btn primary\">Sign off and close</button>");
            sb.Append("</form>");
        }
        sb.Append("</li>");
    }

    private static string FormatRange(DateTimeOffset? start, DateTimeOffset? end)
    {
        var s = start is null ? "—" : start.Value.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var e = end is null ? "in progress" : end.Value.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return $"{s} → {e}";
    }

    private static string InlineCss() => @"<style>
        :root { --bg:#f5f5f7; --surface:#fff; --ink:#1d1d1f; --ink-soft:#515154; --ink-muted:#86868b;
                --rule:#e5e5e7; --info:#1170d2; --warn:#c25e04; --crit:#d92121;
                --warn-soft:#fff4e3; --info-soft:#e8f1fc; --crit-soft:#fde8e8; --ok:#1f8a47; --ok-soft:#e6f4ec; }
        * { box-sizing: border-box; }
        body { font-family:-apple-system,BlinkMacSystemFont,'SF Pro Text',system-ui,sans-serif;
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

        .signoff-main { max-width:920px; margin:1.5rem auto 3rem; padding:0 1.5rem; }
        .encounters-pane h2 { font-size:.78rem; text-transform:uppercase; letter-spacing:.06em;
                              color:var(--ink-muted); font-weight:600; margin:0 0 .5rem; }
        .empty { background:var(--surface); border-radius:14px; padding:1rem 1.25rem; color:var(--ink-muted);
                 font-size:.92rem; box-shadow:0 1px 2px rgba(0,0,0,.04); }
        .enc-list { list-style:none; padding:0; margin:0; display:grid; gap:.75rem; }
        .enc { background:var(--surface); border-radius:14px; padding:.85rem 1.1rem; box-shadow:0 1px 2px rgba(0,0,0,.04);
               border-left:4px solid var(--rule); }
        .enc.active { border-left-color:var(--info); }
        .enc-head { display:flex; justify-content:space-between; align-items:baseline; gap:.5rem; }
        .enc-type { font-weight:600; font-size:1rem; }
        .enc-status { font-size:.62rem; font-weight:700; padding:.15rem .55rem; border-radius:6px;
                      letter-spacing:.05em; text-transform:uppercase;
                      background:var(--bg); color:var(--ink-soft); }
        .enc-status.status-finished { background:var(--ok-soft); color:var(--ok); }
        .enc-status.status-inprogress, .enc-status.status-in-progress { background:var(--info-soft); color:var(--info); }
        .enc-status.status-cancelled, .enc-status.status-entered-in-error { background:var(--crit-soft); color:var(--crit); }
        .enc-meta { display:flex; gap:.75rem; font-size:.82rem; color:var(--ink-muted); margin-top:.2rem; }
        .enc form { margin-top:.75rem; }

        .btn { padding:.55rem 1.1rem; font-size:.92rem; font-weight:600; border:0; border-radius:8px; cursor:pointer; }
        .btn.primary { background:var(--info); color:#fff; }
        .btn.primary:hover { background:#0c5fb5; }
        .btn.secondary { background:var(--surface); color:var(--ink); border:1px solid var(--rule); }
        .btn.secondary:hover { background:var(--bg); }

        .signin-pane { background:var(--surface); border-radius:14px; padding:1.5rem 1.75rem;
                       box-shadow:0 1px 2px rgba(0,0,0,.04); max-width:60ch; }
        .signin-pane h2 { font-size:1.1rem; margin:0 0 .5rem; }
        .signin-pane p { margin:.4rem 0; color:var(--ink-soft); }
        .signin-pane .btn { display:inline-block; text-decoration:none; margin-right:.5rem; }

        .banner { padding:.7rem 1rem; border-radius:10px; margin-bottom:1rem; font-size:.9rem; }
        .banner.ok { background:var(--ok-soft); color:var(--ok); }
        .banner.err { background:var(--crit-soft); color:var(--crit); }
        .banner.info { background:var(--info-soft); color:var(--info); }
        .banner code { font-family:ui-monospace,'SF Mono',Menlo,monospace; font-size:.85rem;
                       background:rgba(0,0,0,.06); padding:.05rem .35rem; border-radius:4px; }
        .link-back { display:inline-block; margin-top:.5rem; color:var(--info); text-decoration:none; margin-right:1rem; }
        .link-back:hover { text-decoration:underline; }
    </style>";

    private static string WebEncode(string? s) => WebUtility.HtmlEncode(s ?? string.Empty);
}

/// <summary>View bundle for the sign-off page.</summary>
public sealed record SignOffView(
    string PatientId,
    string PatientDisplayName,
    string? PatientSubline,
    IReadOnlyList<EncounterSummary> Encounters,
    SignOffStatus Status,
    string? Message,
    string? PageError,
    string? WrittenId);

public enum SignOffStatus { Empty, ClosedOk, AlreadyClosed, NotAuthorised, Failed }
