using System.Globalization;
using System.Net;
using System.Text;

namespace Chiron.Cds.Web.Panel;

/// <summary>
/// Renders the lab and imaging order sub-pages under the Orders rail
/// step. Shares the order sub-nav (Medications / Labs / Imaging) with the
/// medication order page so the user navigates without losing context.
/// </summary>
internal static class ServiceRequestRenderer
{
    public static string Render(ServiceRequestView view, ChartShell.Header header)
    {
        ArgumentNullException.ThrowIfNull(view);
        var sb = new StringBuilder();
        sb.Append("<main class=\"orders-main\">");
        OrderSubNav.Render(sb, view.PatientId,
            view.Category == ServiceRequestCategory.Laboratory ? OrderSubNavActive.Labs : OrderSubNavActive.Imaging);
        switch (view.Status)
        {
            case ServiceRequestStatus.SignedOk:
                RenderSignedBanner(sb, view);
                break;
            case ServiceRequestStatus.NotAuthorised:
                RenderNotAuthorised(sb, view);
                break;
            default:
                if (!string.IsNullOrEmpty(view.Message))
                {
                    var cls = view.Status == ServiceRequestStatus.Failed ? "err" : "info";
                    sb.Append("<div class=\"banner ").Append(cls).Append("\">")
                      .Append(WebEncode(view.Message)).Append("</div>");
                }
                if (!string.IsNullOrEmpty(view.PageError))
                    sb.Append("<div class=\"banner err\">").Append(WebEncode(view.PageError)).Append("</div>");
                RenderForm(sb, view);
                RenderHistory(sb, view);
                break;
        }
        sb.Append("</main>");
        return ChartShell.Page(header, ChartShell.Tab.Orders,
            view.HeadingLabel + " — " + view.PatientDisplayName, sb.ToString(), ContentCss);
    }

    private static void RenderSignedBanner(StringBuilder sb, ServiceRequestView view)
    {
        sb.Append("<div class=\"banner ok\">").Append(WebEncode(view.HeadingLabel))
          .Append(" order signed — server-assigned id <code>").Append(WebEncode(view.WrittenId ?? "(unknown)"))
          .Append("</code>.</div>");
        sb.Append("<p><a class=\"link-back\" href=\"/app/patient/")
          .Append(Uri.EscapeDataString(view.PatientId)).Append("/orders/")
          .Append(view.Category == ServiceRequestCategory.Laboratory ? "labs" : "imaging")
          .Append("\">Place another " + (view.Category == ServiceRequestCategory.Laboratory ? "lab" : "imaging")).Append(" order</a> ");
        sb.Append("<a class=\"link-back\" href=\"/app/patient/").Append(Uri.EscapeDataString(view.PatientId))
          .Append("\">← Back to Visit Brief</a></p>");
    }

    private static void RenderNotAuthorised(StringBuilder sb, ServiceRequestView view)
    {
        var slug = view.Category == ServiceRequestCategory.Laboratory ? "labs" : "imaging";
        sb.Append("<section class=\"signin-pane\">");
        sb.Append("<h2>Sign in to place ").Append(WebEncode(view.HeadingLabel.ToLowerInvariant())).Append(" orders</h2>");
        sb.Append("<p>Submitting a <code>ServiceRequest</code> writes to the EHR's authenticated FHIR endpoint, ");
        sb.Append("which requires an active SMART on FHIR session.</p>");
        sb.Append("<p><a class=\"btn primary\" href=\"/smart/launch\">Start SMART launch</a> ");
        sb.Append("<a class=\"btn secondary\" href=\"/app/patient/").Append(Uri.EscapeDataString(view.PatientId))
          .Append("/orders/").Append(slug).Append("\">Back to draft</a></p>");
        sb.Append("</section>");
    }

    private static void RenderForm(StringBuilder sb, ServiceRequestView view)
    {
        var d = view.Draft;
        var slug = view.Category == ServiceRequestCategory.Laboratory ? "labs" : "imaging";
        var promptHint = view.Category == ServiceRequestCategory.Laboratory
            ? "E.g. CBC with diff, Lipid panel, HbA1c, BMP, TSH."
            : "E.g. Chest X-ray PA/LAT, MRI brain w/wo contrast, CT abdomen.";
        sb.Append("<form method=\"post\" action=\"/app/patient/")
          .Append(Uri.EscapeDataString(view.PatientId)).Append("/orders/").Append(slug)
          .Append("\" class=\"sr-form\">");
        sb.Append("<section class=\"form-section\">");
        sb.Append("<h2>").Append(WebEncode(view.HeadingLabel)).Append("</h2>");
        TextField(sb, "order-text", "OrderText", view.Category == ServiceRequestCategory.Laboratory ? "Test" : "Study",
            d.OrderText, required: true, hint: promptHint);
        TextField(sb, "reason", "Reason", "Indication", d.Reason, hint: "Clinical reason for the order.");
        SelectField(sb, "priority", "Priority", "Priority", d.Priority,
            new[] { ("routine", "Routine"), ("urgent", "Urgent"), ("stat", "Stat") });
        sb.Append("</section>");
        sb.Append("<div class=\"form-actions\">");
        sb.Append("<button type=\"submit\" class=\"btn primary\">Sign order</button>");
        sb.Append("</div></form>");
    }

