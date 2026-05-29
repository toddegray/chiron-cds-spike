using Chiron.Cds.Web.CdsHooks.Models;
using Chiron.Cds.Web.Configuration;
using Chiron.Cds.Web.Panel;
using Chiron.Cds.Web.SmartLaunch;
using FluentAssertions;

namespace Chiron.Cds.Web.IntegrationTests;

public class OrderEntryRendererTests
{
    private static readonly ChartShell.Header Hdr = new("p1", "SMITH, ANNIE", "35y · Female", "1980-01-01", "p1");
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
    public void Medication_Page_Renders_Order_SubNav_With_Medication_Active()
    {
        var html = OrderEntryRenderer.Render(View(), Hdr);
        html.Should().Contain("class=\"order-subnav\"",
            because: "the medication page carries the same Medication/Labs/Imaging strip as the sibling order sub-pages");
        html.Should().Contain("href=\"/app/patient/p1/orders\" class=\"active\">Medication</a>");
        html.Should().Contain("href=\"/app/patient/p1/orders/labs\">Labs</a>");
        html.Should().Contain("href=\"/app/patient/p1/orders/imaging\">Imaging</a>");
    }

    [Fact]
    public void Empty_View_Renders_Form_With_Pharmacy_Dropdown_And_Single_Sign_Button()
    {
        var html = OrderEntryRenderer.Render(View(), Hdr);
        html.Should().Contain("SMITH, ANNIE", because: "the patient name shows in the shell top bar");
        html.Should().Contain("name=\"DrugName\"");
        html.Should().Contain("name=\"Strength\"");
        html.Should().Contain("name=\"Refills\"");
        html.Should().Contain("<option value=\"cvs\"");
        html.Should().Contain("CVS Pharmacy — sandbox stub");
        html.Should().Contain(">Sign order</button>",
            because: "the single-button UX has one Sign action (no separate Check button)");
        html.Should().NotContain("Check CDS",
            because: "the old Check button is gone — Sign always runs CDS first");
        html.Should().Contain("class=\"tab active\" href=\"/app/patient/p1/orders\"", because: "the Orders tab is highlighted active on the medication order page");
    }

    [Fact]
    public void Blocked_Status_Renders_Warning_Banner_And_Acknowledge_Checkbox()
    {
        var card = new CdsCard("Critical interaction", "critical",
            new CdsCardSource("Chiron"), "Stop.", "fp-critical", Array.Empty<CdsCoding>());
        var html = OrderEntryRenderer.Render(
            View(OrderEntryStatus.Blocked,
                cards: new[] { card },
                message: "Acknowledge 1 critical alert to sign."),
            Hdr);
        html.Should().Contain("class=\"banner warn\"");
        html.Should().Contain("Acknowledge 1 critical alert to sign.");
        html.Should().Contain("class=\"badge critical\">CRITICAL");
        html.Should().Contain("name=\"Acknowledged\" value=\"fp-critical\"",
            because: "the critical card carries a real, functional acknowledge checkbox");
        html.Should().Contain("form=\"order-form\"",
            because: "the checkbox lives outside the form element but posts under the form via the form attribute");
        html.Should().Contain(">Sign with 1 acknowledgement</button>",
            because: "the button label reflects how many unacked criticals remain");
    }

    [Fact]
    public void Two_Unacked_Critical_Cards_Render_Pluralised_Button_Label()
    {
        var c1 = new CdsCard("Critical A", "critical",
            new CdsCardSource("Chiron"), "Stop A.", "fp-a", Array.Empty<CdsCoding>());
        var c2 = new CdsCard("Critical B", "critical",
            new CdsCardSource("Chiron"), "Stop B.", "fp-b", Array.Empty<CdsCoding>());
        var html = OrderEntryRenderer.Render(
            View(OrderEntryStatus.Blocked, cards: new[] { c1, c2 }),
            Hdr);
        html.Should().Contain(">Sign with 2 acknowledgements</button>",
            because: "two unacked criticals render the plural label, exercising the != 1 branch");
    }

    [Fact]
    public void Already_Acknowledged_Critical_Card_Renders_Checked_Checkbox_And_Plain_Sign_Button()
    {
        var card = new CdsCard("Critical interaction", "critical",
            new CdsCardSource("Chiron"), "Stop.", "fp-critical", Array.Empty<CdsCoding>());
        var ack = new HashSet<string>(StringComparer.Ordinal) { "fp-critical" };
        var html = OrderEntryRenderer.Render(
            View(OrderEntryStatus.Blocked, cards: new[] { card }, acknowledged: ack),
            Hdr);
        html.Should().Contain("name=\"Acknowledged\" value=\"fp-critical\" checked",
            because: "the box stays ticked when the fingerprint is already acked so the user doesn't re-tick across resubmits");
        html.Should().Contain(">Sign order</button>",
            because: "with the critical alert acked, the button collapses back to plain 'Sign order'");
        html.Should().NotContain("<input type=\"hidden\" name=\"Acknowledged\" value=\"fp-critical\"",
            because: "the checkbox is the single source of truth — a hidden duplicate would let unchecking re-ack via the hidden post");
    }

