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

    public static string Render(
        string heading,
        string subline,
        IReadOnlyList<CdsCard> cards,
        string? banner = null,
        string? navBar = null)
    {
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>");
        sb.Append(WebEncode(heading));
        sb.Append("</title>");
        sb.Append(InlineCss());
        sb.Append("</head><body>");

        if (!string.IsNullOrEmpty(navBar))
            sb.Append("<nav class=\"navbar\">").Append(navBar).Append("</nav>");

        sb.Append("<main>");
        sb.Append("<h1>").Append(WebEncode(heading)).Append("</h1>");

        if (!string.IsNullOrEmpty(banner))
            sb.Append("<div class=\"banner\">").Append(WebEncode(banner)).Append("</div>");

        sb.Append("<p class=\"subline\">").Append(WebEncode(subline)).Append("</p>");

        sb.Append("<p class=\"count\">").Append(cards.Count).Append(" alert(s) fired.</p>");

        foreach (var c in cards)
            RenderCard(sb, c);

        sb.Append("</main></body></html>");
        return sb.ToString();
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
        sb.Append("<header class=\"card-header\">");
        sb.Append("<div class=\"badge ").Append(severityClass).Append("\">")
          .Append(card.Indicator.ToUpperInvariant()).Append("</div>");
        sb.Append("<h2>").Append(WebEncode(card.Summary)).Append("</h2>");
        sb.Append("</header>");

        if (!string.IsNullOrEmpty(card.Uuid))
        {
            sb.Append("<div class=\"fingerprint\"><span class=\"fp-label\">Fingerprint:</span> ");
            sb.Append("<code>").Append(WebEncode(card.Uuid)).Append("</code>");
            sb.Append("<span class=\"fp-explain\">— stable across runs and across the Python/TS engine ports. Override log keys off this.</span>");
            sb.Append("</div>");
        }

        if (!string.IsNullOrEmpty(card.Detail))
        {
            sb.Append("<details open><summary>Derivation &amp; citations</summary>");
            sb.Append("<div class=\"detail\">").Append(Markdown.ToHtml(card.Detail, MarkdownPipeline)).Append("</div>");
            sb.Append("</details>");
        }

        if (card.OverrideReasons is { Count: > 0 })
        {
            sb.Append("<div class=\"overrides\"><strong>Override options:</strong><ul>");
            foreach (var o in card.OverrideReasons)
                sb.Append("<li>").Append(WebEncode(o.Display)).Append(" <code>").Append(WebEncode(o.Code)).Append("</code></li>");
            sb.Append("</ul></div>");
        }

        sb.Append("<div class=\"source\">From <a href=\"")
          .Append(WebEncode(card.Source.Url ?? "#")).Append("\">")
          .Append(WebEncode(card.Source.Label)).Append("</a></div>");

        sb.Append("</article>");
    }

    private static string InlineCss() => @"<style>
        :root {
            --crit: #c0392b; --warn: #d35400; --info: #2778c4; --ok: #27ae60;
            --bg: #fafafa; --fg: #1d1d1f; --muted: #6b7280;
        }
        * { box-sizing: border-box; }
        body { font-family: -apple-system, BlinkMacSystemFont, system-ui, sans-serif;
               margin: 0; background: var(--bg); color: var(--fg); line-height: 1.5; }
        .navbar { background: #1d1d1f; color: #fff; padding: .75rem 1.5rem;
                  display: flex; gap: 1.25rem; align-items: center; font-size: .95rem; }
        .navbar a { color: #fff; text-decoration: none; opacity: .8; }
        .navbar a:hover { opacity: 1; }
        .navbar .brand { font-weight: 600; opacity: 1; }
        main { max-width: 920px; margin: 2rem auto; padding: 0 1.5rem; }
        h1 { font-size: 1.6rem; margin: 0 0 1rem; }
        h2 { font-size: 1.15rem; margin: 0; }
        h3 { font-size: 1rem; margin: 1rem 0 .25rem; color: var(--muted); text-transform: uppercase;
             letter-spacing: .04em; font-weight: 600; }
        .banner { background: #fffbe6; border: 1px solid #d4b106; padding: .65rem .9rem;
                  border-radius: 8px; margin-bottom: 1rem; font-size: .9rem; color: #66510a; }
        .subline { color: var(--muted); margin: 0 0 .5rem; font-size: .9rem; }
        .count { color: var(--muted); font-size: .85rem; margin: 0 0 1.5rem; }
        .card { background: #fff; border-radius: 10px; padding: 1.25rem 1.5rem;
                margin: 1rem 0; box-shadow: 0 1px 3px rgba(0,0,0,.05);
                border-left: 6px solid var(--info); }
        .card.warning { border-left-color: var(--warn); }
        .card.critical { border-left-color: var(--crit); }
        .card-header { display: flex; align-items: center; gap: .75rem; margin-bottom: .5rem; }
        .badge { font-size: .7rem; font-weight: 700; padding: .15rem .5rem;
                 border-radius: 4px; color: #fff; letter-spacing: .05em; }
        .badge.warning { background: var(--warn); }
        .badge.critical { background: var(--crit); }
        .badge.info { background: var(--info); }
        .fingerprint { background: #f3f4f6; padding: .5rem .75rem; border-radius: 6px;
                       font-size: .8rem; margin: .75rem 0; color: var(--muted); }
        .fp-label { font-weight: 600; color: var(--fg); }
        .fp-explain { margin-left: .25rem; }
        .fingerprint code { background: #fff; padding: .1rem .4rem; border-radius: 4px;
                            font-family: ui-monospace, SF Mono, Menlo, monospace;
                            color: var(--fg); font-weight: 600; }
        details { margin: .75rem 0; }
        summary { cursor: pointer; font-weight: 600; padding: .25rem 0;
                  user-select: none; color: var(--info); }
        summary:hover { text-decoration: underline; }
        .detail { padding: .5rem .75rem; background: #f9fafb; border-radius: 6px;
                  margin-top: .5rem; font-size: .92rem; }
        .detail h3 { margin-top: .75rem; }
        .detail ul { padding-left: 1.25rem; margin: .25rem 0; }
        .detail code { background: #e5e7eb; padding: .05rem .35rem;
                       border-radius: 3px; font-family: ui-monospace, SF Mono, Menlo, monospace;
                       font-size: .9em; }
        .detail p { margin: .35rem 0; }
        .detail a { color: var(--info); }
        .overrides { background: #f9fafb; padding: .65rem .9rem; border-radius: 6px;
                     font-size: .85rem; margin: .5rem 0; }
        .overrides ul { padding-left: 1.25rem; margin: .25rem 0 0; }
        .overrides code { font-size: .8em; color: var(--muted); }
        .source { font-size: .8rem; color: var(--muted); margin-top: .75rem;
                  padding-top: .5rem; border-top: 1px solid #e5e7eb; }
        .source a { color: var(--info); text-decoration: none; }
        .source a:hover { text-decoration: underline; }
    </style>";

    private static string WebEncode(string s) => System.Net.WebUtility.HtmlEncode(s);
}
