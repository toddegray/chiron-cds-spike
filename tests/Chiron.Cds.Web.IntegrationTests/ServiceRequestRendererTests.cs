using Chiron.Cds.Web.Panel;
using FluentAssertions;

namespace Chiron.Cds.Web.IntegrationTests;

public class ServiceRequestRendererTests
{
    private const string NavBar = "<span class=\"brand\">Chiron</span>";

    private static ServiceRequestView View(
        ServiceRequestCategory category = ServiceRequestCategory.Laboratory,
        ServiceRequestStatus status = ServiceRequestStatus.Empty,
        ServiceRequestDraft? draft = null,
        IReadOnlyList<ServiceRequestSummary>? history = null,
        string? message = null,
        string? pageError = null,
        string? writtenId = null) => new(
            PatientId: "p1",
            PatientDisplayName: "SMITH, ANNIE",
            PatientSubline: "35y · Female · MRN p1",
            Category: category,
            Draft: draft ?? ServiceRequestDraft.Empty,
            History: history ?? Array.Empty<ServiceRequestSummary>(),
            Status: status,
            Message: message,
            PageError: pageError,
            WrittenId: writtenId);

    [Fact]
    public void Lab_Page_Renders_Sub_Nav_With_Labs_Active()
    {
        var html = ServiceRequestRenderer.Render(View(ServiceRequestCategory.Laboratory), NavBar);
        html.Should().Contain("class=\"order-subnav\"");
        html.Should().Contain("href=\"/app/patient/p1/orders\">Medication</a>");
        html.Should().Contain("href=\"/app/patient/p1/orders/labs\" class=\"active\">Labs</a>");
        html.Should().Contain("href=\"/app/patient/p1/orders/imaging\">Imaging</a>");
        html.Should().Contain("name=\"OrderText\"");
        html.Should().Contain(">Sign order</button>");
    }

    [Fact]
    public void Imaging_Page_Renders_Sub_Nav_With_Imaging_Active()
    {
        var html = ServiceRequestRenderer.Render(View(ServiceRequestCategory.Imaging), NavBar);
        html.Should().Contain("href=\"/app/patient/p1/orders/imaging\" class=\"active\">Imaging</a>");
        html.Should().Contain("MRI brain",
            because: "the imaging hint text references typical imaging studies");
    }

    [Fact]
    public void Lab_Hint_Names_Concrete_Lab_Examples()
    {
        var html = ServiceRequestRenderer.Render(View(ServiceRequestCategory.Laboratory), NavBar);
        html.Should().Contain("CBC with diff");
        html.Should().Contain("HbA1c");
    }

    [Fact]
    public void History_Renders_Prior_Orders_With_Status_And_Priority_Pills()
    {
        var history = new[]
        {
            new ServiceRequestSummary("Lipid Panel", "Active", "Routine", "ASCVD risk",
                DateTimeOffset.Parse("2026-04-15T08:00:00Z")),
            new ServiceRequestSummary("HbA1c", "Completed", null, null,
                DateTimeOffset.Parse("2025-12-01T08:00:00Z")),
        };
        var html = ServiceRequestRenderer.Render(View(history: history), NavBar);
        html.Should().Contain("Lipid Panel");
        html.Should().Contain("status-active");
        html.Should().Contain("HbA1c");
        html.Should().Contain("status-completed");
        html.Should().Contain("Routine");
        html.Should().Contain("ASCVD risk");
        html.Should().Contain("2026-04-15");
    }

    [Fact]
    public void Empty_History_Renders_Empty_State()
    {
        var html = ServiceRequestRenderer.Render(View(ServiceRequestCategory.Imaging), NavBar);
        html.Should().Contain("No prior imaging orders");
    }

    [Fact]
    public void Draft_Echoes_Submitted_Values()
    {
        var draft = new ServiceRequestDraft("Lipid Panel", "CV risk", "urgent");
        var html = ServiceRequestRenderer.Render(View(draft: draft), NavBar);
        html.Should().Contain("value=\"Lipid Panel\"");
        html.Should().Contain("value=\"CV risk\"");
        html.Should().Contain("<option value=\"urgent\" selected");
    }

    [Fact]
    public void Signed_Status_Renders_Success_Banner_And_Followup_Links()
    {
        var html = ServiceRequestRenderer.Render(
            View(ServiceRequestCategory.Laboratory, status: ServiceRequestStatus.SignedOk, writtenId: "sr-99"),
            NavBar);
        html.Should().Contain("class=\"banner ok\"");
        html.Should().Contain("<code>sr-99</code>");
        html.Should().Contain("href=\"/app/patient/p1/orders/labs\"",
            because: "the success banner links back to place another lab order");
        html.Should().Contain("href=\"/app/patient/p1\"",
            because: "the success banner also offers a link back to the Visit Brief");
    }

    [Fact]
    public void NotAuthorised_Renders_Sign_In_Pane_Linking_To_Smart_Launch()
    {
        var html = ServiceRequestRenderer.Render(
            View(status: ServiceRequestStatus.NotAuthorised), NavBar);
        html.Should().Contain("class=\"signin-pane\"");
        html.Should().Contain("Sign in to place laboratory orders");
        html.Should().Contain("href=\"/smart/launch\"");
        html.Should().NotContain("\"resourceType\"",
            because: "no synthesised payload on the sign-in pane");
    }

    [Fact]
    public void Failed_Status_Renders_Error_Banner_And_Keeps_Form()
    {
        var html = ServiceRequestRenderer.Render(
            View(status: ServiceRequestStatus.Failed, message: "FHIR write failed: FHIR 403 Forbidden"),
            NavBar);
        html.Should().Contain("class=\"banner err\"");
        html.Should().Contain("FHIR 403 Forbidden");
        html.Should().Contain("name=\"OrderText\"");
    }

    [Fact]
    public void PageError_Renders_Banner_When_The_Underlying_Search_Failed()
    {
        var html = ServiceRequestRenderer.Render(
            View(pageError: "Timed out"), NavBar);
        html.Should().Contain("class=\"banner err\"");
        html.Should().Contain("Timed out",
            because: "a search failure surfaces as a banner above the form so the user knows the history is incomplete");
        html.Should().Contain("name=\"OrderText\"",
            because: "the form still renders — the search-failure doesn't gate authoring a new order");
    }

    [Fact]
    public void Renderer_Html_Encodes_Hostile_Strings()
    {
        var draft = new ServiceRequestDraft("<script>alert('o')</script>", "<img src=x>", "stat");
        var history = new[]
        {
            new ServiceRequestSummary("<svg onload=alert(1)>", "Active", "Routine", "<b>r</b>",
                DateTimeOffset.UtcNow),
        };
        var html = ServiceRequestRenderer.Render(View(draft: draft, history: history), NavBar);
        html.Should().NotContain("<script>alert");
        html.Should().NotContain("<img src=x>");
        html.Should().NotContain("<svg onload=alert");
        html.Should().NotContain("<b>r</b>");
        html.Should().Contain("&lt;script&gt;");
        html.Should().Contain("&lt;svg");
        html.Should().Contain("&lt;b&gt;r&lt;/b&gt;");
    }
}
