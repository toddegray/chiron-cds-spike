using Chiron.Cds.Web.Panel;
using Chiron.Cds.Web.SmartLaunch;
using FluentAssertions;

namespace Chiron.Cds.Web.IntegrationTests;

/// <summary>
/// Pure-function tests for <see cref="ResultReviewRenderer"/>: header
/// shape, empty states, trending cards, sparkline emission, XSS guards.
/// </summary>
public class ResultReviewRendererTests
{
    private const string NavBar = "<span class=\"brand\">Chiron</span>";

    private static ResultReviewData Empty(string id = "p1") => new(
        Demographics: new PatientDemographics("Smith, Jane", "35y · Female", "1990-01-15", id),
        Reports: Array.Empty<ReportSummary>(),
        Trends: Array.Empty<LabTrend>(),
        Error: null);

    [Fact]
    public void Renders_Header_With_Name_And_Demographics()
    {
        var html = ResultReviewRenderer.Render(Empty(), NavBar);
        html.Should().Contain("<h1>Smith, Jane</h1>");
        html.Should().Contain("35y &#183; Female");
        html.Should().Contain("Born 1990-01-15");
        html.Should().Contain("MRN p1");
    }

    [Fact]
    public void Renders_Both_Empty_States_When_Patient_Has_No_Labs_Or_Reports()
    {
        var html = ResultReviewRenderer.Render(Empty(), NavBar);
        html.Should().Contain("No lab observations on file");
        html.Should().Contain("No diagnostic reports on file");
    }

    [Fact]
    public void Error_Banner_Replaces_Both_Sections_When_Fetch_Failed()
    {
        var failed = ResultReviewData.Failure("p1", "FHIR 403 Forbidden");
        var html = ResultReviewRenderer.Render(failed, NavBar);
        html.Should().Contain("class=\"banner\"");
        html.Should().Contain("Chart results could not be loaded");
        html.Should().Contain("FHIR 403 Forbidden");
        html.Should().NotContain("Lab trends",
            because: "the sections are suppressed in the error state so we don't pretend partial data was returned");
    }

    [Fact]
    public void Renders_Trend_Card_With_Latest_Value_Unit_And_History()
    {
        var data = Empty() with
        {
            Trends = new[]
            {
                new LabTrend(
                    Key: "loinc:2345-7",
                    Loinc: "2345-7",
                    Title: "Glucose Level",
                    Unit: "mmol/L",
                    Points: new[]
                    {
                        new TrendPoint(DateTimeOffset.Parse("2026-05-21T00:00:00Z"), "6", "mmol/L", false),
                        new TrendPoint(DateTimeOffset.Parse("2024-05-21T00:00:00Z"), "5", "mmol/L", false),
                        new TrendPoint(DateTimeOffset.Parse("2023-05-21T00:00:00Z"), "5.2", "mmol/L", false),
                    }),
            },
        };
        var html = ResultReviewRenderer.Render(data, NavBar);
        html.Should().Contain("Glucose Level");
        html.Should().Contain("LOINC 2345-7");
        html.Should().Contain("class=\"trend-value\">6");
        html.Should().Contain("class=\"trend-unit\">mmol/L");
        html.Should().Contain("2026-05-21",
            because: "the date of the latest value renders under the hero number");
        html.Should().Contain("<svg class=\"sparkline\"",
            because: "the sparkline renders when the trend has more than one numeric point");
    }

    [Fact]
    public void Abnormal_Latest_Value_Renders_Abnormal_Pill_And_Class()
    {
        var data = Empty() with
        {
            Trends = new[]
            {
                new LabTrend("loinc:2160-0", "2160-0", "Creatinine", "mg/dL",
                    new[] { new TrendPoint(DateTimeOffset.UtcNow, "2.4", "mg/dL", IsAbnormal: true) }),
            },
        };
        var html = ResultReviewRenderer.Render(data, NavBar);
        html.Should().Contain("trend-card abnormal");
        html.Should().Contain("Abnormal");
    }

    [Fact]
    public void Sparkline_Is_Suppressed_When_Only_One_Point()
    {
        var data = Empty() with
        {
            Trends = new[]
            {
                new LabTrend("k", "k", "Solo", "u",
                    new[] { new TrendPoint(DateTimeOffset.UtcNow, "1", "u", false) }),
            },
        };
        var html = ResultReviewRenderer.Render(data, NavBar);
        html.Should().NotContain("<svg class=\"sparkline\"",
            because: "a single data point cannot form a meaningful sparkline");
    }

