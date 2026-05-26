using Chiron.Cds.Web.Panel;
using FluentAssertions;

namespace Chiron.Cds.Web.IntegrationTests;

public class NoteEntryRendererTests
{
    private const string NavBar = "<span class=\"brand\">Chiron</span>";

    private static NoteEntryView View(
        NoteEntryStatus status = NoteEntryStatus.Empty,
        NoteDraft? draft = null,
        IReadOnlyList<NoteSummary>? history = null,
        string? message = null,
        string? chartError = null,
        string? writtenId = null) => new(
            PatientId: "p1",
            PatientDisplayName: "SMITH, ANNIE",
            PatientSubline: "35y · Female · MRN p1",
            Draft: draft ?? NoteDraft.Empty,
            History: history ?? Array.Empty<NoteSummary>(),
            Status: status,
            Message: message,
            ChartError: chartError,
            WrittenId: writtenId);

    [Fact]
    public void Empty_View_Renders_All_Four_SOAP_Textareas_And_Sign_Button()
    {
        var html = NoteEntryRenderer.Render(View(), NavBar);
        html.Should().Contain("<h1>SMITH, ANNIE</h1>");
        html.Should().Contain("name=\"Subjective\"");
        html.Should().Contain("name=\"Objective\"");
        html.Should().Contain("name=\"Assessment\"");
        html.Should().Contain("name=\"Plan\"");
        html.Should().Contain(">Sign and save note</button>");
        html.Should().MatchRegex("rail-step active\"><a href=\"/app/patient/p1/notes\"",
            because: "the Notes step on the rail is highlighted active on the notes page");
    }

    [Fact]
    public void Form_Echoes_Submitted_Draft_Across_Renders()
    {
        var draft = new NoteDraft(
            Subjective: "Patient reports 3 days of cough.",
            Objective: "Lungs clear bilaterally.",
            Assessment: "Acute viral URI.",
            Plan: "Symptomatic care; recheck in 7 days.");
        var html = NoteEntryRenderer.Render(View(draft: draft), NavBar);
        html.Should().Contain("Patient reports 3 days of cough.");
        html.Should().Contain("Lungs clear bilaterally.");
        html.Should().Contain("Acute viral URI.");
        html.Should().Contain("Symptomatic care");
    }

    [Fact]
    public void History_List_Renders_Prior_Notes_With_Status_Pills()
    {
        var history = new[]
        {
            new NoteSummary("Progress note", "Clinical Note", "current",
                DateTimeOffset.Parse("2024-03-15T00:00:00Z")),
            new NoteSummary("Discharge summary", "Clinical Note", "superseded",
                DateTimeOffset.Parse("2023-11-02T00:00:00Z")),
        };
        var html = NoteEntryRenderer.Render(View(history: history), NavBar);
        html.Should().Contain("Progress note");
        html.Should().Contain("Discharge summary");
        html.Should().Contain("status-current");
        html.Should().Contain("status-superseded");
        html.Should().Contain("2024-03-15");
        html.Should().Contain("2023-11-02");
    }

    [Fact]
    public void Empty_History_Renders_Empty_State()
    {
        var html = NoteEntryRenderer.Render(View(), NavBar);
        html.Should().Contain("No prior notes on file");
    }

    [Fact]
    public void ChartError_Renders_Banner_While_Keeping_Form_Usable()
    {
        var html = NoteEntryRenderer.Render(View(chartError: "Timed out"), NavBar);
        html.Should().Contain("class=\"banner err\"");
        html.Should().Contain("Timed out");
        html.Should().Contain("name=\"Subjective\"",
            because: "a flaky chart load must not gate the SOAP form — the user can still author and save");
    }

    [Fact]
    public void NotAuthorised_Status_Renders_Sign_In_Pane_Linking_To_Smart_Launch()
    {
        var html = NoteEntryRenderer.Render(View(NoteEntryStatus.NotAuthorised), NavBar);
        html.Should().Contain("class=\"signin-pane\"");
        html.Should().Contain("Sign in to save the note");
        html.Should().Contain("href=\"/smart/launch\"");
        html.Should().NotContain("name=\"Subjective\"",
            because: "the SOAP form is suppressed on the sign-in pane — user picks an action, not a draft");
        html.Should().NotContain("\"resourceType\"",
            because: "no synthesised DocumentReference dump anywhere on the page");
    }

    [Fact]
    public void Failed_Status_Renders_Error_Banner_And_Form()
    {
        var html = NoteEntryRenderer.Render(
            View(NoteEntryStatus.Failed, message: "FHIR write failed: FHIR 403 Forbidden"),
            NavBar);
        html.Should().Contain("class=\"banner err\"");
        html.Should().Contain("FHIR 403 Forbidden");
        html.Should().Contain("name=\"Subjective\"");
    }

    [Fact]
    public void SignedOk_Renders_Success_Banner_With_Server_Id_And_Back_Link()
    {
        var html = NoteEntryRenderer.Render(
            View(NoteEntryStatus.SignedOk, writtenId: "DR-9988"),
            NavBar);
        html.Should().Contain("class=\"banner ok\"");
        html.Should().Contain("<code>DR-9988</code>");
        html.Should().Contain("href=\"/app/patient/p1\"");
        html.Should().NotContain("name=\"Subjective\"");
    }

    [Fact]
    public void Renderer_Html_Encodes_Hostile_Strings()
    {
        var draft = new NoteDraft(
            Subjective: "<script>alert('s')</script>",
            Objective: "<img src=x onerror=alert(1)>",
            Assessment: "",
            Plan: "");
        var history = new[]
        {
            new NoteSummary("<svg onload=alert(2)>", "<b>Cat</b>", "current", DateTimeOffset.UtcNow),
        };
        var html = NoteEntryRenderer.Render(View(draft: draft, history: history), NavBar);
        html.Should().NotContain("<script>alert");
        html.Should().NotContain("<img src=x onerror");
        html.Should().NotContain("<svg onload=alert");
        html.Should().NotContain("<b>Cat</b>");
        html.Should().Contain("&lt;script&gt;");
        html.Should().Contain("&lt;svg");
        html.Should().Contain("&lt;b&gt;Cat&lt;/b&gt;");
    }
}
