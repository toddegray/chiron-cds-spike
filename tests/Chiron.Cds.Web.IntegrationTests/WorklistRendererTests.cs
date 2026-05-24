using Chiron.Cds.Web.SmartLaunch;
using FluentAssertions;

namespace Chiron.Cds.Web.IntegrationTests;

public class WorklistRendererTests
{
    private static WorklistRow Row(
        string id = "p1",
        string name = "Test Patient",
        string ageSex = "55y · Female",
        string? time = "8:30 AM",
        string? complaint = "Annual exam",
        string? flag = "Mammography overdue",
        string? severity = "info",
        int alertCount = 1) => new(id, name, ageSex, time, complaint, flag, severity, alertCount);

    [Fact]
    public void Rows_Are_Rendered_With_Severity_Class()
    {
        var html = WorklistRenderer.Render("Today", "sub", new[]
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
        var html = WorklistRenderer.Render("Today", "sub",
            new[] { Row(id: "annie-smith") },
            drillBaseUrl: "/app/demo");
        html.Should().Contain("href=\"/app/demo/annie-smith\"");
    }

    [Fact]
    public void Summary_Counts_Patients_Need_Attention_And_Clean()
    {
        var html = WorklistRenderer.Render("Today", "sub", new[]
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
        var html = WorklistRenderer.Render("Today", "sub", Array.Empty<WorklistRow>());
        html.Should().Contain("No patients on today's schedule");
    }

    [Fact]
    public void Patient_Name_And_Chief_Complaint_Are_Html_Encoded()
    {
        var html = WorklistRenderer.Render("Today", "sub", new[]
        {
            Row(name: "<script>alert('xss')</script>", complaint: "<img src=x onerror=alert(1)>"),
        });
        html.Should().NotContain("<script>alert");
        html.Should().NotContain("<img src=x onerror");
        html.Should().Contain("&lt;script&gt;");
    }

    [Fact]
    public void Time_Placeholder_Renders_When_No_Appointment_Time()
    {
        var html = WorklistRenderer.Render("Today", "sub", new[] { Row(time: null) });
        html.Should().Contain("class=\"time placeholder\"");
    }

    [Fact]
    public void Clean_Row_Renders_Clean_Flag_And_Green_Stripe_Class()
    {
        var html = WorklistRenderer.Render("Today", "sub", new[]
        {
            Row(severity: null, flag: null, alertCount: 0),
        });
        html.Should().Contain("clean-flag");
        html.Should().Contain("Clean");
    }

    [Fact]
    public void Multi_Alert_Row_Says_Alerts_Plural()
    {
        var html = WorklistRenderer.Render("Today", "sub", new[]
        {
            Row(alertCount: 3, severity: "warning"),
        });
        html.Should().Contain("3 alerts");
    }

    [Fact]
    public void Single_Alert_Row_Says_Alert_Singular()
    {
        var html = WorklistRenderer.Render("Today", "sub", new[]
        {
            Row(alertCount: 1, severity: "warning"),
        });
        html.Should().MatchRegex(@"\b1 alert\b(?!s)");
    }
}
