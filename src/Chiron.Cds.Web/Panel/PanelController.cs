using System.Net.Mime;

using Chiron.Cds.Web.Configuration;
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
    private readonly ResultReviewService _results;
    private readonly OrderEntryService _orders;
    private readonly NoteEntryService _notes;
    private readonly EncounterCloseService _signoff;
    private readonly ServiceRequestService _serviceRequests;
    private readonly ITokenStore _tokens;

    public PanelController(
        PanelService panel,
        PatientSearchService search,
        ResultReviewService results,
        OrderEntryService orders,
        NoteEntryService notes,
        EncounterCloseService signoff,
        ServiceRequestService serviceRequests,
        ITokenStore tokens)
    {
        _panel = panel;
        _search = search;
        _results = results;
        _orders = orders;
        _notes = notes;
        _signoff = signoff;
        _serviceRequests = serviceRequests;
        _tokens = tokens;
    }

    [HttpGet("patient/{id}/orders/labs")]
    public Task<IActionResult> LabOrdersForm(string id, CancellationToken ct) =>
        RenderServiceRequestForm(id, ServiceRequestCategory.Laboratory, ServiceRequestDraft.Empty,
            ServiceRequestStatus.Empty, message: null, writtenId: null, ct);

    [HttpPost("patient/{id}/orders/labs")]
    public Task<IActionResult> LabOrdersSubmit(
        string id, [FromForm] ServiceRequestForm form, CancellationToken ct) =>
        SubmitServiceRequest(id, ServiceRequestCategory.Laboratory, form, ct);

    [HttpGet("patient/{id}/orders/imaging")]
    public Task<IActionResult> ImagingOrdersForm(string id, CancellationToken ct) =>
        RenderServiceRequestForm(id, ServiceRequestCategory.Imaging, ServiceRequestDraft.Empty,
            ServiceRequestStatus.Empty, message: null, writtenId: null, ct);

    [HttpPost("patient/{id}/orders/imaging")]
    public Task<IActionResult> ImagingOrdersSubmit(
        string id, [FromForm] ServiceRequestForm form, CancellationToken ct) =>
        SubmitServiceRequest(id, ServiceRequestCategory.Imaging, form, ct);

    private async Task<IActionResult> RenderServiceRequestForm(
        string id, ServiceRequestCategory category, ServiceRequestDraft draft,
        ServiceRequestStatus status, string? message, string? writtenId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var entry = await _panel.GetPatientAsync(id, ct).ConfigureAwait(false);
        var page = await _serviceRequests.GetForPatientAsync(id, category, ct).ConfigureAwait(false);
        var view = new ServiceRequestView(
            PatientId: id,
            PatientDisplayName: entry?.DisplayName ?? $"Patient {id}",
            PatientSubline: BuildPatientSubline(entry),
            Category: category,
            Draft: draft,
            History: page.History,
            Status: status,
            Message: message,
            PageError: page.Error,
            WrittenId: writtenId);
        return Content(
            ServiceRequestRenderer.Render(view, NavBar(), ChartTabs(id, activeTab: "orders")),
            MediaTypeNames.Text.Html);
    }

    private async Task<IActionResult> SubmitServiceRequest(
        string id, ServiceRequestCategory category, ServiceRequestForm form, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(form);
        var draft = form.ToDraft();
        var accessToken = ReadSessionToken();
        var write = await _serviceRequests.SignAsync(id, draft, category, accessToken, ct).ConfigureAwait(false);
        var (status, message, writtenId) = write.Status switch
        {
            ServiceRequestWriteStatus.Ok => (ServiceRequestStatus.SignedOk, (string?)null, write.WrittenId),
            ServiceRequestWriteStatus.NotAuthorised => (ServiceRequestStatus.NotAuthorised, (string?)null, (string?)null),
            _ => (ServiceRequestStatus.Failed, write.Message, (string?)null),
        };
        return await RenderServiceRequestForm(id, category, draft, status, message, writtenId, ct).ConfigureAwait(false);
    }

    [HttpGet("patient/{id}/signoff")]
    public async Task<IActionResult> SignOff(string id, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var entry = await _panel.GetPatientAsync(id, ct).ConfigureAwait(false);
        var page = await _signoff.GetForPatientAsync(id, ct).ConfigureAwait(false);
        var view = BuildSignOffView(id, entry, page, SignOffStatus.Empty, message: null, writtenId: null);
        return Content(
            EncounterCloseRenderer.Render(view, NavBar(), ChartTabs(id, activeTab: "signoff")),
            MediaTypeNames.Text.Html);
    }

    [HttpPost("patient/{id}/signoff")]
    public async Task<IActionResult> SignOffSubmit(
        string id,
        [FromForm] SignOffForm form,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(form);
        if (string.IsNullOrWhiteSpace(form.EncounterId))
            return BadRequest("Missing encounter id.");

        var accessToken = ReadSessionToken();
        var write = await _signoff.CloseAsync(id, form.EncounterId, accessToken, ct).ConfigureAwait(false);

        var entry = await _panel.GetPatientAsync(id, ct).ConfigureAwait(false);
        var page = await _signoff.GetForPatientAsync(id, ct).ConfigureAwait(false);
        var (status, message, writtenId) = write.Status switch
        {
            EncounterCloseStatus.Ok => (SignOffStatus.ClosedOk, (string?)null, write.UpdatedId),
            EncounterCloseStatus.NotAuthorised => (SignOffStatus.NotAuthorised, (string?)null, (string?)null),
            EncounterCloseStatus.AlreadyClosed => (SignOffStatus.AlreadyClosed,
                "That encounter is already marked finished — nothing to update.", (string?)null),
            _ => (SignOffStatus.Failed, write.Message, (string?)null),
        };
        var view = BuildSignOffView(id, entry, page, status, message, writtenId);
        return Content(
            EncounterCloseRenderer.Render(view, NavBar(), ChartTabs(id, activeTab: "signoff")),
            MediaTypeNames.Text.Html);
    }

    private SignOffView BuildSignOffView(
        string id, PanelEntry? entry, SignOffPageData page,
        SignOffStatus status, string? message, string? writtenId) => new(
            PatientId: id,
            PatientDisplayName: entry?.DisplayName ?? $"Patient {id}",
            PatientSubline: BuildPatientSubline(entry),
            Encounters: page.Encounters,
            Status: status,
            Message: message,
            PageError: page.Error,
            WrittenId: writtenId);

    [HttpGet("patient/{id}/notes")]
    public async Task<IActionResult> NotesForm(string id, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var (entry, page) = await LoadNotesPageAsync(id, ct).ConfigureAwait(false);
        var view = BuildNotesView(id, entry, page.Draft, page.History, NoteEntryStatus.Empty,
            message: null, chartError: page.Error, writtenId: null);
        return Content(
            NoteEntryRenderer.Render(view, NavBar(), ChartTabs(id, activeTab: "notes")),
            MediaTypeNames.Text.Html);
    }

    [HttpPost("patient/{id}/notes")]
    public async Task<IActionResult> NotesSubmit(
        string id,
        [FromForm] NoteForm form,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(form);

        var draft = form.ToDraft();
        var accessToken = ReadSessionToken();
        var write = await _notes.SignAsync(id, draft, accessToken, ct).ConfigureAwait(false);

        var (entry, page) = await LoadNotesPageAsync(id, ct).ConfigureAwait(false);
        var (status, message, writtenId) = write.Status switch
        {
            NoteWriteStatus.Ok => (NoteEntryStatus.SignedOk, (string?)null, write.WrittenId),
            NoteWriteStatus.NotAuthorised => (NoteEntryStatus.NotAuthorised, (string?)null, (string?)null),
            _ => (NoteEntryStatus.Failed, write.Message, (string?)null),
        };
        var view = BuildNotesView(id, entry, draft, page.History, status, message, page.Error, writtenId);
        return Content(
            NoteEntryRenderer.Render(view, NavBar(), ChartTabs(id, activeTab: "notes")),
            MediaTypeNames.Text.Html);
    }

    private async Task<(PanelEntry? Entry, NotesPageData Page)> LoadNotesPageAsync(string id, CancellationToken ct)
    {
        var entry = await _panel.GetPatientAsync(id, ct).ConfigureAwait(false);
        var page = await _notes.GetForPatientAsync(id, ct).ConfigureAwait(false);
        return (entry, page);
    }

    private NoteEntryView BuildNotesView(
        string id, PanelEntry? entry, NoteDraft draft, IReadOnlyList<NoteSummary> history,
        NoteEntryStatus status, string? message, string? chartError, string? writtenId) => new(
            PatientId: id,
            PatientDisplayName: entry?.DisplayName ?? $"Patient {id}",
            PatientSubline: BuildPatientSubline(entry),
            Draft: draft,
            History: history,
            Status: status,
            Message: message,
            ChartError: chartError,
            WrittenId: writtenId);

    [HttpGet("patient/{id}/orders")]
    public async Task<IActionResult> OrdersForm(string id, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var entry = await _panel.GetPatientAsync(id, ct).ConfigureAwait(false);
        var view = BuildOrderView(id, entry, OrderDraft.Empty,
            Array.Empty<CdsHooks.Models.CdsCard>(),
            OrderEntryStatus.Empty, message: null, writtenId: null,
            acknowledged: new HashSet<string>(StringComparer.Ordinal));
        return Content(
            OrderEntryRenderer.Render(view, NavBar(), ChartTabs(id, activeTab: "orders")),
            MediaTypeNames.Text.Html);
    }

    [HttpPost("patient/{id}/orders")]
    public async Task<IActionResult> OrdersSubmit(
        string id,
        [FromForm] OrderForm form,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(form);

        var draft = form.ToDraft(_orders.Pharmacies);
        var ack = new HashSet<string>(form.Acknowledged ?? Array.Empty<string>(), StringComparer.Ordinal);
        var entry = await _panel.GetPatientAsync(id, ct).ConfigureAwait(false);

        OrderEntryStatus status;
        string? message = null;
        string? writtenId = null;
        IReadOnlyList<CdsHooks.Models.CdsCard> cards;

        // Single-button flow: every submit always runs CDS first, then
        // attempts to write. The result type tells the renderer whether
        // to render the success page, the acknowledge checkboxes, the
        // sign-in prompt (no SMART session), or the error banner.
        var accessToken = ReadSessionToken();
        var write = await _orders.SignAsync(id, draft, accessToken, ack, ct).ConfigureAwait(false);
        switch (write.Status)
        {
            case OrderWriteStatus.Ok:
                status = OrderEntryStatus.SignedOk;
                writtenId = write.WrittenId;
                cards = Array.Empty<CdsHooks.Models.CdsCard>();
                break;
            case OrderWriteStatus.Blocked:
                status = OrderEntryStatus.Blocked;
                message = write.Message;
                cards = write.Cards;
                break;
            case OrderWriteStatus.NotAuthorised:
                status = OrderEntryStatus.NotAuthorised;
                cards = Array.Empty<CdsHooks.Models.CdsCard>();
                break;
            default:
                status = OrderEntryStatus.Failed;
                message = write.Message;
                cards = Array.Empty<CdsHooks.Models.CdsCard>();
                break;
        }

        var view = BuildOrderView(id, entry, draft, cards, status, message, writtenId, ack);
        return Content(
            OrderEntryRenderer.Render(view, NavBar(), ChartTabs(id, activeTab: "orders")),
            MediaTypeNames.Text.Html);
    }

    private string? ReadSessionToken()
    {
        var sessionId = Request.Query["session"].ToString();
        if (string.IsNullOrEmpty(sessionId)) return null;
        return _tokens.GetSession(sessionId)?.AccessToken;
    }

    private OrderEntryView BuildOrderView(
        string id,
        PanelEntry? entry,
        OrderDraft draft,
        IReadOnlyList<CdsHooks.Models.CdsCard> cards,
        OrderEntryStatus status,
        string? message,
        string? writtenId,
        IReadOnlySet<string> acknowledged) => new(
            PatientId: id,
            PatientDisplayName: entry?.DisplayName ?? $"Patient {id}",
            PatientSubline: BuildPatientSubline(entry),
            Draft: draft,
            Cards: cards,
            Pharmacies: _orders.Pharmacies,
            AcknowledgedFingerprints: acknowledged,
            Status: status,
            Message: message,
            WrittenId: writtenId);

    private static string? BuildPatientSubline(PanelEntry? entry)
    {
        if (entry is null) return null;
        var parts = new List<string>(3);
        if (!string.IsNullOrEmpty(entry.AgeSex)) parts.Add(entry.AgeSex);
        if (!string.IsNullOrEmpty(entry.DateOfBirth)) parts.Add("Born " + entry.DateOfBirth);
        if (!string.IsNullOrEmpty(entry.Mrn)) parts.Add("MRN " + entry.Mrn);
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    [HttpGet("patient/{id}/results")]
    public async Task<IActionResult> Results(string id, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var data = await _results.GetForPatientAsync(id, ct).ConfigureAwait(false);
        return Content(
            ResultReviewRenderer.Render(data, NavBar(), ChartTabs(id, activeTab: "results")),
            MediaTypeNames.Text.Html);
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
            patient: header,
            chartTabs: ChartTabs(id, activeTab: "brief"));
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

    /// <summary>Per-patient tabs strip: Visit brief / Results / Orders / Notes / Sign off.</summary>
    private static IReadOnlyList<ChartTab> ChartTabs(string patientId, string activeTab)
    {
        var escaped = Uri.EscapeDataString(patientId);
        return new[]
        {
            new ChartTab("Visit brief", $"/app/patient/{escaped}", activeTab == "brief"),
            new ChartTab("Results", $"/app/patient/{escaped}/results", activeTab == "results"),
            new ChartTab("Orders", $"/app/patient/{escaped}/orders", activeTab == "orders"),
            new ChartTab("Notes", $"/app/patient/{escaped}/notes", activeTab == "notes"),
            new ChartTab("Sign off", $"/app/patient/{escaped}/signoff", activeTab == "signoff"),
        };
    }

    /// <summary>
    /// Form-binder shape for the order-entry POST. Mirrors <see cref="OrderDraft"/>
    /// using primitives the default MVC model binder can populate from an
    /// <c>application/x-www-form-urlencoded</c> payload, plus the multi-valued
    /// <c>Acknowledged</c> array of card fingerprints the user ticked.
    /// </summary>
    public sealed class OrderForm
    {
        public string DrugName { get; set; } = string.Empty;
        public string Strength { get; set; } = string.Empty;
        public string? Form { get; set; }
        public string? Route { get; set; }
        public string? Frequency { get; set; }
        public string? Quantity { get; set; }
        public int Refills { get; set; }
        public bool AsNeeded { get; set; }
        public string? PrnReason { get; set; }
        public string? PharmacyId { get; set; }
        public bool SubstitutionAllowed { get; set; } = true;
        public string? NoteToPharmacist { get; set; }
        public string[]? Acknowledged { get; set; }

        public OrderDraft ToDraft(IReadOnlyList<PharmacyEntry> pharmacies)
        {
            var entry = string.IsNullOrEmpty(PharmacyId)
                ? null
                : pharmacies.FirstOrDefault(p => string.Equals(p.Id, PharmacyId, StringComparison.Ordinal));
            return new OrderDraft(
                DrugName: DrugName,
                Strength: Strength,
                Form: Form,
                Route: Route,
                Frequency: Frequency,
                Quantity: Quantity,
                Refills: Refills,
                AsNeeded: AsNeeded,
                PrnReason: PrnReason,
                PharmacyId: entry?.Id,
                PharmacyDisplay: entry?.DisplayName,
                SubstitutionAllowed: SubstitutionAllowed,
                NoteToPharmacist: NoteToPharmacist);
        }
    }

    /// <summary>Form-binder shape for the sign-off POST.</summary>
    public sealed class SignOffForm
    {
        public string EncounterId { get; set; } = string.Empty;
    }

    /// <summary>
    /// Form-binder shape for lab + imaging order POSTs. <see cref="OrderText"/>
    /// is nullable to bypass ASP.NET's implicit required-string validation —
    /// the service-side <see cref="ServiceRequestService.SignAsync"/> guard
    /// returns a friendly "Enter a test or procedure" error instead of a
    /// raw 400 ModelState response.
    /// </summary>
    public sealed class ServiceRequestForm
    {
        public string? OrderText { get; set; }
        public string? Reason { get; set; }
        public string? Priority { get; set; }

        public ServiceRequestDraft ToDraft() => new(OrderText ?? string.Empty, Reason, Priority);
    }

    /// <summary>Form-binder shape for the SOAP note POST.</summary>
    public sealed class NoteForm
    {
        public string Subjective { get; set; } = string.Empty;
        public string Objective { get; set; } = string.Empty;
        public string Assessment { get; set; } = string.Empty;
        public string Plan { get; set; } = string.Empty;

        public NoteDraft ToDraft() => new(Subjective, Objective, Assessment, Plan);
    }

    private static string NavBar() =>
        "<span class=\"brand\">Chiron</span>"
        + "<a href=\"/app/panel\">Panel</a>"
        + "<a href=\"/app/search\">Search</a>"
        + "<a href=\"/cds-services\">CDS Hooks</a>"
        + "<a href=\"/smart/launch?iss=https://fhir-ehr-code.cerner.com/r4/ec2458f2-1e24-41c8-b71b-0e701af7583d\">SMART launch</a>";
}
