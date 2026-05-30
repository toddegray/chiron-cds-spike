using Chiron.Cds.Engine.Primitives;
using Chiron.Cds.Web.CdsHooks.Models;
using Chiron.Cds.Web.Mappers;
using Chiron.Cds.Web.Panel;
using FluentAssertions;

namespace Chiron.Cds.Web.IntegrationTests;

/// <summary>
/// Unit tests for <see cref="EhrChartRenderer"/> — the clinician-facing EHR
/// Visit Brief. Pure function: feed engine inputs + CDS cards, assert the HTML
/// carries the patient banner, the problem/med/allergy/lab panels, the
/// decision-support cards, the empty states, and safe encoding.
/// </summary>
public class EhrChartRendererTests
{
    private static EngineInputs Inputs(
        IEnumerable<Condition>? conditions = null,
        IEnumerable<Medication>? medications = null,
        IEnumerable<Lab>? labs = null,
        IEnumerable<Allergy>? allergies = null) => new(
        Patient: new Patient("p", 48, "F"),
        Medications: (medications ?? Array.Empty<Medication>()).ToArray(),
        Labs: (labs ?? Array.Empty<Lab>()).ToArray(),
        Conditions: (conditions ?? Array.Empty<Condition>()).ToArray(),
        Allergies: (allergies ?? Array.Empty<Allergy>()).ToArray(),
        Immunizations: Array.Empty<Immunization>(),
        Procedures: Array.Empty<Procedure>());

    private static CdsCard Card(string summary, string indicator = "info") =>
        new(summary, indicator, new CdsCardSource("Clinical Reasoning"), Detail: "- supporting reason");

    private static string Render(
        EngineInputs inputs, IReadOnlyList<CdsCard> cards,
        string name = "Camila Maria Lopez", string mrn = "203713", string id = "erXuFYUfucBZaryVksYEcMg3") =>
        EhrChartRenderer.Render(id, name, "48y · Female", "1987-09-12", mrn, inputs, cards);

    [Fact]
    public void Renders_Patient_Identity_Banner()
    {
        var html = Render(Inputs(), Array.Empty<CdsCard>());
        html.Should().Contain("class=\"topbar\"");
        html.Should().Contain("Camila Maria Lopez");
        html.Should().Contain("MRN 203713");
        html.Should().Contain("DOB 1987-09-12");
        html.Should().Contain("class=\"icon-rail\"");
        html.Should().Contain("class=\"tab active\"", because: "the Summary tab is the active chart section");
    }

    [Fact]
    public void Renders_Problem_List_Humanized()
    {
        var html = Render(Inputs(conditions: new[] { new Condition("type_2_diabetes_mellitus") }), Array.Empty<CdsCard>());
        html.Should().Contain("Problem List");
        html.Should().Contain("Type 2 diabetes mellitus");
    }

    [Fact]
    public void Medications_Render_With_Add_Order_Link()
    {
        var html = Render(Inputs(medications: new[] { new Medication("lisinopril") }), Array.Empty<CdsCard>());
        html.Should().Contain("Lisinopril");
        html.Should().Contain("/app/patient/erXuFYUfucBZaryVksYEcMg3/orders");
    }

    [Fact]
    public void Allergies_Render()
    {
        var html = Render(Inputs(allergies: new[] { new Allergy("penicillin") }), Array.Empty<CdsCard>());
        html.Should().Contain("Allergies");
        html.Should().Contain("Penicillin");
    }

    [Fact]
    public void Key_Labs_Render_Value_With_Display_Label()
    {
        var html = Render(Inputs(labs: new[] { new Lab("hemoglobin_a1c", 7.8, "%") }), Array.Empty<CdsCard>());
        html.Should().Contain("Hemoglobin A1c");
        html.Should().Contain("7.8");
    }

