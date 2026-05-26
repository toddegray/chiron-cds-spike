using System.Net;
using System.Text;
using Chiron.Cds.Web.CdsHooks.Models;
using Chiron.Cds.Web.Configuration;
using Chiron.Cds.Web.SmartLaunch;
using Markdig;

namespace Chiron.Cds.Web.Panel;

/// <summary>Renders the order-entry form + inline CDS cards + sign UX for one patient.</summary>
internal static class OrderEntryRenderer
{
    private static readonly MarkdownPipeline MarkdownPipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    public static string Render(OrderEntryView view, string navBar, IReadOnlyList<ChartTab> chartTabs)
    {
        ArgumentNullException.ThrowIfNull(view);
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.Append("<title>Order — ").Append(WebEncode(view.PatientDisplayName)).Append("</title>");
        sb.Append(InlineCss());
        sb.Append("</head><body>");
        sb.Append("<nav class=\"navbar\">").Append(navBar).Append("</nav>");

        RenderHeader(sb, view, chartTabs);

        sb.Append("<main class=\"order-main\">");
        // Order sub-nav lives outside the status switch so it renders even
        // on the signed-ok / not-authorised pages — keeps the Medication /
        // Labs / Imaging strip stable across every order interaction.
        sb.Append("<div class=\"order-subnav-host\">");
        OrderSubNav.Render(sb, view.PatientId, OrderSubNavActive.Medication);
        sb.Append("</div>");
        switch (view.Status)
        {
            case OrderEntryStatus.SignedOk:
                RenderSignedBanner(sb, view.WrittenId, view.PatientId);
                break;
            case OrderEntryStatus.NotAuthorised:
                RenderNotAuthorised(sb, view);
                break;
            default:
                RenderResultBanner(sb, view);
                RenderForm(sb, view);
                RenderCards(sb, view);
                break;
        }
        sb.Append("</main></body></html>");
        return sb.ToString();
    }

    private static void RenderNotAuthorised(StringBuilder sb, OrderEntryView view)
    {
        sb.Append("<section class=\"signin-pane\">");
        sb.Append("<h2>Sign in to write orders</h2>");
        sb.Append("<p>Submitting an order writes a real <code>MedicationRequest</code> to the EHR's authenticated FHIR endpoint, ");
        sb.Append("which requires an active SMART on FHIR session.</p>");
        sb.Append("<p><a class=\"btn primary\" href=\"/smart/launch\">Start SMART launch</a> ");
        sb.Append("<a class=\"btn secondary\" href=\"/app/patient/").Append(Uri.EscapeDataString(view.PatientId)).Append("/orders\">Back to draft</a></p>");
        sb.Append("</section>");
    }

    private static void RenderHeader(StringBuilder sb, OrderEntryView view, IReadOnlyList<ChartTab> tabs)
    {
        sb.Append("<header class=\"page-header\"><div class=\"page-header-inner\">");
        sb.Append("<h1>").Append(WebEncode(view.PatientDisplayName)).Append("</h1>");
        if (!string.IsNullOrEmpty(view.PatientSubline))
            sb.Append("<div class=\"demographics\"><span class=\"demo-item\">")
              .Append(WebEncode(view.PatientSubline)).Append("</span></div>");
        sb.Append("<nav class=\"chart-tabs\" aria-label=\"Chart sections\">");
        foreach (var tab in tabs)
        {
            sb.Append("<a class=\"chart-tab");
            if (tab.IsActive) sb.Append(" active");
            sb.Append("\" href=\"").Append(WebEncode(tab.Href)).Append("\">")
              .Append(WebEncode(tab.Label)).Append("</a>");
        }
        sb.Append("</nav>");
        sb.Append("</div></header>");
    }

    private static void RenderSignedBanner(StringBuilder sb, string? writtenId, string patientId)
    {
        sb.Append("<div class=\"banner ok\">Order signed and written to the EHR — server-assigned id ");
        sb.Append("<code>").Append(WebEncode(writtenId ?? "(unknown)")).Append("</code>.</div>");
        sb.Append("<p><a class=\"link-back\" href=\"/app/patient/")
          .Append(Uri.EscapeDataString(patientId)).Append("\">← Back to Visit Brief</a></p>");
    }

