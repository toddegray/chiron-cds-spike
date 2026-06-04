using System.Net.Mime;
using System.Text.Json;

using Chiron.Cds.Engine.Primitives;
using Chiron.Cds.Web.CdsHooks;
using Chiron.Cds.Web.CdsHooks.Models;
using ReasoningEngine = Chiron.Cds.Engine.Engine;
using Chiron.Cds.Engine;
using Chiron.Cds.Web.FhirClient;
using Chiron.Cds.Web.Mappers;
using Chiron.Cds.Web.Panel;
using Chiron.Cds.Web.Tenancy;
using Microsoft.AspNetCore.Mvc;

namespace Chiron.Cds.Web.SmartLaunch;

/// <summary>
/// Post-launch landing for the SMART session. <c>GET /app</c> renders the
/// patient chart inside the shared <see cref="ChartShell"/> (top bar, icon
/// rail, tab strip) so the launch hands off into the same UI the rest of the
/// app uses. <c>GET /app/alerts</c> is the CDS Hooks JSON wire format for
/// integration tests and curl.
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
    private readonly PatientViewService _patientViewHook;
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
        PatientViewService patientViewHook,
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
        _patientViewHook = patientViewHook;
        _log = log;
    }

    [HttpGet]
    public async Task<IActionResult> Index(
        [FromQuery] string session,
        [FromQuery] string? patient,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(session))
            return Content(RenderLandingHtml("No session — start at /smart/launch."), MediaTypeNames.Text.Html);

        var sess = _store.GetSession(session);
        if (sess is null)
            return Content(RenderLandingHtml("Session not found or expired."), MediaTypeNames.Text.Html);

        // A provider / user-scoped launch returns a token with no patient
        // context, so the clinician selects one via ?patient=<id>. A patient
        // bound to the launch token always wins over the query parameter.
        var patientId = !string.IsNullOrEmpty(sess.PatientId) ? sess.PatientId : patient;
        if (string.IsNullOrEmpty(patientId))
            return Content(RenderLandingHtml(
                "SMART session has no patient context — append ?patient=<id> to choose one. " +
                "(A user-scoped provider launch returns no patient.) " +
                "Granted scopes: " + string.Join(", ", sess.GrantedScopes)), MediaTypeNames.Text.Html);

        var resolved = sess.PatientId == patientId ? sess : sess with { PatientId = patientId };

        try
        {
            var (inputs, cards, _, header) = await EvaluateForSessionAsync(resolved, ct).ConfigureAwait(false);
            var html = EhrChartRenderer.Render(
                patientId: resolved.PatientId,
                displayName: header.DisplayName,
                ageSex: header.AgeSex,
                dateOfBirth: header.DateOfBirth,
                mrn: header.Mrn,
                inputs: inputs,
                cards: cards);
            return Content(html, MediaTypeNames.Text.Html);
        }
        catch (Hl7.Fhir.Rest.FhirOperationException ex)
        {
            var diag = $"FHIR call failed with HTTP {(int)ex.Status} ({ex.Status}). "
                + $"Patient: {resolved.PatientId}. Tenant: {resolved.TenantId}. "
                + $"Granted scopes from token response: [{string.Join(", ", resolved.GrantedScopes.OrderBy(s => s, StringComparer.Ordinal))}]. "
                + $"Body: {ex.Message}";
            _log.LogWarning(ex, "FHIR fetch failed for session {Session}.", resolved.SessionId);
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

        var tenant = _tenants.GetById(sess.TenantId);
        var request = BuildPatientViewHookRequest(sess, tenant);
        var evaluation = await _patientViewHook.EvaluateBundledAsync(request, ct).ConfigureAwait(false);
        return Ok(new CdsHookResponse(evaluation.Cards));
    }

    private static CdsHookRequest BuildPatientViewHookRequest(SmartSession sess, TenantConfig tenant)
    {
        var expiresIn = Math.Max((int)(sess.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds, 0);
        var context = JsonSerializer.SerializeToElement(new
        {
            patientId = sess.PatientId,
            encounterId = sess.EncounterId,
        });
        return new CdsHookRequest(
            Hook: "patient-view",
            HookInstance: "smart-session-" + sess.SessionId,
            FhirServer: tenant.FhirBaseUrl.AbsoluteUri.TrimEnd('/'),
            FhirAuthorization: new CdsFhirAuthorization(
                AccessToken: sess.AccessToken,
                TokenType: "Bearer",
                ExpiresIn: expiresIn,
                Scope: string.Join(' ', sess.GrantedScopes),
                Subject: null),
            Context: context,
            Prefetch: null);
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
        var (_, _, alerts, _) = await EvaluateForSessionAsync(sess, ct).ConfigureAwait(false);
        var alert = alerts.FirstOrDefault(a => a.Fingerprint == fingerprint);
        if (alert is null) return NotFound("Alert with that fingerprint not found in current evaluation.");

        var reportId = await _reportWriter.WriteAsync(
            tenant, sess.AccessToken, sess.PatientId, alert, ct).ConfigureAwait(false);
        return Ok(new { reportId, fingerprint = alert.Fingerprint });
    }

    private async Task<(EngineInputs Inputs, IReadOnlyList<CdsCard> Cards, IReadOnlyList<Alert> Alerts, PatientHeader Header)> EvaluateForSessionAsync(
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

        var header = PatientHeader.From(
            inputs,
            displayName: PanelService.ChartName(chart.Patient, sess.PatientId),
            dateOfBirth: chart.Patient.BirthDate,
            mrn: PatientMrn.Extract(chart.Patient, tenant.MrnSystem));
        _log.LogInformation("Evaluated session {Session}: {Count} alerts.", sess.SessionId, result.Alerts.Count);
        return (inputs, cards, result.Alerts, header);
    }

    private static string RenderLandingHtml(string message) =>
        $"<!doctype html><html><body><h1>CDS</h1><p>{WebEncode(message)}</p></body></html>";

    private static string WebEncode(string s) => System.Net.WebUtility.HtmlEncode(s);
}
