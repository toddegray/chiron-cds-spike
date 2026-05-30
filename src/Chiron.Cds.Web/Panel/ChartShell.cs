using System.Net;
using System.Text;

namespace Chiron.Cds.Web.Panel;

/// <summary>
/// The shared patient-chart shell: the dark identity top bar, the left icon
/// rail, and the section tab strip. Every chart route renders its content
/// inside this same shell — only the content region differs as the clinician
/// moves between Summary, Labs &amp; Results, Orders, Notes, and Sign off — so
/// the application frame never changes underfoot.
/// </summary>
internal static class ChartShell
{
    internal enum Tab { Summary, Results, Orders, Notes, SignOff }

    /// <summary>Patient identity shown in the top bar — supplied by the controller for every route.</summary>
    internal sealed record Header(string PatientId, string DisplayName, string AgeSex, string? DateOfBirth, string? Mrn);

    private static readonly (Tab Tab, string Label, string Slug, string Glyph)[] Sections =
    {
        (Tab.Summary, "Summary", "", "▤"),
        (Tab.Results, "Labs & Results", "/results", "▥"),
        (Tab.Orders, "Orders", "/orders", "℞"),
        (Tab.Notes, "Notes", "/notes", "✎"),
        (Tab.SignOff, "Sign off", "/signoff", "✓"),
    };

    /// <summary>
    /// Wrap section content in the full chart page. <paramref name="contentHtml"/>
    /// is dropped into the workspace below the tab strip; <paramref name="extraCss"/>
    /// carries the section's own content styles (the shell styles are always present).
    /// </summary>
    public static string Page(Header header, Tab active, string title, string contentHtml, string extraCss = "")
    {
        ArgumentNullException.ThrowIfNull(header);
        var id = Uri.EscapeDataString(header.PatientId);
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.Append("<title>").Append(Enc(title)).Append("</title>");
        sb.Append("<style>").Append(ShellCss).Append(extraCss).Append("</style>");
        sb.Append("</head><body>");

        RenderTopBar(sb, header);
        sb.Append("<div class=\"shell\">");
        RenderIconRail(sb, id, active);
        sb.Append("<div class=\"workspace\">");
        RenderTabs(sb, id, active);
        sb.Append(contentHtml);
        sb.Append("</div></div></body></html>");
        return sb.ToString();
    }

    private static void RenderTopBar(StringBuilder sb, Header h)
    {
        sb.Append("<header class=\"topbar\">");
        sb.Append("<div class=\"brand\"><span class=\"brand-mark\">✚</span> CDS</div>");
        sb.Append("<div class=\"patient-id\">");
        sb.Append("<div class=\"pid-name\">").Append(Enc(h.DisplayName)).Append("</div>");
        sb.Append("<div class=\"pid-sub\">");
        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(h.AgeSex)) parts.Add(h.AgeSex);
        if (!string.IsNullOrWhiteSpace(h.DateOfBirth)) parts.Add("DOB " + h.DateOfBirth);
        if (!string.IsNullOrWhiteSpace(h.Mrn)) parts.Add("MRN " + h.Mrn);
        sb.Append(Enc(string.Join("  •  ", parts)));
        sb.Append("</div></div>");
        sb.Append("<div class=\"topbar-meta\"><div>Epic Sandbox · SMART on FHIR</div>");
        sb.Append("<div class=\"meta-dim\">Last updated ")
          .Append(Enc(DateTimeOffset.Now.ToString("MM/dd/yyyy HH:mm")))
          .Append("</div></div>");
        sb.Append("</header>");
    }

    private static void RenderIconRail(StringBuilder sb, string id, Tab active)
    {
        sb.Append("<nav class=\"icon-rail\" aria-label=\"Chart sections\">");
        foreach (var (tab, label, slug, glyph) in Sections)
            RailIcon(sb, $"/app/patient/{id}{slug}", label, glyph, tab == active);
        sb.Append("<div class=\"rail-spacer\"></div>");
        RailIcon(sb, "/app/panel", "Panel", "☰", active: false);
        RailIcon(sb, "/app/search", "Find patient", "⌕", active: false);
        sb.Append("</nav>");
    }

    private static void RailIcon(StringBuilder sb, string href, string label, string glyph, bool active)
    {
        sb.Append("<a class=\"rail-icon").Append(active ? " active" : "").Append("\" href=\"")
          .Append(Enc(href)).Append("\" title=\"").Append(Enc(label))
          .Append("\" aria-label=\"").Append(Enc(label)).Append("\">").Append(glyph).Append("</a>");
    }

    private static void RenderTabs(StringBuilder sb, string id, Tab active)
    {
        sb.Append("<nav class=\"tabs\" aria-label=\"Chart sections\">");
        foreach (var (tab, label, slug, _) in Sections)
            sb.Append("<a class=\"tab").Append(tab == active ? " active" : "").Append("\" href=\"")
              .Append("/app/patient/").Append(id).Append(Enc(slug)).Append("\">").Append(Enc(label)).Append("</a>");
        sb.Append("</nav>");
    }

    private static string Enc(string s) => WebUtility.HtmlEncode(s);

    // Shared chrome styles. Section content styles are appended per-route via extraCss.
    internal const string ShellCss = @"
        :root {
            --navy: #14294d; --navy-2: #1c3a63; --rail: #0f2038;
            --bg: #eef1f5; --surface: #ffffff; --ink: #1d2733; --ink-soft: #51606f;
            --ink-muted: #8593a3; --rule: #e2e7ee; --accent: #1f6fd6;
            --crit: #d92121; --crit-soft: #fdecec; --warn: #c2640a; --warn-soft: #fdf2e3;
            --info: #1f6fd6; --info-soft: #e9f2fd; --ok: #1f8a47; --ok-soft: #e6f4ec;
        }
        * { box-sizing: border-box; }
        body { margin: 0; background: var(--bg); color: var(--ink); line-height: 1.45;
               font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', 'Inter', system-ui, sans-serif;
               -webkit-font-smoothing: antialiased; font-size: 14px; }
        a { color: var(--accent); }

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
                     flex-direction: column; align-items: center; padding: .6rem 0; gap: .35rem; }
        .icon-rail .rail-spacer { flex: 1; }
        .rail-icon { width: 38px; height: 38px; border-radius: 9px; display: flex; align-items: center;
                     justify-content: center; color: #9fb2c9; text-decoration: none; font-size: 1.1rem; }
        .rail-icon:hover { background: rgba(255,255,255,.08); color: #fff; }
        .rail-icon.active { background: rgba(95,176,255,.18); color: #fff; }

        .workspace { flex: 1; min-width: 0; padding: 0 1.25rem 2rem; }
        .tabs { display: flex; gap: .25rem; flex-wrap: wrap; border-bottom: 1px solid var(--rule);
                background: var(--surface); margin: 0 -1.25rem 0; padding: 0 1.25rem;
                position: sticky; top: 0; z-index: 2; }
        .tab { padding: .8rem .9rem; font-size: .8rem; font-weight: 600; letter-spacing: .03em;
               text-transform: uppercase; color: var(--ink-muted); text-decoration: none;
               border-bottom: 2.5px solid transparent; }
        .tab:hover { color: var(--ink-soft); }
        .tab.active { color: var(--accent); border-bottom-color: var(--accent); }
        .chart-title-row h1 { font-size: 1.15rem; font-weight: 700; margin: 1rem 0 .9rem; }
    ";
}
