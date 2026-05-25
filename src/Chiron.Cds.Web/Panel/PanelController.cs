using System.Net.Mime;

using Chiron.Cds.Web.SmartLaunch;
using Microsoft.AspNetCore.Mvc;

namespace Chiron.Cds.Web.Panel;

/// <summary>
/// Replacement-mode entry surface. Three routes:
/// <list type="bullet">
///   <item><c>GET /app/panel</c> — multi-patient worklist (today's schedule).</item>
///   <item><c>GET /app/search?q=…</c> — search the FHIR endpoint for any patient by name.</item>
///   <item><c>GET /app/patient/{id}</c> — the Visit Brief for a specific patient.</item>
/// </list>
/// These run against the open FHIR sandbox today — when the authenticated
/// endpoint is wired up the controller stays the same; only the underlying
/// tenant URL + bearer token change.
/// </summary>
[ApiController]
[Route("app")]
public sealed class PanelController : ControllerBase
{
    private readonly PanelService _panel;
    private readonly PatientSearchService _search;

    public PanelController(PanelService panel, PatientSearchService search)
    {
        _panel = panel;
        _search = search;
    }

    [HttpGet("panel")]
    public async Task<IActionResult> Panel(CancellationToken ct)
    {
        var entries = await _panel.GetPanelAsync(ct).ConfigureAwait(false);
        var rows = entries.Select(ToWorklistRow).ToArray();
        var html = WorklistRenderer.Render(
            heading: "Your panel",
            subline: string.Empty,
            rows: rows,
            navBar: NavBar(),
            drillBaseUrl: "/app/patient");
        return Content(html, MediaTypeNames.Text.Html);
    }

    [HttpGet("search")]
    public async Task<IActionResult> Search([FromQuery] string? q, CancellationToken ct)
    {
        var result = string.IsNullOrWhiteSpace(q)
            ? PatientSearchResult.Empty
            : await _search.SearchAsync(q, ct).ConfigureAwait(false);
        var html = PatientSearchRenderer.Render(
            query: q ?? string.Empty,
            hits: result.Hits,
            warning: result.Warning,
            navBar: NavBar(),
            drillBaseUrl: "/app/patient");
        return Content(html, MediaTypeNames.Text.Html);
    }

    [HttpGet("patient/{id}")]
    public async Task<IActionResult> Patient(string id, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var entry = await _panel.GetPatientAsync(id, ct).ConfigureAwait(false);
        if (entry is null) return NotFound();

        if (entry.Error is not null)
        {
            return Content(
                AlertHtmlRenderer.Render(
                    heading: entry.DisplayName,
                    subline: string.Empty,
                    cards: Array.Empty<CdsHooks.Models.CdsCard>(),
                    banner: $"Chart could not be loaded: {entry.Error}",
                    navBar: NavBar(),
                    patient: null),
                MediaTypeNames.Text.Html);
        }

        var header = entry.Inputs is null
            ? null
            : PatientHeader.From(
                entry.Inputs,
                entry.DisplayName,
                dateOfBirth: entry.DateOfBirth,
                mrn: entry.Mrn);
        var html = AlertHtmlRenderer.Render(
            heading: entry.DisplayName,
            subline: string.Empty,
            cards: entry.Cards,
            navBar: NavBar(),
            patient: header);
        return Content(html, MediaTypeNames.Text.Html);
    }

    private static WorklistRow ToWorklistRow(PanelEntry e)
    {
        var headline = e.Cards.FirstOrDefault();
        return new WorklistRow(
            PatientId: e.PatientId,
            DisplayName: e.DisplayName,
            AgeSex: e.AgeSex,
            HeadlineFlag: e.Error is not null ? $"Could not load chart — {e.Error}" : headline?.Summary,
            HeadlineSeverity: e.Error is not null ? "warning" : headline?.Indicator,
            AlertCount: e.Cards.Count);
    }

    private static string NavBar() =>
        "<span class=\"brand\">Chiron</span>"
        + "<a href=\"/app/panel\">Panel</a>"
        + "<a href=\"/app/search\">Search</a>"
        + "<a href=\"/cds-services\">CDS Hooks</a>"
        + "<a href=\"/smart/launch?iss=https://fhir-ehr-code.cerner.com/r4/ec2458f2-1e24-41c8-b71b-0e701af7583d\">SMART launch</a>";
}
