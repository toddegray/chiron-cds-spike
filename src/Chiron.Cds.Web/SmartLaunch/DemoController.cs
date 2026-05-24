using System.Net.Mime;
using System.Text.Json;

using Chiron.Cds.Web.CdsHooks;
using Chiron.Cds.Web.CdsHooks.Models;
using Microsoft.AspNetCore.Mvc;

namespace Chiron.Cds.Web.SmartLaunch;

/// <summary>
/// Renders the post-launch alert UI from one of several canonical sample
/// CDS Hooks requests under <c>docs/</c>. Routes through the exact same
/// evaluation path as the live CDS Hooks <c>chiron-patient-view</c>
/// service — no separate code path, no mock — the only thing the demo
/// replaces is the bearer-authenticated FHIR fetch, with the prefetch
/// resources Cerner already returned to a previous open-sandbox query.
/// </summary>
[ApiController]
[Route("app/demo")]
public sealed class DemoController : ControllerBase
{
    private static readonly IReadOnlyDictionary<string, DemoScenario> Scenarios = new Dictionary<string, DemoScenario>(StringComparer.Ordinal)
    {
        ["annie-smith"] = new DemoScenario(
            Id: "annie-smith",
            Title: "Annie Smith (real Cerner sandbox)",
            PatientId: "12674028",
            Description: "35-year-old female with active Type 2 diabetes mellitus and an active metformin order. Fetched live from fhir-open.cerner.com; no augmentation. Engine fires CHA₂DS₂-VASc on female_sex + diabetes.",
            Filename: "sample-patient-view-request.json"),
    };

    private readonly PatientViewService _service;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<DemoController> _log;

    public DemoController(
        PatientViewService service,
        IWebHostEnvironment env,
        ILogger<DemoController> log)
    {
        _service = service;
        _env = env;
        _log = log;
    }