    [Fact]
    public void Report_Status_Becomes_A_Pill_Class()
    {
        var data = Empty() with
        {
            Reports = new[]
            {
                new ReportSummary("Lipid Panel", "Laboratory", "amended",
                    DateTimeOffset.Parse("2025-01-13T06:46:59Z"), Conclusion: "elevated LDL"),
                new ReportSummary("Radiology Reports", "Radiology", "final",
                    DateTimeOffset.Parse("2020-08-13T19:36:06Z"), Conclusion: null),
            },
        };
        var html = ResultReviewRenderer.Render(data, NavBar);
        html.Should().Contain("report-status status-amended");
        html.Should().Contain("report-status status-final");
        html.Should().Contain("Lipid Panel");
        html.Should().Contain("elevated LDL");
        html.Should().Contain("2025-01-13");
    }

    [Fact]
    public void Renderer_Html_Encodes_Hostile_FHIR_Strings()
    {
        var data = Empty() with
        {
            Trends = new[]
            {
                new LabTrend("k", "<svg/onload=alert(1)>", "<img src=x>", "<b>U</b>",
                    new[] { new TrendPoint(DateTimeOffset.UtcNow, "<script>", "u", false) }),
            },
            Reports = new[]
            {
                new ReportSummary("<img src=x>", "<svg onload=alert(2)>", "final",
                    DateTimeOffset.UtcNow, Conclusion: "<script>alert(3)</script>"),
            },
        };
        var html = ResultReviewRenderer.Render(data, NavBar);
        html.Should().NotContain("<img src=x>");
        html.Should().NotContain("<svg/onload=alert");
        html.Should().NotContain("<svg onload=alert");
        html.Should().NotContain("<script>");
        html.Should().Contain("&lt;img");
        html.Should().Contain("&lt;svg");
        html.Should().Contain("&lt;script&gt;");
    }

    [Fact]
    public void Trend_Card_Omits_Loinc_Pill_When_Loinc_Is_Null()
    {
        var data = Empty() with
        {
            Trends = new[]
            {
                new LabTrend("text:Glucose Level", Loinc: null, "Glucose Level", "mmol/L",
                    new[] { new TrendPoint(DateTimeOffset.UtcNow, "5", "mmol/L", false) }),
            },
        };
        var html = ResultReviewRenderer.Render(data, NavBar);
        html.Should().Contain("Glucose Level");
        html.Should().NotContain("LOINC",
            because: "the LOINC pill is suppressed when no LOINC code is on the trend");
    }

    [Fact]
    public void Trend_Card_Omits_Unit_Spans_When_Unit_Is_Empty()
    {
        var data = Empty() with
        {
            Trends = new[]
            {
                new LabTrend("k", "k", "Some Score", Unit: string.Empty, new[]
                {
                    new TrendPoint(DateTimeOffset.Parse("2026-05-21T00:00:00Z"), "42", "", false),
                    new TrendPoint(DateTimeOffset.Parse("2024-05-21T00:00:00Z"), "41", "", false),
                }),
            },
        };
        var html = ResultReviewRenderer.Render(data, NavBar);
        html.Should().NotContain("class=\"trend-unit\">",
            because: "the unit span is suppressed when the trend has no unit");
        // History rows should also omit a trailing unit space.
        html.Should().Contain("class=\"trend-row-value\">42</span>",
            because: "history rows render the bare value when no unit is present");
    }

    [Fact]
    public void Report_Omits_Category_Span_When_Category_Is_Null()
    {
        var data = Empty() with
        {
            Reports = new[]
            {
                new ReportSummary("Notes", Category: null, "final",
                    DateTimeOffset.Parse("2025-01-13T00:00:00Z"), Conclusion: null),
            },
        };
        var html = ResultReviewRenderer.Render(data, NavBar);
        html.Should().NotContain("class=\"report-category\"",
            because: "without a category, no category span renders");
    }

    [Fact]
    public void Report_Renders_Em_Dash_When_IssuedAt_Is_Null()
    {
        var data = Empty() with
        {
            Reports = new[]
            {
                new ReportSummary("Notes", "Category", "final", IssuedAt: null, Conclusion: null),
            },
        };
        var html = ResultReviewRenderer.Render(data, NavBar);
        html.Should().Contain("report-issued\">—</span>",
            because: "a missing issued timestamp renders as an em-dash, not as a fake date");
    }

    [Fact]
    public void Header_Includes_Chart_Tabs_When_Supplied()
    {
        var tabs = new[]
        {
            new ChartTab("Visit brief", "/app/patient/p1", IsActive: false),
            new ChartTab("Results", "/app/patient/p1/results", IsActive: true),
        };
        var html = ResultReviewRenderer.Render(Empty(), NavBar, tabs);
        html.Should().Contain("class=\"chart-tabs\"");
        html.Should().Contain("href=\"/app/patient/p1\"");
        html.Should().Contain("chart-tab active");
        html.Should().Contain("Results</a>");
    }
}