    private static void RenderHistory(StringBuilder sb, ServiceRequestView view)
    {
        sb.Append("<aside class=\"history-pane\"><h2>Prior ")
          .Append(WebEncode(view.HeadingLabel.ToLowerInvariant())).Append(" orders</h2>");
        if (view.History.Count == 0)
        {
            sb.Append("<div class=\"empty\">No prior ").Append(WebEncode(view.HeadingLabel.ToLowerInvariant()))
              .Append(" orders on file.</div>");
        }
        else
        {
            sb.Append("<ul class=\"sr-list\">");
            foreach (var h in view.History) RenderHistoryRow(sb, h);
            sb.Append("</ul>");
        }
        sb.Append("</aside>");
    }

    private static void RenderHistoryRow(StringBuilder sb, ServiceRequestSummary h)
    {
        sb.Append("<li class=\"sr\"><div class=\"sr-head\">");
        sb.Append("<span class=\"sr-name\">").Append(WebEncode(h.Name)).Append("</span>");
        sb.Append("<span class=\"sr-status status-").Append(WebEncode(h.Status.ToLowerInvariant()))
          .Append("\">").Append(WebEncode(h.Status)).Append("</span>");
        sb.Append("</div><div class=\"sr-meta\">");
        if (!string.IsNullOrEmpty(h.Priority))
            sb.Append("<span>").Append(WebEncode(h.Priority)).Append("</span>");
        if (!string.IsNullOrEmpty(h.Reason))
            sb.Append("<span>").Append(WebEncode(h.Reason)).Append("</span>");
        if (h.OccurrenceAt is not null)
            sb.Append("<span>")
              .Append(WebEncode(h.OccurrenceAt.Value.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)))
              .Append("</span>");
        sb.Append("</div></li>");
    }

    private static void TextField(
        StringBuilder sb, string id, string name, string label, string? value,
        bool required = false, string? hint = null)
    {
        sb.Append("<label class=\"field\" for=\"").Append(id).Append("\">");
        sb.Append("<span class=\"field-label\">").Append(WebEncode(label));
        if (required) sb.Append(" <span class=\"req\">*</span>");
        sb.Append("</span>");
        sb.Append("<input type=\"text\" id=\"").Append(id).Append("\" name=\"").Append(name).Append("\" value=\"")
          .Append(WebEncode(value ?? string.Empty)).Append("\"");
        if (required) sb.Append(" required");
        sb.Append(" />");
        if (!string.IsNullOrEmpty(hint))
            sb.Append("<span class=\"hint\">").Append(WebEncode(hint)).Append("</span>");
        sb.Append("</label>");
    }

    private static void SelectField(
        StringBuilder sb, string id, string name, string label, string? current,
        IReadOnlyList<(string Id, string Display)> options)
    {
        sb.Append("<label class=\"field\" for=\"").Append(id).Append("\">");
        sb.Append("<span class=\"field-label\">").Append(WebEncode(label)).Append("</span>");
        sb.Append("<select id=\"").Append(id).Append("\" name=\"").Append(name).Append("\">");
        foreach (var (oid, display) in options)
        {
            sb.Append("<option value=\"").Append(WebEncode(oid)).Append("\"")
              .Append(string.Equals(oid, current, StringComparison.Ordinal) ? " selected" : "")
              .Append(">").Append(WebEncode(display)).Append("</option>");
        }
        sb.Append("</select></label>");
    }

    // Content-only styles; shell chrome (vars, body, top bar, rail, tabs) is in ChartShell.
    private const string ContentCss = @"
        .orders-main { margin:1rem 0 2rem; }
        .order-subnav { display:flex; gap:.5rem; margin-bottom:1.25rem; }
        .order-subnav a { padding:.35rem .8rem; font-size:.85rem; color:var(--ink-soft); text-decoration:none;
                          border-radius:999px; border:1px solid var(--rule); background:var(--surface); }
        .order-subnav a:hover { color:var(--ink); }
        .order-subnav a.active { color:#fff; background:var(--ink); border-color:var(--ink); font-weight:600; }

        .sr-form { display:flex; flex-direction:column; gap:1rem; max-width:60ch; }
        .form-section { background:var(--surface); border-radius:14px; padding:1rem 1.25rem;
                        box-shadow:0 1px 2px rgba(0,0,0,.04); }
        .form-section h2 { font-size:1rem; margin:0 0 .5rem; }
        .field { display:flex; flex-direction:column; margin-bottom:.85rem; }
        .field-label { font-size:.85rem; font-weight:500; color:var(--ink); margin-bottom:.25rem; }
        .field input[type=text], .field select { padding:.55rem .7rem; font-size:.95rem; border:1px solid var(--rule);
                                                  border-radius:8px; background:#fff; color:var(--ink); }
        .field input[type=text]:focus, .field select:focus { outline:2px solid var(--info); outline-offset:1px; }
        .hint { font-size:.78rem; color:var(--ink-muted); margin-top:.2rem; }
        .req { color:var(--crit); }
        .form-actions { display:flex; justify-content:flex-end; padding-top:.25rem; }
        .btn { padding:.55rem 1.1rem; font-size:.92rem; font-weight:600; border:0; border-radius:8px; cursor:pointer; }
        .btn.primary { background:var(--info); color:#fff; }
        .btn.primary:hover { background:#0c5fb5; }
        .btn.secondary { background:var(--surface); color:var(--ink); border:1px solid var(--rule); }
        .btn.secondary:hover { background:var(--bg); }

        .history-pane { margin-top:2rem; }
        .history-pane h2 { font-size:.78rem; text-transform:uppercase; letter-spacing:.06em;
                           color:var(--ink-muted); font-weight:600; margin:0 0 .5rem; }
        .empty { background:var(--surface); border-radius:14px; padding:1rem 1.25rem;
                 color:var(--ink-muted); font-size:.92rem; box-shadow:0 1px 2px rgba(0,0,0,.04); }
        .sr-list { list-style:none; padding:0; margin:0; display:grid; gap:.55rem; }
        .sr { background:var(--surface); border-radius:14px; padding:.75rem 1rem; box-shadow:0 1px 2px rgba(0,0,0,.04); }
        .sr-head { display:flex; justify-content:space-between; align-items:baseline; gap:.5rem; }
        .sr-name { font-weight:600; font-size:.95rem; }
        .sr-status { font-size:.62rem; font-weight:700; padding:.12rem .45rem; border-radius:6px;
                     letter-spacing:.05em; text-transform:uppercase; background:var(--bg); color:var(--ink-soft); }
        .sr-status.status-active { background:var(--info-soft); color:var(--info); }
        .sr-status.status-completed { background:var(--ok-soft); color:var(--ok); }
        .sr-status.status-revoked, .sr-status.status-entered-in-error { background:var(--crit-soft); color:var(--crit); }
        .sr-meta { display:flex; gap:.6rem; font-size:.8rem; color:var(--ink-muted); margin-top:.2rem; }

        .signin-pane { background:var(--surface); border-radius:14px; padding:1.5rem 1.75rem;
                       box-shadow:0 1px 2px rgba(0,0,0,.04); max-width:60ch; }
        .signin-pane h2 { font-size:1.1rem; margin:0 0 .5rem; }
        .signin-pane p { margin:.4rem 0; color:var(--ink-soft); }
        .signin-pane .btn { display:inline-block; text-decoration:none; margin-right:.5rem; }

        .banner { padding:.7rem 1rem; border-radius:10px; margin-bottom:1rem; font-size:.9rem; max-width:60ch; }
        .banner.ok { background:var(--ok-soft); color:var(--ok); }
        .banner.err { background:var(--crit-soft); color:var(--crit); }
        .banner.info { background:var(--info-soft); color:var(--info); }
        .banner code { font-family:ui-monospace,'SF Mono',Menlo,monospace; font-size:.85rem;
                       background:rgba(0,0,0,.06); padding:.05rem .35rem; border-radius:4px; }
        .link-back { display:inline-block; margin-top:.5rem; color:var(--info); text-decoration:none; margin-right:1rem; }
        .link-back:hover { text-decoration:underline; }
    ";

    private static string WebEncode(string? s) => WebUtility.HtmlEncode(s ?? string.Empty);
}

public sealed record ServiceRequestView(
    string PatientId,
    string PatientDisplayName,
    string? PatientSubline,
    ServiceRequestCategory Category,
    ServiceRequestDraft Draft,
    IReadOnlyList<ServiceRequestSummary> History,
    ServiceRequestStatus Status,
    string? Message,
    string? PageError,
    string? WrittenId)
{
    public string HeadingLabel => Category == ServiceRequestCategory.Laboratory ? "Laboratory" : "Imaging";
}

public enum ServiceRequestStatus { Empty, NotAuthorised, Failed, SignedOk }
