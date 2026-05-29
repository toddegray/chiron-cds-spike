using Chiron.Cds.Web.Panel;
using FluentAssertions;

namespace Chiron.Cds.Web.IntegrationTests;

public class EncounterCloseRendererTests
{
    private static readonly ChartShell.Header Hdr = new("p1", "SMITH, ANNIE", "35y · Female", "1980-01-01", "p1");

    private static SignOffView View(
        SignOffStatus status = SignOffStatus.Empty,
        IReadOnlyList<EncounterSummary>? encounters = null,
        string? message = null,
        string? pageError = null,
        string? writtenId = null) => new(
            PatientId: "p1",
            PatientDisplayName: "SMITH, ANNIE",
            PatientSubline: "35y · Female · MRN p1",
            Encounters: encounters ?? Array.Empty<EncounterSummary>(),
            Status: status,
            Message: message,
            PageError: pageError,
            WrittenId: writtenId);

    [Fact]
    public void Empty_Encounter_List_Renders_Empty_State()
    {
        var html = EncounterCloseRenderer.Render(View(), Hdr);
        html.Should().Contain("No encounters on file");
        html.Should().Contain("class=\"tab active\" href=\"/app/patient/p1/signoff\"", because: "the Sign off tab is active");
    }

    [Fact]
    public void In_Progress_Encounter_Renders_With_Close_Button()
    {
        var e = new EncounterSummary("e1", "Outpatient", "ambulatory", "InProgress",
            DateTimeOffset.Parse("2026-05-25T09:00:00Z"), null);
        var html = EncounterCloseRenderer.Render(View(encounters: new[] { e }), Hdr);
        html.Should().Contain("Outpatient");
        html.Should().Contain("ambulatory");
        html.Should().Contain("status-inprogress");
        html.Should().Contain(">Sign off and close</button>");
        html.Should().Contain("name=\"EncounterId\" value=\"e1\"");
        html.Should().Contain("in progress",
            because: "the period shows 'in progress' when no end date is present");
    }

    [Fact]
    public void Finished_Encounter_Has_No_Close_Button()
    {
        var e = new EncounterSummary("e1", "Outpatient", "ambulatory", "Finished",
            DateTimeOffset.Parse("2020-01-15T09:00:00Z"),
            DateTimeOffset.Parse("2020-01-15T09:30:00Z"));
        var html = EncounterCloseRenderer.Render(View(encounters: new[] { e }), Hdr);
        html.Should().Contain("status-finished");
        html.Should().Contain("2020-01-15");
        html.Should().NotContain("Sign off and close",
            because: "finished encounters are not actionable — no close button");
    }

    [Fact]
    public void ClosedOk_Status_Renders_Success_Banner_And_Next_Patient_Link()
    {
        var html = EncounterCloseRenderer.Render(
            View(SignOffStatus.ClosedOk, writtenId: "e1"), Hdr);
        html.Should().Contain("class=\"banner ok\"");
        html.Should().Contain("<code>e1</code>");
        html.Should().Contain("href=\"/app/patient/p1\"");
        html.Should().Contain("href=\"/app/panel\"",
            because: "the success page offers a 'Next patient' link back to the panel");
    }

    [Fact]
    public void NotAuthorised_Status_Renders_Sign_In_Pane()
    {
        var html = EncounterCloseRenderer.Render(View(SignOffStatus.NotAuthorised), Hdr);
        html.Should().Contain("class=\"signin-pane\"");
        html.Should().Contain("Sign in to close encounters");
        html.Should().Contain("href=\"/smart/launch\"");
        html.Should().NotContain("class=\"enc-list\"",
            because: "the encounter list is suppressed on the sign-in pane");
        html.Should().NotContain("Sign off and close",
            because: "no close button when we're showing the sign-in prompt");
    }

    [Fact]
    public void AlreadyClosed_Renders_Info_Banner_Keeps_List()
    {
        var e = new EncounterSummary("e1", "Outpatient", "ambulatory", "Finished",
            DateTimeOffset.Parse("2020-01-15T09:00:00Z"),
            DateTimeOffset.Parse("2020-01-15T09:30:00Z"));
        var html = EncounterCloseRenderer.Render(
            View(SignOffStatus.AlreadyClosed,
                encounters: new[] { e },
                message: "That encounter is already marked finished — nothing to update."),
            Hdr);
        html.Should().Contain("class=\"banner info\"");
        html.Should().Contain("already marked finished");
        html.Should().Contain("status-finished",
            because: "the existing list still renders so the user has context for the message");
    }

    [Fact]
    public void Failed_Status_Renders_Error_Banner()
    {
        var html = EncounterCloseRenderer.Render(
            View(SignOffStatus.Failed, message: "FHIR update failed: FHIR 403 Forbidden"),
            Hdr);
        html.Should().Contain("class=\"banner err\"");
        html.Should().Contain("FHIR 403 Forbidden");
    }

    [Fact]
    public void Page_Error_Renders_Banner_Independently_Of_Status_Message()
    {
        var html = EncounterCloseRenderer.Render(
            View(pageError: "Timed out"), Hdr);
        html.Should().Contain("class=\"banner err\"");
        html.Should().Contain("Timed out");
    }

    [Fact]
    public void Renderer_Html_Encodes_Hostile_Encounter_Strings()
    {
        var e = new EncounterSummary("e1",
            Type: "<script>alert('t')</script>",
            Class: "<img src=x>",
            Status: "<svg onload=alert(1)>",
            PeriodStart: DateTimeOffset.UtcNow,
            PeriodEnd: null);
        var html = EncounterCloseRenderer.Render(View(encounters: new[] { e }), Hdr);
        html.Should().NotContain("<script>alert");
        html.Should().NotContain("<img src=x>");
        html.Should().NotContain("<svg onload");
        html.Should().Contain("&lt;script&gt;");
        html.Should().Contain("&lt;img");
        html.Should().Contain("&lt;svg");
    }
}