    /// <summary>Index: list available demo scenarios.</summary>
    [HttpGet]
    public IActionResult Index()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>Chiron CDS — Demos</title>");
        sb.Append(@"<style>
            body{font-family:-apple-system,BlinkMacSystemFont,system-ui,sans-serif;margin:0;background:#fafafa;color:#1d1d1f;line-height:1.5;}
            .navbar{background:#1d1d1f;color:#fff;padding:.75rem 1.5rem;display:flex;gap:1.25rem;align-items:center;font-size:.95rem;}
            .navbar a{color:#fff;text-decoration:none;opacity:.8;}.navbar a:hover{opacity:1;}.navbar .brand{font-weight:600;opacity:1;}
            main{max-width:920px;margin:2rem auto;padding:0 1.5rem;}
            h1{font-size:1.6rem;margin:0 0 .5rem;} h2{font-size:1.15rem;margin:.5rem 0;}
            .lede{color:#6b7280;margin:0 0 2rem;}
            .scenario{background:#fff;border-radius:10px;padding:1.25rem 1.5rem;margin:1rem 0;box-shadow:0 1px 3px rgba(0,0,0,.05);border-left:6px solid #27ae60;}
            .scenario a{color:#2778c4;text-decoration:none;font-weight:600;}.scenario a:hover{text-decoration:underline;}
            .scenario .meta{color:#6b7280;font-size:.85rem;margin:.25rem 0 .5rem;}
            .endpoint{background:#fff;border-radius:10px;padding:1.25rem 1.5rem;margin:1.5rem 0;box-shadow:0 1px 3px rgba(0,0,0,.05);border-left:6px solid #2778c4;}
            .endpoint code{background:#f3f4f6;padding:.1rem .4rem;border-radius:4px;font-family:ui-monospace,SF Mono,Menlo,monospace;}
            .endpoint pre{background:#f3f4f6;padding:.75rem;border-radius:6px;overflow:auto;font-size:.85rem;}
        </style>");
        sb.Append("</head><body>");
        sb.Append(NavBar());
        sb.Append("<main>");
        sb.Append("<h1>Chiron CDS — Demo scenarios</h1>");
        sb.Append("<p class=\"lede\">Each scenario routes through the live engine and CDS Hooks evaluation path. The chart data comes from a prefetched real Cerner sandbox patient — no SMART session, no synthetic clinical data.</p>");

        foreach (var s in Scenarios.Values)
        {
            sb.Append("<div class=\"scenario\">");
            sb.Append("<h2><a href=\"/app/demo/").Append(s.Id).Append("\">").Append(System.Net.WebUtility.HtmlEncode(s.Title)).Append("</a></h2>");
            sb.Append("<div class=\"meta\">Patient ").Append(System.Net.WebUtility.HtmlEncode(s.PatientId))
              .Append(" — prefetch from <code>docs/").Append(System.Net.WebUtility.HtmlEncode(s.Filename)).Append("</code></div>");
            sb.Append("<p>").Append(System.Net.WebUtility.HtmlEncode(s.Description)).Append("</p>");
            sb.Append("</div>");
        }

        sb.Append("<div class=\"endpoint\">");
        sb.Append("<h2>Live CDS Hooks endpoints</h2>");
        sb.Append("<p>The same engine output is reachable server-to-server. No login needed; the EHR posts a request, we return cards.</p>");
        sb.Append("<p><strong>Discovery:</strong> <a href=\"/cds-services\">GET /cds-services</a> — returns the patient-view service descriptor.</p>");
        sb.Append("<p><strong>Patient-view:</strong> <code>POST /cds-services/chiron-patient-view</code> — accepts a CDS Hooks request, returns CDS cards. Try it:</p>");
        sb.Append("<pre>curl -s -X POST http://localhost:5099/cds-services/chiron-patient-view \\\n  -H 'Content-Type: application/json' \\\n  -d @docs/sample-patient-view-request.json | jq</pre>");
        sb.Append("</div>");

        sb.Append("<div class=\"endpoint\">");
        sb.Append("<h2>Live SMART launch</h2>");
        sb.Append("<p>End-to-end OAuth handshake with the Cerner Code sandbox.</p>");
        sb.Append("<p><a href=\"/smart/launch?iss=https://fhir-ehr-code.cerner.com/r4/ec2458f2-1e24-41c8-b71b-0e701af7583d\">Begin SMART launch</a> — discovers Cerner's auth endpoint, redirects to CernerCare login, exchanges code for tokens, fetches the authenticated chart.</p>");
        sb.Append("</div>");

        sb.Append("</main></body></html>");
        return Content(sb.ToString(), MediaTypeNames.Text.Html);
    }

    /// <summary>Render a specific demo scenario.</summary>
    [HttpGet("{id}")]
    public async Task<IActionResult> Render(string id, CancellationToken ct)
    {
        if (!Scenarios.TryGetValue(id, out var scenario))
            return NotFound($"Unknown demo scenario '{id}'.");

        var samplePath = ResolveSamplePath(scenario.Filename);
        if (samplePath is null)
            return Content(
                $"<!doctype html><html><body><h1>Chiron CDS demo</h1><p>Sample file not found. Expected at docs/{scenario.Filename}.</p></body></html>",
                MediaTypeNames.Text.Html);

        var json = await System.IO.File.ReadAllTextAsync(samplePath, ct).ConfigureAwait(false);
        var request = JsonSerializer.Deserialize<CdsHookRequest>(json)
            ?? throw new InvalidOperationException("Sample request did not deserialize.");

        _log.LogInformation("Demo rendering scenario {Id} (patient {Patient}).", id, scenario.PatientId);

        var bundled = await _service.EvaluateBundledAsync(request, ct).ConfigureAwait(false);
        var patientHeader = bundled.Inputs is null
            ? null
            : PatientHeader.From(bundled.Inputs, scenario.Title);

        var html = AlertHtmlRenderer.Render(
            heading: scenario.Title,
            subline: $"Patient {scenario.PatientId} — {scenario.Description}",
            cards: bundled.Cards,
            banner: "Demo mode — running the live engine over a prefetched real Cerner chart. No SMART session, no live FHIR fetch.",
            navBar: NavBar(),
            patient: patientHeader);

        return Content(html, MediaTypeNames.Text.Html);
    }


    private static string NavBar() =>
        "<span class=\"brand\">Chiron CDS</span>"
        + "<a href=\"/app/demo\">Demo</a>"
        + "<a href=\"/cds-services\">CDS Hooks discovery</a>"
        + "<a href=\"/smart/launch?iss=https://fhir-ehr-code.cerner.com/r4/ec2458f2-1e24-41c8-b71b-0e701af7583d\">SMART launch</a>"
        + "<a href=\"/health\">Health</a>";

    private string? ResolveSamplePath(string filename)
    {
        var candidates = new[]
        {
            Path.Combine(_env.ContentRootPath, "..", "..", "docs", filename),
            Path.Combine(_env.ContentRootPath, "..", "docs", filename),
            Path.Combine(_env.ContentRootPath, "docs", filename),
        };
        foreach (var c in candidates)
        {
            var full = Path.GetFullPath(c);
            if (System.IO.File.Exists(full)) return full;
        }
        return null;
    }

    private sealed record DemoScenario(
        string Id,
        string Title,
        string PatientId,
        string Description,
        string Filename);
}
