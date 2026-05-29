using System.Globalization;
using System.Net;
using System.Text;

namespace Chiron.Cds.Web.Panel;

/// <summary>
/// Renders the Results tab's content — lab trends as a stack of cards (latest
/// value + sparkline + history table), then the recent reports as a list —
/// wrapped in the shared <see cref="ChartShell"/>.
/// </summary>
internal static class ResultReviewRenderer
{
    public static string Render(ResultReviewData data, ChartShell.Header header)
    {
        ArgumentNullException.ThrowIfNull(data);
        var sb = new StringBuilder();
        sb.Append("<main class=\"results-main\">");
        if (data.Error is not null)
        {
            sb.Append("<div class=\"banner\">Chart results could not be loaded: ").Append(WebEncode(data.Error)).Append("</div>");
        }
        else
        {
            RenderTrendsSection(sb, data.Trends);
            RenderReportsSection(sb, data.Reports);
        }
        sb.Append("</main>");
        return ChartShell.Page(header, ChartShell.Tab.Results,
            "Results — " + data.Demographics.DisplayName, sb.ToString(), ContentCss);
    }

    private static void RenderTrendsSection(StringBuilder sb, IReadOnlyList<LabTrend> trends)
    {
        sb.Append("<section class=\"section\"><h2>Lab trends</h2>");
        if (trends.Count == 0)
        {
            sb.Append("<div class=\"empty\">No lab observations on file for this patient.</div>");
            sb.Append("</section>");
            return;
        }
        sb.Append("<div class=\"trend-grid\">");
        foreach (var trend in trends) RenderTrendCard(sb, trend);
        sb.Append("</div></section>");
    }

    private static void RenderTrendCard(StringBuilder sb, LabTrend trend)
    {
        var latest = trend.Points.FirstOrDefault();
        sb.Append("<article class=\"trend-card");
        if (latest?.IsAbnormal == true) sb.Append(" abnormal");
        sb.Append("\">");
        sb.Append("<header class=\"trend-head\">");
        sb.Append("<span class=\"trend-title\">").Append(WebEncode(trend.Title)).Append("</span>");
        if (!string.IsNullOrEmpty(trend.Loinc))
            sb.Append("<span class=\"trend-loinc\">LOINC ").Append(WebEncode(trend.Loinc)).Append("</span>");
        sb.Append("</header>");

        if (latest is not null)
        {
            sb.Append("<div class=\"trend-hero\">");
            sb.Append("<span class=\"trend-value\">").Append(WebEncode(latest.Value)).Append("</span>");
            if (!string.IsNullOrEmpty(latest.Unit))
                sb.Append("<span class=\"trend-unit\">").Append(WebEncode(latest.Unit)).Append("</span>");
            if (latest.IsAbnormal)
                sb.Append("<span class=\"abnormal-pill\">Abnormal</span>");
            sb.Append("</div>");
            sb.Append("<div class=\"trend-when\">")
              .Append(WebEncode(FormatWhen(latest.EffectiveAt)))
              .Append("</div>");
        }

        if (trend.Points.Count > 1)
        {
            RenderSparkline(sb, trend.Points);
            sb.Append("<ol class=\"trend-history\">");
            foreach (var p in trend.Points)
            {
                sb.Append("<li class=\"trend-row");
                if (p.IsAbnormal) sb.Append(" abnormal");
                sb.Append("\">");
                sb.Append("<span class=\"trend-row-when\">").Append(WebEncode(FormatWhen(p.EffectiveAt))).Append("</span>");
                sb.Append("<span class=\"trend-row-value\">").Append(WebEncode(p.Value));
                if (!string.IsNullOrEmpty(p.Unit))
                    sb.Append(' ').Append(WebEncode(p.Unit));
                sb.Append("</span>");
                sb.Append("</li>");
            }
            sb.Append("</ol>");
        }
        sb.Append("</article>");
    }