    private static void RenderResultBanner(StringBuilder sb, OrderEntryView view)
    {
        switch (view.Status)
        {
            case OrderEntryStatus.Empty:
                return;
            case OrderEntryStatus.Blocked:
                sb.Append("<div class=\"banner warn\">").Append(WebEncode(view.Message ?? string.Empty));
                sb.Append(" Tick each critical card's <strong>Acknowledge</strong> box below, then Sign again.</div>");
                return;
            case OrderEntryStatus.Failed:
                sb.Append("<div class=\"banner err\">").Append(WebEncode(view.Message ?? "")).Append("</div>");
                return;
        }
    }

    private static void RenderForm(StringBuilder sb, OrderEntryView view)
    {
        var d = view.Draft;
        var unacked = CountUnacknowledgedCritical(view);
        sb.Append("<form id=\"order-form\" method=\"post\" action=\"/app/patient/")
          .Append(Uri.EscapeDataString(view.PatientId)).Append("/orders\" class=\"order-form\">");

        sb.Append("<section class=\"form-section\"><h2>Medication</h2>");
        TextField(sb, "drug-name", "DrugName", "Drug name", d.DrugName, required: true,
            hint: "Generic name preferred (e.g. metformin, lisinopril).");
        TextField(sb, "strength", "Strength", "Strength", d.Strength, required: true,
            hint: "Numeric + unit (e.g. 500 mg).");
        TextField(sb, "form", "Form", "Form", d.Form, hint: "Tablet, capsule, solution, etc.");
        sb.Append("</section>");

        sb.Append("<section class=\"form-section\"><h2>Sig</h2>");
        SelectField(sb, "route", "Route", "Route", d.Route,
            new[] { "Oral", "Topical", "Subcutaneous", "Intravenous", "Intramuscular", "Inhaled", "Other" });
        SelectField(sb, "frequency", "Frequency", "Frequency", d.Frequency,
            new[] { "Once daily", "Twice daily", "Three times daily", "Four times daily", "Every 4 hours", "Every 6 hours", "Every 8 hours", "Every 12 hours", "Bedtime" });
        CheckboxField(sb, "as-needed", "AsNeeded", "As needed (PRN)", d.AsNeeded);
        TextField(sb, "prn-reason", "PrnReason", "PRN indication", d.PrnReason,
            hint: "Required when 'As needed' is checked. E.g. pain, nausea.");
        sb.Append("</section>");

        sb.Append("<section class=\"form-section\"><h2>Dispense</h2>");
        TextField(sb, "quantity", "Quantity", "Quantity", d.Quantity, hint: "E.g. 30 tablets / 100 mL.");
        NumberField(sb, "refills", "Refills", "Refills", d.Refills, min: 0, max: 12);
        SelectField(sb, "pharmacy", "PharmacyId", "Pharmacy", d.PharmacyId,
            view.Pharmacies.Select(p => (p.Id, p.DisplayName)).ToArray(),
            allowEmpty: true,
            emptyLabel: "(none selected)");
        CheckboxField(sb, "sub-allowed", "SubstitutionAllowed", "Substitution allowed", d.SubstitutionAllowed);
        TextAreaField(sb, "note", "NoteToPharmacist", "Note to pharmacist", d.NoteToPharmacist);
        sb.Append("</section>");

        // Hidden inputs survive only acks whose card is no longer in the
        // current evaluation — for cards still on screen the checkbox is
        // the single source of truth, so unchecking actually un-ack's.
        var displayedFingerprints = view.Cards
            .Select(c => c.Uuid)
            .Where(uuid => !string.IsNullOrEmpty(uuid))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var fp in view.AcknowledgedFingerprints)
        {
            if (displayedFingerprints.Contains(fp)) continue;
            sb.Append("<input type=\"hidden\" name=\"Acknowledged\" value=\"").Append(WebEncode(fp)).Append("\" />");
        }

