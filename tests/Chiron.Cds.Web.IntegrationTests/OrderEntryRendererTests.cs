using Chiron.Cds.Web.CdsHooks.Models;
using Chiron.Cds.Web.Configuration;
using Chiron.Cds.Web.Panel;
using Chiron.Cds.Web.SmartLaunch;
using FluentAssertions;

namespace Chiron.Cds.Web.IntegrationTests;

public class OrderEntryRendererTests
{
    private const string NavBar = "<span class=\"brand\">Chiron</span>";
    private static readonly IReadOnlyList<ChartTab> Tabs = new[]
    {
        new ChartTab("Visit brief", "/app/patient/p1", IsActive: false),
        new ChartTab("Orders", "/app/patient/p1/orders", IsActive: true),
    };
    private static readonly IReadOnlyList<PharmacyEntry> Pharmacies = new[]
    {
        new PharmacyEntry { Id = "cvs", DisplayName = "CVS Pharmacy — sandbox stub" },
        new PharmacyEntry { Id = "rite-aid", DisplayName = "Rite Aid — sandbox stub" },
    };

    private static OrderEntryView View(
        OrderEntryStatus status = OrderEntryStatus.Empty,
        OrderDraft? draft = null,
        IReadOnlyList<CdsCard>? cards = null,
        string? message = null,
        string? writtenId = null,
        IReadOnlySet<string>? acknowledged = null) => new(
            PatientId: "p1",
            PatientDisplayName: "SMITH, ANNIE",
            PatientSubline: "35y · Female · MRN p1",
            Draft: draft ?? OrderDraft.Empty,
            Cards: cards ?? Array.Empty<CdsCard>(),
            Pharmacies: Pharmacies,
            AcknowledgedFingerprints: acknowledged ?? new HashSet<string>(StringComparer.Ordinal),
            Status: status,
            Message: message,
            WrittenId: writtenId);

    [Fact]
    public void Empty_View_Renders_Form_With_Pharmacy_Dropdown_And_Two_Buttons()
    {
        var html = OrderEntryRenderer.Render(View(), NavBar, Tabs);
        html.Should().Contain("<h1>SMITH, ANNIE</h1>");
        html.Should().Contain("name=\"DrugName\"");
        html.Should().Contain("name=\"Strength\"");
        html.Should().Contain("name=\"Refills\"");
        html.Should().Contain("<option value=\"cvs\"");
        html.Should().Contain("CVS Pharmacy — sandbox stub");
        html.Should().Contain("name=\"Action\" value=\"check\"");
        html.Should().Contain("name=\"Action\" value=\"sign\"");
        html.Should().Contain("chart-tab active",
            because: "the Orders tab is rendered as the active tab on this page");
    }

    [Fact]
    public void Checked_Status_Renders_Info_Banner_And_Card_Stack()
    {
        var card = new CdsCard(
            Summary: "Drug-drug interaction",
            Indicator: "warning",
            Source: new CdsCardSource("Chiron"),
            Detail: "Watch for hypoglycemia.",
            Uuid: "fp1",
            OverrideReasons: Array.Empty<CdsCoding>());
        var html = OrderEntryRenderer.Render(View(OrderEntryStatus.Checked, cards: new[] { card }), NavBar, Tabs);
        html.Should().Contain("class=\"banner info\"");
        html.Should().Contain("Drug-drug interaction");
        html.Should().Contain("class=\"badge warning\">WARNING");
        html.Should().Contain("Watch for hypoglycemia");
    }

    [Fact]
    public void Blocked_Status_Surfaces_The_Message_And_Cards_Together()
    {
        var card = new CdsCard("Critical interaction", "critical",
            new CdsCardSource("Chiron"), "Stop.", "fp2", Array.Empty<CdsCoding>());
        var html = OrderEntryRenderer.Render(
            View(OrderEntryStatus.Blocked,
                cards: new[] { card },
                message: "Critical CDS alerts are not acknowledged: fp2"),
            NavBar, Tabs);
        html.Should().Contain("class=\"banner warn\"");
        html.Should().Contain("Critical CDS alerts are not acknowledged: fp2");
        html.Should().Contain("class=\"badge critical\">CRITICAL");
    }

