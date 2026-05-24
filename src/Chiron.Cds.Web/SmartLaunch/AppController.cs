using System.Net.Mime;

using Chiron.Cds.Engine.Primitives;
using Chiron.Cds.Web.CdsHooks.Models;
using ReasoningEngine = Chiron.Cds.Engine.Engine;
using Chiron.Cds.Engine;
using Chiron.Cds.Web.FhirClient;
using Chiron.Cds.Web.Mappers;
using Chiron.Cds.Web.Tenancy;
using Microsoft.AspNetCore.Mvc;

namespace Chiron.Cds.Web.SmartLaunch;

/// <summary>
/// Post-launch landing page. Renders alerts as inline HTML so the demo
/// works without a separate SPA. The wire format under <c>/app/alerts</c>
/// is JSON for the integration tests and curl.
/// </summary>
[ApiController]
[Route("app")]
public sealed class AppController : ControllerBase
{
    private readonly ITokenStore _store;
    private readonly TenantRegistry _tenants;
    private readonly ReasoningEngine _engine;
    private readonly PatientChartFetcher _fetcher;
    private readonly FhirToFactMapper _factMapper;
    private readonly AlertToCdsCardMapper _cardMapper;
    private readonly DiagnosticReportWriter _reportWriter;
    private readonly IOverrideLog _overrideLog;
    private readonly ILogger<AppController> _log;

    public AppController(
        ITokenStore store,
        TenantRegistry tenants,
        ReasoningEngine engine,
        PatientChartFetcher fetcher,
        FhirToFactMapper factMapper,
        AlertToCdsCardMapper cardMapper,
        DiagnosticReportWriter reportWriter,
        IOverrideLog overrideLog,
        ILogger<AppController> log)
    {
        _store = store;
        _tenants = tenants;
        _engine = engine;
        _fetcher = fetcher;
        _factMapper = factMapper;
        _cardMapper = cardMapper;
        _reportWriter = reportWriter;
        _overrideLog = overrideLog;
        _log = log;
    }

    [HttpGet]
    public async Task<IActionResult> Index(
        [FromQuery] string session,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(session))
            return Content(RenderLandingHtml("No session — start at /smart/launch."), MediaTypeNames.Text.Html);

        var sess = _store.GetSession(session);
        if (sess is null)
            return Content(RenderLandingHtml("Session not found or expired."), MediaTypeNames.Text.Html);

        if (string.IsNullOrEmpty(sess.PatientId))
            return Content(RenderLandingHtml(
                "SMART session has no patient context. Redo the launch and answer 'Yes' " +
                "to 'Does your application require a patient?' so Cerner binds a patient to the launch token. " +
                "Granted scopes: " + string.Join(", ", sess.GrantedScopes)), MediaTypeNames.Text.Html);

        try
        {
            var (cards, _, header) = await EvaluateAsync(sess, ct).ConfigureAwait(false);
            return Content(RenderAlertsHtml(sess, cards, header), MediaTypeNames.Text.Html);
        }
        catch (Hl7.Fhir.Rest.FhirOperationException ex)
        {
            var diag = $"FHIR call failed with HTTP {(int)ex.Status} ({ex.Status}). "
                + $"Patient: {sess.PatientId}. Tenant: {sess.TenantId}. "
                + $"Granted scopes from token response: [{string.Join(", ", sess.GrantedScopes.OrderBy(s => s, StringComparer.Ordinal))}]. "
                + $"Body: {ex.Message}";
            _log.LogWarning(ex, "FHIR fetch failed for session {Session}.", sess.SessionId);
            return Content(RenderLandingHtml(diag), MediaTypeNames.Text.Html);
        }
    }

    [HttpGet("alerts")]
    public async Task<ActionResult<CdsHookResponse>> Alerts(
        [FromQuery] string session,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(session)) return BadRequest("Missing session parameter.");
        var sess = _store.GetSession(session);
        if (sess is null) return NotFound("Session not found or expired.");

        var (cards, _, _) = await EvaluateAsync(sess, ct).ConfigureAwait(false);
        return Ok(new CdsHookResponse(cards));
    }

    [HttpPost("accept-alert")]
    public async Task<IActionResult> AcceptAlert(
        [FromQuery] string session,
        [FromQuery] string fingerprint,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(session) || string.IsNullOrEmpty(fingerprint))
            return BadRequest("Missing session or fingerprint.");

        var sess = _store.GetSession(session);
        if (sess is null) return NotFound("Session not found or expired.");

        var tenant = _tenants.GetById(sess.TenantId);
        var (_, alerts, _) = await EvaluateForSessionAsync(sess, ct).ConfigureAwait(false);
        var alert = alerts.FirstOrDefault(a => a.Fingerprint == fingerprint);
        if (alert is null) return NotFound("Alert with that fingerprint not found in current evaluation.");

        var reportId = await _reportWriter.WriteAsync(
            tenant, sess.AccessToken, sess.PatientId, alert, ct).ConfigureAwait(false);
        return Ok(new { reportId, fingerprint = alert.Fingerprint });
    }

    private async Task<(IReadOnlyList<CdsCard> Cards, int AlertCount, PatientHeader? Header)> EvaluateAsync(
        SmartSession sess, CancellationToken ct)
    {
        var (cards, alerts, header) = await EvaluateForSessionAsync(sess, ct).ConfigureAwait(false);
        return (cards, alerts.Count, header);
    }

    private async Task<(IReadOnlyList<CdsCard> Cards, IReadOnlyList<Alert> Alerts, PatientHeader Header)> EvaluateForSessionAsync(
        SmartSession sess, CancellationToken ct)
    {
        var tenant = _tenants.GetById(sess.TenantId);
        var chart = await _fetcher.FetchAsync(tenant, sess.AccessToken, sess.PatientId, sess.EncounterId, ct).ConfigureAwait(false);
        var inputs = _factMapper.Project(chart);
        var result = _engine.Evaluate(inputs.Patient, inputs.Medications, inputs.Labs, inputs.Conditions, inputs.Allergies, inputs.Immunizations, inputs.Procedures);

        var cards = new List<CdsCard>(result.Alerts.Count);
        foreach (var alert in result.Alerts)
        {
            _overrideLog.RecordFire(alert);
            cards.Add(_cardMapper.Map(alert));
        }

        var header = PatientHeader.From(inputs, displayName: $"Patient {sess.PatientId}");
        _log.LogInformation("Evaluated session {Session}: {Count} alerts.", sess.SessionId, result.Alerts.Count);
        return (cards, result.Alerts, header);
    }

    private static string RenderAlertsHtml(SmartSession sess, IReadOnlyList<CdsCard> cards, PatientHeader? header) =>
        AlertHtmlRenderer.Render(
            heading: "Chiron CDS",
            subline: $"Session for patient {sess.PatientId} on tenant {sess.TenantId}.",
            cards: cards,
            patient: header);

    private static string RenderLandingHtml(string message) =>
        $"<!doctype html><html><body><h1>Chiron CDS</h1><p>{WebEncode(message)}</p></body></html>";

    private static string WebEncode(string s) => System.Net.WebUtility.HtmlEncode(s);
}