    private static void RenderSparkline(StringBuilder sb, IReadOnlyList<TrendPoint> points)
    {
        // Sparkline: oldest on the left, newest on the right.
        var numericValues = points
            .Reverse()
            .Select(p => double.TryParse(p.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : (double?)null)
            .ToArray();
        if (numericValues.All(v => v is null)) return;

        const int W = 220, H = 36, Pad = 4;
        var defined = numericValues.Where(v => v is not null).Select(v => v!.Value).ToArray();
        var min = defined.Min();
        var max = defined.Max();
        var range = max - min;
        if (range == 0) range = 1;

        sb.Append("<svg class=\"sparkline\" viewBox=\"0 0 ").Append(W).Append(' ').Append(H)
          .Append("\" preserveAspectRatio=\"none\" role=\"img\" aria-label=\"Trend sparkline\">");
        sb.Append("<polyline fill=\"none\" stroke-width=\"2\" points=\"");
        var stepX = numericValues.Length > 1 ? (W - 2 * Pad) / (double)(numericValues.Length - 1) : 0;
        for (var i = 0; i < numericValues.Length; i++)
        {
            if (numericValues[i] is null) continue;
            var x = Pad + i * stepX;
            var y = H - Pad - (numericValues[i]!.Value - min) / range * (H - 2 * Pad);
            if (i > 0) sb.Append(' ');
            sb.Append(x.ToString("0.##", CultureInfo.InvariantCulture)).Append(',')
              .Append(y.ToString("0.##", CultureInfo.InvariantCulture));
        }
        sb.Append("\" /></svg>");
    }

    private static void RenderReportsSection(StringBuilder sb, IReadOnlyList<ReportSummary> reports)
    {
        sb.Append("<section class=\"section\"><h2>Recent reports</h2>");
        if (reports.Count == 0)
        {
            sb.Append("<div class=\"empty\">No diagnostic reports on file for this patient.</div>");
            sb.Append("</section>");
            return;
        }
        sb.Append("<ul class=\"report-list\">");
        foreach (var r in reports) RenderReport(sb, r);
        sb.Append("</ul></section>");
    }

    private static void RenderReport(StringBuilder sb, ReportSummary r)
    {
        sb.Append("<li class=\"report\">");
        sb.Append("<div class=\"report-head\">");
        sb.Append("<span class=\"report-title\">").Append(WebEncode(r.Title)).Append("</span>");
        sb.Append("<span class=\"report-status status-").Append(WebEncode(r.Status)).Append("\">")
          .Append(WebEncode(r.Status)).Append("</span>");
        sb.Append("</div>");
        sb.Append("<div class=\"report-meta\">");
        if (!string.IsNullOrEmpty(r.Category))
            sb.Append("<span class=\"report-category\">").Append(WebEncode(r.Category)).Append("</span>");
        sb.Append("<span class=\"report-issued\">").Append(WebEncode(FormatWhen(r.IssuedAt))).Append("</span>");
        sb.Append("</div>");
        if (!string.IsNullOrEmpty(r.Conclusion))
            sb.Append("<p class=\"report-conclusion\">").Append(WebEncode(r.Conclusion)).Append("</p>");
        sb.Append("</li>");
    }

    private static string FormatWhen(DateTimeOffset? when)
    {
        if (when is null) return "—";
        var d = when.Value.UtcDateTime;
        return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    // Content-only styles; shell chrome (vars, body, top bar, rail, tabs) is in ChartShell.
    private const string ContentCss = @"
        .banner { background: var(--warn-soft); border: 1px solid #f0c46a; padding: .65rem .9rem;
                  border-radius: 8px; max-width: 70ch; margin: 1rem 0; color: var(--warn); }

        .results-main { margin: 1rem 0 2rem; display: grid; grid-template-columns: 1fr; gap: 2rem; }
        .section h2 { font-size: 1rem; text-transform: uppercase; letter-spacing: .06em;
                      color: var(--ink-muted); font-weight: 600; margin: 0 0 .75rem; }
        .empty { background: var(--surface); border-radius: 14px; padding: 1.25rem 1.5rem;
                 color: var(--ink-muted); font-size: .92rem; box-shadow: 0 1px 2px rgba(0,0,0,.04); }

        .trend-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(260px, 1fr)); gap: 1rem; }
        .trend-card { background: var(--surface); border-radius: 14px; padding: 1rem 1.1rem;
                      box-shadow: 0 1px 2px rgba(0,0,0,.04); display: flex; flex-direction: column; gap: .5rem; }
        .trend-card.abnormal { box-shadow: 0 1px 2px rgba(217, 33, 33, .15), inset 4px 0 0 var(--crit); }
        .trend-head { display: flex; justify-content: space-between; align-items: baseline; gap: .5rem; }
        .trend-title { font-weight: 600; font-size: .98rem; }
        .trend-loinc { font-family: ui-monospace, 'SF Mono', Menlo, monospace; font-size: .72rem; color: var(--ink-muted); }
        .trend-hero { display: flex; align-items: baseline; gap: .35rem; }
        .trend-value { font-size: 1.8rem; font-weight: 700; letter-spacing: -.02em; color: var(--ink); }
        .trend-card.abnormal .trend-value { color: var(--crit); }
        .trend-unit { color: var(--ink-muted); font-size: .85rem; }
        .abnormal-pill { background: #fde8e8; color: var(--crit); border-radius: 999px; padding: .1rem .55rem;
                         font-size: .68rem; font-weight: 700; letter-spacing: .05em; text-transform: uppercase; margin-left: .35rem; }
        .trend-when { font-size: .8rem; color: var(--ink-muted); }
        .sparkline { width: 100%; height: 36px; stroke: var(--info); margin-top: .25rem; }
        .trend-card.abnormal .sparkline { stroke: var(--crit); }
        .trend-history { list-style: none; padding: 0; margin: .25rem 0 0;
                         border-top: 1px solid var(--rule); }
        .trend-row { display: flex; justify-content: space-between; padding: .3rem 0;
                     border-bottom: 1px solid var(--rule); font-size: .85rem; }
        .trend-row:last-child { border-bottom: 0; }
        .trend-row.abnormal { color: var(--crit); font-weight: 600; }
        .trend-row-when { color: var(--ink-muted); }
        .trend-row-value { font-variant-numeric: tabular-nums; }

        .report-list { list-style: none; padding: 0; margin: 0; display: grid; gap: .75rem; }
        .report { background: var(--surface); border-radius: 14px; padding: .85rem 1.1rem;
                  box-shadow: 0 1px 2px rgba(0,0,0,.04); }
        .report-head { display: flex; justify-content: space-between; align-items: baseline; gap: .75rem; }
        .report-title { font-weight: 600; font-size: 1rem; }
        .report-status { font-size: .65rem; font-weight: 700; padding: .15rem .55rem;
                         border-radius: 6px; letter-spacing: .05em; text-transform: uppercase;
                         background: var(--bg); color: var(--ink-soft); }
        .report-status.status-final { background: #e6f4ec; color: var(--ok); }
        .report-status.status-amended, .report-status.status-corrected { background: var(--warn-soft); color: var(--warn); }
        .report-status.status-preliminary, .report-status.status-partial { background: #e8f1fc; color: var(--info); }
        .report-meta { display: flex; gap: .75rem; font-size: .82rem; color: var(--ink-muted); margin-top: .2rem; }
        .report-conclusion { margin: .55rem 0 0; font-size: .92rem; color: var(--ink-soft); }
    ";

    private static string WebEncode(string? s) => WebUtility.HtmlEncode(s ?? string.Empty);
}
