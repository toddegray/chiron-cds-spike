using Chiron.Cds.Web.SmartLaunch;
using FluentAssertions;

namespace Chiron.Cds.Web.IntegrationTests;

public class WorklistRendererTests
{
    private const string DrillUrl = "/app/patient";

    private static WorklistRow Row(
        string id = "p1",
        string name = "Test Patient",
        string ageSex = "55y · Female",
        string? flag = "Mammography overdue",
        string? severity = "info",
        int alertCount = 1) => new(id, name, ageSex, flag, severity, alertCount);

    private static string Render(
        IReadOnlyList<WorklistRow> rows,
        string drillBaseUrl = DrillUrl) =>
        WorklistRenderer.Render("Today", "sub", rows, drillBaseUrl);

    [Fact]
    public void Rows_Are_Rendered_With_Severity_Class()
    {
        var html = Render(new[]
        {
            Row(id: "p1", name: "Critical Patient", severity: "critical", flag: "Sepsis criteria"),
            Row(id: "p2", name: "Warning Patient", severity: "warning"),
            Row(id: "p3", name: "Info Patient", severity: "info"),
            Row(id: "p4", name: "Clean Patient", severity: null, flag: null, alertCount: 0),
        });

        html.Should().Contain("class=\"row critical\"");
        html.Should().Contain("class=\"row warning\"");
        html.Should().Contain("class=\"row info\"");
        html.Should().Contain("class=\"row clean\"");
    }

    [Fact]
    public void Drill_Link_Includes_PatientId()
    {
        var html = Render(new[] { Row(id: "annie-smith") });
        html.Should().Contain("href=\"/app/patient/annie-smith\"");
    }

    [Fact]
    public void Drill_Link_Url_Escapes_PatientId_To_Prevent_Path_Injection()
    {
        // PatientId is a URL path segment. WebUtility.HtmlEncode does not
        // escape '/', '?', or '#' — only Uri.EscapeDataString does. A
        // regression that reverts the encoder would silently let a
        // misshapen id inject extra path segments / a query string into
        // the drill href.
        var html = Render(new[] { Row(id: "evil/../other?x=1") });
        html.Should().Contain("href=\"/app/patient/evil%2F..%2Fother%3Fx%3D1\"");
        html.Should().NotContain("href=\"/app/patient/evil/",
            because: "the slashes in the id must be percent-encoded into the href");
    }

    [Fact]
    public void Summary_Counts_Patients_Need_Attention_And_Clean()
    {
        var html = Render(new[]
        {
            Row(severity: "warning", alertCount: 2),
            Row(severity: "warning", alertCount: 1),
            Row(severity: null, flag: null, alertCount: 0),
            Row(severity: null, flag: null, alertCount: 0),
            Row(severity: null, flag: null, alertCount: 0),
        });

        html.Should().MatchRegex("<div class=\"summary-num\">5</div>\\s*<div class=\"summary-label\">Patients today</div>");
        html.Should().MatchRegex("<div class=\"summary-num\">2</div>\\s*<div class=\"summary-label\">Need attention</div>");
        html.Should().MatchRegex("<div class=\"summary-num\">3</div>\\s*<div class=\"summary-label\">Clean charts</div>");
    }

    [Fact]
    public void Empty_State_When_No_Rows()
    {
        var html = Render(Array.Empty<WorklistRow>());
        html.Should().Contain("No patients on today's schedule");
    }

    [Fact]
    public void Patient_Name_Is_Html_Encoded()
    {
        var html = Render(new[] { Row(name: "<script>alert('xss')</script>") });
        html.Should().NotContain("<script>alert");
        html.Should().Contain("&lt;script&gt;");
    }

    [Fact]
    public void Worklist_Does_Not_Fabricate_Appointment_Times()
    {
        // Cerner's open sandbox does not support Appointment search;
        // the worklist must not invent slot times. No "time" cell, no
        // placeholder, no '— ' filler.
        var html = Render(new[] { Row() });
        html.Should().NotContain("class=\"row-time\"");
        html.Should().NotContain("class=\"time placeholder\"");
        html.Should().NotContain("class=\"time\">");
    }

    [Fact]
    public void Clean_Row_Renders_Clean_Flag_And_Green_Stripe_Class()
    {
        var html = Render(new[] { Row(severity: null, flag: null, alertCount: 0) });
        html.Should().Contain("clean-flag");
        html.Should().Contain("Clean");
    }

    [Fact]
    public void Multi_Alert_Row_Says_Alerts_Plural()
    {
        var html = Render(new[] { Row(alertCount: 3, severity: "warning") });
        html.Should().Contain("3 alerts");
    }

    [Fact]
    public void Single_Alert_Row_Says_Alert_Singular()
    {
        var html = Render(new[] { Row(alertCount: 1, severity: "warning") });
        html.Should().MatchRegex(@"\b1 alert\b(?!s)");
    }
}
