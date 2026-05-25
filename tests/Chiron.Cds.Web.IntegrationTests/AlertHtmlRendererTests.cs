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
    public void Fingerprint_Is_Not_Rendered_In_The_Visit_Brief()
    {
        // Fingerprints are dev-toolbox: they identify alerts in the
        // override log and the CDS Hooks JSON (`card.uuid`), but a
        // clinician never types or reads one. They must not appear in
        // the rendered visit brief at all.
        var html = AlertHtmlRenderer.Render("Test", "sub", new[] { MakeCard("d") });
        html.Should().NotContain("abc123def456",
            because: "the SHA-style alert uuid must not leak into the clinician-facing HTML");
        html.Should().NotContain("Audit fingerprint",
            because: "the prior 'Audit fingerprint' footer was also clinician-irrelevant");
        html.Should().NotContain("class=\"fingerprint\"",
            because: "the original prominent fingerprint chip is gone too");
        html.Should().NotContain("class=\"derivation-fp\"",
            because: "the in-derivation fingerprint footer is gone");
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
            CompletedProcedureCount: 2,
            DateOfBirth: "1990-08-01",
            Mrn: "12674028");

        var html = AlertHtmlRenderer.Render(
            heading: "SMITH, ANNIE",
            // Empty subline — the patient page lets the demographics row
            // carry context, not a narrative "this is your chart" line.
            subline: string.Empty,
            cards: Array.Empty<CdsCard>(),
            patient: patient);

        html.Should().Contain("<h1>SMITH, ANNIE</h1>",
            because: "the patient name is the page-header h1, not duplicated in the rail");
        html.Should().Contain("class=\"demographics\"",
            because: "the demographics row renders structurally beneath the h1");
        // HtmlEncode emits the U+00B7 middle dot as the numeric entity, so
        // assert on what the encoder actually produces ("35y &#183; Female").
        html.Should().Contain("35y &#183; Female");
        html.Should().Contain("Born 1990-08-01");
        html.Should().Contain("MRN 12674028");
        html.Should().Contain("Diabetes");
        html.Should().Contain("Hypertension");
        html.Should().Contain("Sulfa");
        html.Should().Contain("allergy-chips",
            because: "allergies render with the distinctive critical-tone chip class");
        html.Should().NotContain("class=\"patient-hero\"",
            because: "the old name+age-sex rail block duplicated the page-header banner and is gone");
        html.Should().NotContain("class=\"patient-name\"",
            because: "the name now lives only in the page-header h1");

        // Asserting the numeric stats appear in their respective stat-num blocks
        // catches a mutation that swaps value/label or reorders the stats.
        html.Should().MatchRegex("<div class=\"stat-num\">3</div>\\s*<div class=\"stat-label\">Medications</div>");
        html.Should().MatchRegex("<div class=\"stat-num\">5</div>\\s*<div class=\"stat-label\">Immunizations</div>");
        html.Should().MatchRegex("<div class=\"stat-num\">2</div>\\s*<div class=\"stat-label\">Procedures</div>");
        html.Should().MatchRegex("<div class=\"stat-num\">2</div>\\s*<div class=\"stat-label\">Conditions</div>");
        html.Should().MatchRegex("<div class=\"stat-num\">1</div>\\s*<div class=\"stat-label\">Allergy</div>",
            because: "single-allergy case uses the singular label");
    }

    [Theory]
    [InlineData(0, "Allergies")]
    [InlineData(2, "Allergies")]
    [InlineData(7, "Allergies")]
    public void Allergy_Stat_Label_Is_Plural_When_Count_Is_Not_One(int count, string expectedLabel)
    {
        // Pins the false branch of the singular/plural ternary on
        // AlertHtmlRenderer's allergy stat — without this a mutation
        // changing "Allergies" to "Allergy" (or flipping the predicate
        // to `>= 1`) would slip through.
        var patient = new PatientHeader(
            DisplayName: "Test",
            AgeSex: "40y · Female",
            ActiveConditions: Array.Empty<string>(),
            ActiveAllergies: Enumerable.Range(0, count).Select(i => $"allergen{i}").ToArray(),
            ActiveMedicationCount: 0,
            CompletedImmunizationCount: 0,
            CompletedProcedureCount: 0);

        var html = AlertHtmlRenderer.Render(
            heading: "Test", subline: "sub", cards: Array.Empty<CdsCard>(), patient: patient);

        html.Should().MatchRegex(
            $"<div class=\"stat-num\">{count}</div>\\s*<div class=\"stat-label\">{expectedLabel}</div>");
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
        // The demographics row + condition chips are the only patient-data
        // surfaces the renderer paints now (the rail no longer carries the
        // patient.DisplayName). Inject hostile values into the live fields
        // and assert nothing escapes the encoder.
        var hostile = new PatientHeader(
            DisplayName: "ignored-by-renderer",
            AgeSex: "<script>alert('age')</script>",
            ActiveConditions: new[] { "<b>evil</b>" },
            ActiveAllergies: Array.Empty<string>(),
            ActiveMedicationCount: 0,
            CompletedImmunizationCount: 0,
            CompletedProcedureCount: 0,
            DateOfBirth: "<img src=x>",
            Mrn: "<svg onload=alert(1)>");
        var html = AlertHtmlRenderer.Render("Test", "sub", Array.Empty<CdsCard>(), patient: hostile);
        html.Should().NotContain("<script>alert");
        html.Should().NotContain("<img src=x>");
        html.Should().NotContain("<svg onload=alert");
        html.Should().NotContain("<b>evil</b>");
        html.Should().Contain("&lt;script&gt;",
            because: "AgeSex is rendered into the demographics row, must be HTML-encoded");
        html.Should().Contain("&lt;img",
            because: "DateOfBirth flows into the demographics row");
        html.Should().Contain("&lt;svg",
            because: "MRN flows into the demographics row");
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

    [Fact]
    public void Empty_Subline_Suppresses_The_Subline_Element()
    {
        var html = AlertHtmlRenderer.Render(heading: "Test", subline: "", cards: Array.Empty<CdsCard>());
        html.Should().NotContain("class=\"subline\"",
            because: "an empty subline must not paint an empty <p class='subline'> element");
    }

    [Fact]
    public void No_Patient_Fallback_Renders_When_Patient_Is_Null()
    {
        var html = AlertHtmlRenderer.Render("Test", "sub", Array.Empty<CdsCard>(), patient: null);
        html.Should().Contain("class=\"no-patient\"");
        html.Should().Contain("No patient context",
            because: "the fallback message tells viewers the page is rendering without a patient");
    }

    [Fact]
    public void Demographics_Row_Includes_Only_The_Fields_That_Are_Set()
    {
        // AgeSex-only header — no DateOfBirth, no MRN. Confirms each
        // conditional in RenderDemographics actually short-circuits when
        // the source field is empty.
        var partial = new PatientHeader(
            DisplayName: "irrelevant",
            AgeSex: "78y · Male",
            ActiveConditions: Array.Empty<string>(),
            ActiveAllergies: Array.Empty<string>(),
            ActiveMedicationCount: 0,
            CompletedImmunizationCount: 0,
            CompletedProcedureCount: 0);

        var html = AlertHtmlRenderer.Render("Test", "sub", Array.Empty<CdsCard>(), patient: partial);
        html.Should().Contain("78y &#183; Male");
        // Assert against the rendered demographics fragment, not the whole
        // document, so CSS-comment substrings ("MRN", "Born") don't fool
        // the NotContain checks.
        var fragment = ExtractDemographicsFragment(html);
        fragment.Should().NotContain("Born",
            because: "no DateOfBirth on the header → no 'Born …' segment");
        fragment.Should().NotContain("MRN",
            because: "no Mrn on the header → no 'MRN …' segment");
        fragment.Should().NotContain("demo-sep",
            because: "with only one demographic part, no middle-dot separator should render");
    }

    private static string ExtractDemographicsFragment(string html)
    {
        const string open = "<div class=\"demographics\">";
        const string close = "</div>";
        var start = html.IndexOf(open, StringComparison.Ordinal);
        if (start < 0) return string.Empty;
        var end = html.IndexOf(close, start + open.Length, StringComparison.Ordinal);
        return end < 0 ? html[start..] : html[start..(end + close.Length)];
    }
}