        sb.Append("<div class=\"form-actions\">");
        sb.Append("<button type=\"submit\" class=\"btn primary\">");
        sb.Append(unacked > 0
            ? $"Sign with {unacked} acknowledgement{(unacked == 1 ? "" : "s")}"
            : "Sign order");
        sb.Append("</button>");
        sb.Append("</div></form>");
    }

    private static int CountUnacknowledgedCritical(OrderEntryView view) =>
        view.Cards
            .Where(c => string.Equals(c.Indicator, "critical", StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Uuid)
            .Count(uuid => !string.IsNullOrEmpty(uuid)
                           && !view.AcknowledgedFingerprints.Contains(uuid));

    private static void RenderCards(StringBuilder sb, OrderEntryView view)
    {
        if (view.Cards.Count == 0) return;
        sb.Append("<section class=\"cards-panel\">");
        sb.Append("<h2>Inline CDS</h2>");
        foreach (var card in view.Cards) RenderCard(sb, card, view.AcknowledgedFingerprints);
        sb.Append("</section>");
    }

    private static void RenderCard(StringBuilder sb, CdsCard card, IReadOnlySet<string> acknowledged)
    {
        var severity = card.Indicator switch
        {
            "critical" => "critical",
            "warning" => "warning",
            _ => "info",
        };
        var isAcked = !string.IsNullOrEmpty(card.Uuid) && acknowledged.Contains(card.Uuid);
        sb.Append("<article class=\"alert ").Append(severity);
        if (isAcked) sb.Append(" acknowledged");
        sb.Append("\">");
        sb.Append("<header class=\"alert-head\">");
        sb.Append("<span class=\"badge ").Append(severity).Append("\">")
          .Append(card.Indicator.ToUpperInvariant()).Append("</span>");
        sb.Append("<h3>").Append(WebEncode(card.Summary)).Append("</h3>");
        sb.Append("</header>");
        if (!string.IsNullOrEmpty(card.Detail))
            sb.Append("<div class=\"alert-detail\">")
              .Append(Markdown.ToHtml(card.Detail, MarkdownPipeline))
              .Append("</div>");
        if (severity == "critical" && !string.IsNullOrEmpty(card.Uuid))
        {
            // The `form="order-form"` attribute lets a checkbox outside the
            // <form> still POST under the form's Acknowledged[] array. Pre-
            // checked when the fingerprint is already in the ack set so the
            // user doesn't have to re-tick across resubmits.
            sb.Append("<label class=\"ack\"><input type=\"checkbox\" form=\"order-form\" name=\"Acknowledged\" value=\"")
              .Append(WebEncode(card.Uuid)).Append("\"");
            if (isAcked) sb.Append(" checked");
            sb.Append(" /> Acknowledge this critical alert</label>");
        }
        sb.Append("</article>");
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

    private static void TextAreaField(StringBuilder sb, string id, string name, string label, string? value)
    {
        sb.Append("<label class=\"field\" for=\"").Append(id).Append("\">");
        sb.Append("<span class=\"field-label\">").Append(WebEncode(label)).Append("</span>");
        sb.Append("<textarea id=\"").Append(id).Append("\" name=\"").Append(name).Append("\" rows=\"2\">")
          .Append(WebEncode(value ?? string.Empty)).Append("</textarea>");
        sb.Append("</label>");
    }

    private static void NumberField(
        StringBuilder sb, string id, string name, string label, int value, int min, int max)
    {
        sb.Append("<label class=\"field\" for=\"").Append(id).Append("\">");
        sb.Append("<span class=\"field-label\">").Append(WebEncode(label)).Append("</span>");
        sb.Append("<input type=\"number\" id=\"").Append(id).Append("\" name=\"").Append(name)
          .Append("\" min=\"").Append(min).Append("\" max=\"").Append(max)
          .Append("\" value=\"").Append(value).Append("\" />");
        sb.Append("</label>");
    }

    private static void SelectField(
        StringBuilder sb, string id, string name, string label, string? current, IReadOnlyList<string> options)
    {
        var pairs = options.Select(o => (Id: o, Display: o)).ToArray();
        SelectField(sb, id, name, label, current, pairs, allowEmpty: true, emptyLabel: "(choose one)");
    }

    private static void SelectField(
        StringBuilder sb, string id, string name, string label, string? current,
        IReadOnlyList<(string Id, string Display)> options,
        bool allowEmpty, string emptyLabel)
    {
        sb.Append("<label class=\"field\" for=\"").Append(id).Append("\">");
        sb.Append("<span class=\"field-label\">").Append(WebEncode(label)).Append("</span>");
        sb.Append("<select id=\"").Append(id).Append("\" name=\"").Append(name).Append("\">");
        if (allowEmpty)
        {
            sb.Append("<option value=\"\"")
              .Append(string.IsNullOrEmpty(current) ? " selected" : "")
              .Append(">").Append(WebEncode(emptyLabel)).Append("</option>");
        }
        foreach (var (oid, display) in options)
        {
            sb.Append("<option value=\"").Append(WebEncode(oid)).Append("\"")
              .Append(string.Equals(oid, current, StringComparison.Ordinal) ? " selected" : "")
              .Append(">").Append(WebEncode(display)).Append("</option>");
        }
        sb.Append("</select></label>");
    }

    private static void CheckboxField(StringBuilder sb, string id, string name, string label, bool checkedValue)
    {
        sb.Append("<label class=\"field checkbox\" for=\"").Append(id).Append("\">");
        // Hidden zero ensures unchecked submits as false (HTML forms omit unchecked boxes).
        sb.Append("<input type=\"hidden\" name=\"").Append(name).Append("\" value=\"false\" />");
        sb.Append("<input type=\"checkbox\" id=\"").Append(id).Append("\" name=\"").Append(name).Append("\" value=\"true\"")
          .Append(checkedValue ? " checked" : "").Append(" />");
        sb.Append("<span>").Append(WebEncode(label)).Append("</span>");
        sb.Append("</label>");
    }

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

        .order-main { max-width:1280px; margin:1.5rem auto 3rem; padding:0 1.5rem;
                      display:grid; grid-template-columns: minmax(0, 1fr) 380px; gap:1.5rem; }
        .order-subnav-host { grid-column: 1 / -1; margin-bottom: .25rem; }
        .order-subnav { display:flex; gap:.5rem; }
        .order-subnav a { padding:.35rem .8rem; font-size:.85rem; color:var(--ink-soft); text-decoration:none;
                          border-radius:999px; border:1px solid var(--rule); background:var(--surface); }
        .order-subnav a:hover { color:var(--ink); }
        .order-subnav a.active { color:#fff; background:var(--ink); border-color:var(--ink); font-weight:600; }
        .order-form { display:flex; flex-direction:column; gap:1rem; }
        .form-section { background:var(--surface); border-radius:14px; padding:1rem 1.25rem;
                        box-shadow:0 1px 2px rgba(0,0,0,.04); }
        .form-section h2 { font-size:.78rem; text-transform:uppercase; letter-spacing:.06em;
                           color:var(--ink-muted); font-weight:600; margin:0 0 .65rem; }
        .field { display:flex; flex-direction:column; margin-bottom:.85rem; }
        .field-label { font-size:.85rem; font-weight:500; color:var(--ink); margin-bottom:.25rem; }
        .field input[type=text], .field input[type=number], .field select, .field textarea {
            padding:.55rem .7rem; font-size:.95rem; border:1px solid var(--rule); border-radius:8px;
            background:#fff; color:var(--ink); font-family:inherit; }
        .field input[type=text]:focus, .field input[type=number]:focus,
        .field select:focus, .field textarea:focus { outline:2px solid var(--info); outline-offset:1px; }
        .field.checkbox { flex-direction:row; align-items:center; gap:.5rem; margin-bottom:.6rem; }
        .field.checkbox > input[type=hidden] + input[type=checkbox] { margin:0; }
        .hint { font-size:.78rem; color:var(--ink-muted); margin-top:.2rem; }
        .req { color:var(--crit); }
        .form-actions { display:flex; gap:.75rem; justify-content:flex-end; padding-top:.25rem; }
        .btn { padding:.55rem 1.1rem; font-size:.92rem; font-weight:600; border:0; border-radius:8px;
               cursor:pointer; }
        .btn.primary { background:var(--info); color:#fff; }
        .btn.primary:hover { background:#0c5fb5; }
        .btn.secondary { background:var(--surface); color:var(--ink); border:1px solid var(--rule); }
        .btn.secondary:hover { background:var(--bg); }

        .cards-panel { display:flex; flex-direction:column; gap:.75rem; }
        .cards-panel h2 { font-size:.78rem; text-transform:uppercase; letter-spacing:.06em;
                          color:var(--ink-muted); font-weight:600; margin:0; }
        .alert { background:var(--surface); border-radius:14px; padding:.9rem 1.1rem;
                 box-shadow:0 1px 2px rgba(0,0,0,.04); border-left:4px solid var(--info); }
        .alert.warning { border-left-color:var(--warn); }
        .alert.critical { border-left-color:var(--crit); }
        .alert.acknowledged { background:var(--ok-soft); border-left-color:var(--ok); }
        .alert-head { display:flex; align-items:baseline; gap:.5rem; }
        .alert-head h3 { font-size:.95rem; margin:0; font-weight:600; }
        .badge { font-size:.62rem; font-weight:700; padding:.15rem .5rem; border-radius:6px; letter-spacing:.05em; }
        .badge.info { background:var(--info-soft); color:var(--info); }
        .badge.warning { background:var(--warn-soft); color:var(--warn); }
        .badge.critical { background:var(--crit-soft); color:var(--crit); }
        .alert-detail { margin-top:.5rem; font-size:.88rem; color:var(--ink-soft); }
        .alert-detail p { margin:.25rem 0; }
        .alert-detail ul { padding-left:1.1rem; margin:.25rem 0; }
        .ack { display:flex; gap:.4rem; align-items:center; margin-top:.5rem; font-size:.82rem; color:var(--ink-muted); }

        .banner { padding:.7rem 1rem; border-radius:10px; margin-bottom:1rem; font-size:.9rem;
                  grid-column: 1 / -1; }
        .banner.info { background:var(--info-soft); color:var(--info); }
        .banner.warn { background:var(--warn-soft); color:var(--warn); border:1px solid #f0c46a; }
        .banner.err  { background:var(--crit-soft); color:var(--crit); }
        .banner.ok   { background:var(--ok-soft); color:var(--ok); }
        .banner code { font-family:ui-monospace,'SF Mono',Menlo,monospace; font-size:.82rem;
                       background:rgba(0,0,0,.06); padding:.05rem .35rem; border-radius:4px; }

        .link-back { display:inline-block; margin-top:.5rem; color:var(--info); text-decoration:none; }
        .link-back:hover { text-decoration:underline; }

        .signin-pane { grid-column: 1 / -1; background:var(--surface); border-radius:14px;
                       padding:1.5rem 1.75rem; box-shadow:0 1px 2px rgba(0,0,0,.04); max-width:60ch; }
        .signin-pane h2 { font-size:1.1rem; margin:0 0 .5rem; }
        .signin-pane p { margin:.4rem 0; color:var(--ink-soft); }
        .signin-pane .btn { display:inline-block; text-decoration:none; margin-right:.5rem; }

        @media (max-width: 880px) { .order-main { grid-template-columns: 1fr; } }
    </style>";

    private static string WebEncode(string? s) => WebUtility.HtmlEncode(s ?? string.Empty);
}

/// <summary>View bundle the order controller hands the renderer.</summary>
public sealed record OrderEntryView(
    string PatientId,
    string PatientDisplayName,
    string? PatientSubline,
    OrderDraft Draft,
    IReadOnlyList<CdsCard> Cards,
    IReadOnlyList<PharmacyEntry> Pharmacies,
    IReadOnlySet<string> AcknowledgedFingerprints,
    OrderEntryStatus Status,
    string? Message,
    string? WrittenId);

public enum OrderEntryStatus
{
    Empty,
    Blocked,
    NotAuthorised,
    Failed,
    SignedOk,
}
