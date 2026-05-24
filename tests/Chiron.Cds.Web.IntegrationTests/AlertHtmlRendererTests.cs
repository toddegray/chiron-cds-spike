using Chiron.Cds.Web.CdsHooks.Models;
using Chiron.Cds.Web.SmartLaunch;
using FluentAssertions;

namespace Chiron.Cds.Web.IntegrationTests;

/// <summary>
/// Unit tests for <see cref="AlertHtmlRenderer"/>. The XSS-prevention test
/// is the load-bearing one: FHIR-derived strings flow into the card detail
/// as raw markdown and Markdig must not render embedded HTML.
/// </summary>
public class AlertHtmlRendererTests
{
    private static CdsCard MakeCard(string detail, string summary = "Test summary", string indicator = "warning") => new(
        Summary: summary,
        Indicator: indicator,
        Source: new CdsCardSource(Label: "Test source", Url: "https://example.test/source"),
        Detail: detail,
        Uuid: "abc123def456",
        OverrideReasons: new[]
        {
            new CdsCoding(Code: "bleeding_risk", System: "https://chiron.health/cds/override-reasons", Display: "Bleeding Risk"),
        });

    [Fact]
    public void Markdig_Does_Not_Pass_Through_Raw_Html_From_Detail()
    {
        // Simulates a hostile FHIR Observation whose Quantity.unit field
        // contains a <script> tag — that text flows into the alert detail
        // via FhirToFactMapper → Fact.Unit → AlertToCdsCardMapper.
        var detail = "**egfr_ckd_epi** = `27.34` mg/dL<script>alert('xss')</script>";
        var html = AlertHtmlRenderer.Render(
            heading: "Test",
            subline: "Test subline",
            cards: new[] { MakeCard(detail) });

        html.Should().NotContain("<script>",
            because: "Markdig with DisableHtml() must escape inline HTML in markdown source");
        html.Should().Contain("&lt;script&gt;",
            because: "The literal text should be visible to the user, just rendered as text");
    }

    [Fact]
    public void Block_Level_Html_In_Detail_Is_Also_Escaped()
    {
        var detail = "<iframe src=\"https://evil.test\"></iframe>\n\nSome **markdown** below.";
        var html = AlertHtmlRenderer.Render("Test", "sub", new[] { MakeCard(detail) });
        html.Should().NotContain("<iframe",
            because: "Block-level HTML is treated as literal text under DisableHtml()");
    }

    [Fact]
    public void Card_Summary_Is_Html_Encoded()
    {
        var html = AlertHtmlRenderer.Render(
            heading: "Test",
            subline: "sub",
            cards: new[] { MakeCard(detail: "ok", summary: "Summary with <b>evil</b>") });
        html.Should().NotContain("<b>evil</b>");
        html.Should().Contain("Summary with &lt;b&gt;evil&lt;/b&gt;");
    }

    [Fact]
    public void Renders_Card_Indicator_As_Severity_Class_And_Badge()
    {
        var html = AlertHtmlRenderer.Render("Test", "sub", new[] { MakeCard("d", indicator: "critical") });
        html.Should().Contain("card critical");
        html.Should().Contain("badge critical");
        html.Should().Contain("CRITICAL");
    }

    [Fact]
    public void Renders_Fingerprint_Block()
    {
        var html = AlertHtmlRenderer.Render("Test", "sub", new[] { MakeCard("d") });
        html.Should().Contain("abc123def456");
        html.Should().Contain("Fingerprint",
            because: "the fingerprint label appears in the per-card fingerprint chip");
    }

    [Fact]
    public void Renders_Override_Reasons()
    {
        var html = AlertHtmlRenderer.Render("Test", "sub", new[] { MakeCard("d") });
        html.Should().Contain("Bleeding Risk");
        html.Should().Contain("bleeding_risk",
            because: "the override-reason code is rendered as a code element");
    }