    [Fact]
    public void Hidden_Acknowledgement_Survives_Only_When_Card_Is_Not_Currently_Displayed()
    {
        // Critical card 'fp-shown' is on screen; ack 'fp-gone' has no
        // matching card. The renderer should hidden-input only the latter.
        var card = new CdsCard("Shown", "critical", new CdsCardSource("Chiron"), null, "fp-shown",
            Array.Empty<CdsCoding>());
        var ack = new HashSet<string>(StringComparer.Ordinal) { "fp-shown", "fp-gone" };
        var html = OrderEntryRenderer.Render(
            View(OrderEntryStatus.Blocked, cards: new[] { card }, acknowledged: ack), Hdr);
        html.Should().Contain("<input type=\"hidden\" name=\"Acknowledged\" value=\"fp-gone\"");
        html.Should().NotContain("<input type=\"hidden\" name=\"Acknowledged\" value=\"fp-shown\"");
    }

    [Fact]
    public void NotAuthorised_Status_Renders_Sign_In_Pane_Linking_To_Smart_Launch()
    {
        var html = OrderEntryRenderer.Render(
            View(OrderEntryStatus.NotAuthorised),
            Hdr);
        html.Should().Contain("class=\"signin-pane\"");
        html.Should().Contain("Sign in to write orders");
        html.Should().Contain("href=\"/smart/launch\"",
            because: "the no-session page links straight to the SMART launch entry");
        html.Should().Contain("href=\"/app/patient/p1/orders\"",
            because: "the secondary action returns to the draft form");
        html.Should().NotContain("preview-json",
            because: "no FHIR JSON dump on the no-session page — the user never sees dev-toolbox surfaces");
        html.Should().NotContain("Pending FHIR payload");
        html.Should().NotContain("\"resourceType\"",
            because: "no synthesised FHIR resource anywhere on the sign-in page");
    }

    [Fact]
    public void Failed_Status_Surfaces_An_Error_Banner_And_Keeps_The_Form()
    {
        var html = OrderEntryRenderer.Render(
            View(OrderEntryStatus.Failed, message: "FHIR write failed: FHIR 403 Forbidden"),
            Hdr);
        html.Should().Contain("class=\"banner err\"");
        html.Should().Contain("FHIR 403 Forbidden");
        html.Should().Contain("name=\"DrugName\"");
    }

    [Fact]
    public void Signed_Status_Replaces_Form_With_Success_Banner_And_Back_Link()
    {
        var html = OrderEntryRenderer.Render(
            View(OrderEntryStatus.SignedOk, writtenId: "MR-12345"),
            Hdr);
        html.Should().Contain("class=\"banner ok\"");
        html.Should().Contain("<code>MR-12345</code>");
        html.Should().Contain("href=\"/app/patient/p1\"");
        html.Should().NotContain("name=\"DrugName\"");
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
        var html = OrderEntryRenderer.Render(View(OrderEntryStatus.Blocked, draft: draft), Hdr);
        html.Should().Contain("value=\"warfarin\"");
        html.Should().Contain("value=\"5 mg\"");
        html.Should().Contain("value=\"30 tablets\"");
        html.Should().Contain("value=\"5\"");
        html.Should().Contain("<option value=\"rite-aid\" selected");
        html.Should().Contain("patient is allergic to vitamin K");
        html.Should().Contain("<option value=\"Oral\" selected");
        html.Should().Contain("<option value=\"Once daily\" selected");
    }

    [Fact]
    public void Renderer_Html_Encodes_Hostile_Strings()
    {
        var draft = OrderDraft.Empty with { DrugName = "<script>alert('xss')</script>", NoteToPharmacist = "<img src=x>" };
        var card = new CdsCard("<svg onload=alert(1)>", "critical",
            new CdsCardSource("Chiron"), "<script>", "fp", Array.Empty<CdsCoding>());
        var html = OrderEntryRenderer.Render(
            View(OrderEntryStatus.Blocked, draft: draft, cards: new[] { card }), Hdr);
        html.Should().NotContain("<script>alert");
        html.Should().NotContain("<svg onload=alert");
        html.Should().NotContain("<img src=x>");
        html.Should().Contain("&lt;script&gt;");
        html.Should().Contain("&lt;svg");
        html.Should().Contain("&lt;img");
    }
}