    [Fact]
    public void Decision_Support_Card_Renders_With_Severity_And_Action()
    {
        var html = Render(Inputs(), new[] { Card("Hyperkalemia risk", "warning") });
        html.Should().Contain("Clinical Decision Support");
        html.Should().Contain("Hyperkalemia risk");
        html.Should().Contain("cds-card warning", because: "the card carries its severity class for styling");
        html.Should().Contain("Add order");
    }

    [Fact]
    public void Renders_Empty_States_When_Chart_Is_Bare()
    {
        var html = Render(Inputs(), Array.Empty<CdsCard>());
        html.Should().Contain("No active problems");
        html.Should().Contain("No active medications");
        html.Should().Contain("No known allergies");
        html.Should().Contain("No open recommendations");
    }

    [Fact]
    public void Html_Encodes_Name_And_Problem_Text_To_Prevent_Xss()
    {
        var html = Render(
            Inputs(conditions: new[] { new Condition("<script>evil") }),
            Array.Empty<CdsCard>(),
            name: "<img src=x onerror=alert(1)>");
        html.Should().NotContain("<img src=x onerror=alert(1)>");
        html.Should().NotContain("<script>evil");
        html.Should().Contain("&lt;");
    }

    [Fact]
    public void Url_Escapes_Patient_Id_In_Tab_And_Order_Links()
    {
        var html = Render(Inputs(medications: new[] { new Medication("metformin") }), Array.Empty<CdsCard>(), id: "a/b?c");
        html.Should().Contain("/app/patient/a%2Fb%3Fc/results", because: "the id is a path segment and must be percent-encoded");
        html.Should().NotContain("/app/patient/a/b?c");
    }

    [Theory]
    [InlineData("critical", "cds-card critical")]
    [InlineData("warning", "cds-card warning")]
    [InlineData("info", "cds-card info")]
    public void Cds_Card_Class_Reflects_Severity(string indicator, string expectedClass)
    {
        var html = Render(Inputs(), new[] { Card("Alert text", indicator) });
        html.Should().Contain(expectedClass);
    }

    [Fact]
    public void Key_Lab_Renders_Taken_Date_When_Present()
    {
        var when = new DateTimeOffset(2019, 5, 28, 0, 0, 0, TimeSpan.Zero);
        var html = Render(Inputs(labs: new[] { new Lab("inr", 1.1, "", when) }), Array.Empty<CdsCard>());
        html.Should().Contain("05/28/2019", because: "a lab with a collection date shows it");
    }

    [Fact]
    public void Key_Lab_Omits_Unit_When_Absent()
    {
        var html = Render(Inputs(labs: new[] { new Lab("inr", 1.1, null) }), Array.Empty<CdsCard>());
        html.Should().Contain("1.1");
        html.Should().NotContain("<span class=\"lab-unit\">", because: "no unit means no unit span");
    }

    [Fact]
    public void Lab_Value_Formats_To_Two_Decimals()
    {
        var html = Render(Inputs(labs: new[] { new Lab("creatinine", 1.234, "mg/dL") }), Array.Empty<CdsCard>());
        html.Should().Contain("1.23");
        html.Should().NotContain("1.234", because: "the value is formatted to at most two decimals");
    }

    [Fact]
    public void Key_Lab_Falls_Back_To_Humanized_Name_When_Not_In_Label_Map()
    {
        var html = Render(Inputs(labs: new[] { new Lab("serum_potassium", 5.4, "mmol/L") }), Array.Empty<CdsCard>());
        html.Should().Contain("Serum potassium", because: "an unmapped lab name humanizes its canonical form");
        html.Should().Contain("5.4");
    }

    [Fact]
    public void Inactive_Entities_Are_Filtered_Out()
    {
        var html = Render(
            Inputs(
                conditions: new[] { new Condition("resolved_problem", Active: false) },
                medications: new[] { new Medication("discontinued_med", Active: false) },
                allergies: new[] { new Allergy("inactive_allergy", Active: false) }),
            Array.Empty<CdsCard>());
        html.Should().NotContain("Resolved problem");
        html.Should().NotContain("Discontinued med");
        html.Should().NotContain("Inactive allergy");
        html.Should().Contain("No active problems",
            because: "filtering out the inactive entries leaves the panels empty");
    }