    [Fact]
    public void Renders_Banner_When_Provided()
    {
        var html = AlertHtmlRenderer.Render(
            heading: "Test",
            subline: "sub",
            cards: Array.Empty<CdsCard>(),
            banner: "Demo mode notice");
        html.Should().Contain("Demo mode notice");
        html.Should().Contain("class=\"banner\"");
    }

    [Fact]
    public void Renders_Nav_Bar_When_Provided()
    {
        var html = AlertHtmlRenderer.Render(
            heading: "Test",
            subline: "sub",
            cards: Array.Empty<CdsCard>(),
            navBar: "<a href=\"/\">home</a>");
        html.Should().Contain("class=\"navbar\"");
        html.Should().Contain("<a href=\"/\">home</a>");
    }

    [Fact]
    public void Renders_Patient_Header_When_Provided()
    {
        var patient = new PatientHeader(
            DisplayName: "SMITH, ANNIE",
            AgeSex: "35y · Female",
            ActiveConditions: new[] { "Diabetes", "Hypertension" },
            ActiveAllergies: new[] { "Sulfa" },
            ActiveMedicationCount: 3,
            CompletedImmunizationCount: 5,
            CompletedProcedureCount: 2);

        var html = AlertHtmlRenderer.Render(
            heading: "Test",
            subline: "sub",
            cards: Array.Empty<CdsCard>(),
            patient: patient);

        html.Should().Contain("SMITH, ANNIE");
        html.Should().Contain("35y");
        html.Should().Contain("Female");
        html.Should().Contain("Diabetes");
        html.Should().Contain("Hypertension");
        html.Should().Contain("Sulfa");
        html.Should().Contain("allergy-chips",
            because: "allergies render with the distinctive critical-tone chip class");

        // Asserting the numeric stats appear in their respective stat-num blocks
        // catches a mutation that swaps value/label or reorders the stats.
        html.Should().MatchRegex("<div class=\"stat-num\">3</div>\\s*<div class=\"stat-label\">Active meds</div>");
        html.Should().MatchRegex("<div class=\"stat-num\">5</div>\\s*<div class=\"stat-label\">Immunizations</div>");
        html.Should().MatchRegex("<div class=\"stat-num\">2</div>\\s*<div class=\"stat-label\">Procedures</div>");
        html.Should().MatchRegex("<div class=\"stat-num\">2</div>\\s*<div class=\"stat-label\">Active conditions</div>");
        html.Should().MatchRegex("<div class=\"stat-num\">1</div>\\s*<div class=\"stat-label\">Allergies</div>");
    }

    [Fact]
    public void Renders_Empty_State_When_No_Cards()
    {
        var html = AlertHtmlRenderer.Render(
            heading: "Test",
            subline: "sub",
            cards: Array.Empty<CdsCard>());
        html.Should().Contain("Nothing needs your attention");
    }

    [Fact]
    public void Patient_Header_Is_Html_Encoded()
    {
        var hostile = new PatientHeader(
            DisplayName: "<script>alert(1)</script>",
            AgeSex: "x",
            ActiveConditions: new[] { "<b>evil</b>" },
            ActiveAllergies: Array.Empty<string>(),
            ActiveMedicationCount: 0,
            CompletedImmunizationCount: 0,
            CompletedProcedureCount: 0);
        var html = AlertHtmlRenderer.Render("Test", "sub", Array.Empty<CdsCard>(), patient: hostile);
        html.Should().NotContain("<script>alert");
        html.Should().Contain("&lt;script&gt;");
        html.Should().NotContain("<b>evil</b>");
    }

    [Fact]
    public void Markdown_Formatting_Still_Works_When_Html_Is_Disabled()
    {
        var detail = "**bold** and `code` and a [link](https://example.test/path).";
        var html = AlertHtmlRenderer.Render("Test", "sub", new[] { MakeCard(detail) });
        html.Should().Contain("<strong>bold</strong>");
        html.Should().Contain("<code>code</code>");
        html.Should().Contain("href=\"https://example.test/path\"");
    }
}