    [Fact]
    public void Not_Authorised_Status_Surfaces_The_Smart_Session_Hint()
    {
        var html = OrderEntryRenderer.Render(
            View(OrderEntryStatus.NotAuthorised,
                message: "Signing requires an authenticated SMART session — open /smart/launch first."),
            NavBar, Tabs);
        html.Should().Contain("class=\"banner warn\"");
        html.Should().Contain("Signing requires an authenticated SMART session");
    }

    [Fact]
    public void Failed_Status_Surfaces_An_Error_Banner_And_Keeps_The_Form()
    {
        var html = OrderEntryRenderer.Render(
            View(OrderEntryStatus.Failed, message: "FHIR write failed: FHIR 403 Forbidden"),
            NavBar, Tabs);
        html.Should().Contain("class=\"banner err\"");
        html.Should().Contain("FHIR 403 Forbidden");
        html.Should().Contain("name=\"DrugName\"",
            because: "the form stays available so the doctor can adjust and resubmit");
    }

    [Fact]
    public void Signed_Status_Replaces_Form_With_Success_Banner_And_Back_Link()
    {
        var html = OrderEntryRenderer.Render(
            View(OrderEntryStatus.SignedOk, writtenId: "MR-12345"),
            NavBar, Tabs);
        html.Should().Contain("class=\"banner ok\"");
        html.Should().Contain("<code>MR-12345</code>");
        html.Should().Contain("href=\"/app/patient/p1\"",
            because: "the success page links back to the Visit Brief");
        html.Should().NotContain("name=\"DrugName\"",
            because: "the form is suppressed after a successful sign so the user moves on");
    }

    [Fact]
    public void Form_Preserves_Submitted_Values_On_Re_Render()
    {
        var draft = new OrderDraft(
            DrugName: "warfarin", Strength: "5 mg", Form: "tablet",
            Route: "Oral", Frequency: "Once daily", Quantity: "30 tablets",
            Refills: 5, AsNeeded: false, PrnReason: null,
            PharmacyId: "rite-aid", PharmacyDisplay: "Rite Aid — sandbox stub",
            SubstitutionAllowed: false, NoteToPharmacist: "patient is allergic to vitamin K");
        var html = OrderEntryRenderer.Render(View(OrderEntryStatus.Checked, draft: draft), NavBar, Tabs);
        html.Should().Contain("value=\"warfarin\"");
        html.Should().Contain("value=\"5 mg\"");
        html.Should().Contain("value=\"30 tablets\"");
        html.Should().Contain("value=\"5\"",
            because: "the Refills numeric input must echo the prior value");
        html.Should().Contain("<option value=\"rite-aid\" selected");
        html.Should().Contain("patient is allergic to vitamin K");
        html.Should().Contain("<option value=\"Oral\" selected");
        html.Should().Contain("<option value=\"Once daily\" selected");
    }

    [Fact]
    public void Acknowledged_Fingerprints_Render_As_Hidden_Inputs()
    {
        var ack = new HashSet<string>(StringComparer.Ordinal) { "fp-a", "fp-b" };
        var html = OrderEntryRenderer.Render(View(OrderEntryStatus.Blocked, acknowledged: ack), NavBar, Tabs);
        html.Should().Contain("<input type=\"hidden\" name=\"Acknowledged\" value=\"fp-a\" />");
        html.Should().Contain("<input type=\"hidden\" name=\"Acknowledged\" value=\"fp-b\" />");
    }

    [Fact]
    public void Renderer_Html_Encodes_Hostile_Strings()
    {
        var draft = OrderDraft.Empty with { DrugName = "<script>alert('xss')</script>", NoteToPharmacist = "<img src=x>" };
        var card = new CdsCard("<svg onload=alert(1)>", "critical",
            new CdsCardSource("Chiron"), "<script>", "fp", Array.Empty<CdsCoding>());
        var html = OrderEntryRenderer.Render(
            View(OrderEntryStatus.Checked, draft: draft, cards: new[] { card }), NavBar, Tabs);
        html.Should().NotContain("<script>alert");
        html.Should().NotContain("<svg onload=alert");
        html.Should().NotContain("<img src=x>");
        html.Should().Contain("&lt;script&gt;");
        html.Should().Contain("&lt;svg");
        html.Should().Contain("&lt;img");
    }
}