    [Fact]
    public void Duplicate_Problems_Are_Deduplicated()
    {
        var html = Render(
            Inputs(conditions: new[] { new Condition("hypertension"), new Condition("hypertension") }),
            Array.Empty<CdsCard>());
        System.Text.RegularExpressions.Regex.Matches(html, "Hypertension").Count.Should().Be(1,
            because: "the same problem listed twice renders a single row");
    }

    [Fact]
    public void Card_Without_Detail_Renders_No_Detail_Block()
    {
        var card = new CdsCard("Brief alert", "info", new CdsCardSource("CDS"));
        var html = Render(Inputs(), new[] { card });
        html.Should().Contain("Brief alert");
        html.Should().NotContain("<div class=\"cds-detail\">");
    }

    [Fact]
    public void Card_With_Empty_Source_Label_Omits_Source()
    {
        var card = new CdsCard("Alert", "info", new CdsCardSource(""), Detail: null);
        var html = Render(Inputs(), new[] { card });
        html.Should().NotContain("<span class=\"cds-source\">");
    }

    [Fact]
    public void Key_Lab_With_Multiple_Readings_Is_Expandable_With_History()
    {
        var labs = new[]
        {
            new Lab("systolic_bp", 155, "mm[Hg]", new DateTimeOffset(2026, 5, 29, 0, 0, 0, TimeSpan.Zero)),
            new Lab("systolic_bp", 120, "mm[Hg]", new DateTimeOffset(2019, 5, 28, 0, 0, 0, TimeSpan.Zero)),
            new Lab("systolic_bp", 118, "mm[Hg]", TakenAt: null),
        };
        var html = Render(Inputs(labs: labs), Array.Empty<CdsCard>());
        html.Should().Contain("<details class=\"lab-detail\">", because: "a lab with history is expandable");
        html.Should().Contain("lab-history", because: "the expanded view tabulates every reading");
        html.Should().Contain("3 readings");
        html.Should().Contain("155", because: "the headline shows the most recent value");
        html.Should().Contain("05/28/2019", because: "the older reading appears in the history table");
        html.Should().Contain("<td class=\"lh-date\">—</td>",
            because: "a reading with no date renders a placeholder in the history cell (not the title's em-dash)");
    }

    [Fact]
    public void Single_Reading_Lab_Is_Not_Expandable()
    {
        var html = Render(Inputs(labs: new[] { new Lab("inr", 1.1, null) }), Array.Empty<CdsCard>());
        html.Should().NotContain("<details class=\"lab-detail\">");
    }

    [Fact]
    public void Problem_List_Shows_Recorded_Date_And_Orders_Most_Recent_First()
    {
        var older = new Condition("hypertension", Active: true, RecordedDate: new DateTimeOffset(2019, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var newer = new Condition("chest_pain", Active: true, RecordedDate: new DateTimeOffset(2023, 6, 2, 0, 0, 0, TimeSpan.Zero));
        var html = Render(Inputs(conditions: new[] { older, newer }), Array.Empty<CdsCard>());
        html.Should().Contain("Recorded Jun 2023");
        html.Should().Contain("Recorded Jan 2019");
        html.IndexOf("Chest pain", StringComparison.Ordinal).Should().BeLessThan(
            html.IndexOf("Hypertension", StringComparison.Ordinal),
            because: "the more recently recorded problem sorts first");
    }

    [Fact]
    public void Problem_With_Onset_Shows_Onset_Year()
    {
        var pcos = new Condition("pcos", Onset: new DateTimeOffset(2005, 9, 20, 0, 0, 0, TimeSpan.Zero), Active: true);
        var html = Render(Inputs(conditions: new[] { pcos }), Array.Empty<CdsCard>());
        html.Should().Contain("Onset 2005", because: "onset is preferred over recorded date when present");
    }
}
