using Chiron.Cds.Web.Panel;
using FluentAssertions;

namespace Chiron.Cds.Web.IntegrationTests;

/// <summary>
/// Pure unit tests for <see cref="PatientSearchRenderer"/>. The renderer is a
/// static function — render some hits, render an empty result, render an empty
/// form — and assert the HTML carries the expected fields, anchors, and
/// encoding.
/// </summary>
public class PatientSearchRendererTests
{
    private const string NavBar = "<span class=\"brand\">CDS</span>";

    private static PatientSearchCriteria Empty => new(null, null, null, null);
    private static PatientSearchCriteria Name(string name) => new(name, null, null, null);

    private static string Render(PatientSearchCriteria criteria, IReadOnlyList<PatientSearchHit> hits, string? warning = null) =>
        PatientSearchRenderer.Render(criteria, hits, warning, NavBar, "/app/patient");

    [Fact]
    public void Empty_Form_Renders_Hint_Not_Results()
    {
        var html = Render(Empty, Array.Empty<PatientSearchHit>());
        html.Should().Contain("Start typing",
            because: "the empty-form state guides the user instead of showing 'no results'");
        html.Should().NotContain("No patients matched",
            because: "the empty-form state must not show the empty-results message");
    }

    [Fact]
    public void Empty_Results_For_Criteria_Renders_Empty_State()
    {
        var html = Render(Name("xyzzy"), Array.Empty<PatientSearchHit>());
        html.Should().Contain("No patients matched");
    }

    [Fact]
    public void Renders_All_Four_Search_Fields()
    {
        var html = Render(Empty, Array.Empty<PatientSearchHit>());
        html.Should().Contain("name=\"mrn\"");
        html.Should().Contain("name=\"name\"");
        html.Should().Contain("name=\"dob\"");
        html.Should().Contain("name=\"encounter\"");
    }

    [Fact]
    public void Field_Values_Repopulate_From_Criteria()
    {
        var html = Render(new PatientSearchCriteria(Name: "Lopez", BirthDate: "1987-09-12", Mrn: "203713", EncounterId: null),
            Array.Empty<PatientSearchHit>());
        html.Should().Contain("value=\"203713\"");
        html.Should().Contain("value=\"Lopez\"");
        html.Should().Contain("value=\"1987-09-12\"");
    }

    [Fact]
    public void Hits_Render_As_Rows_Linked_To_Visit_Brief()
    {
        var hits = new[]
        {
            new PatientSearchHit(PatientId: "111", DisplayName: "Smith, Jane", Gender: "female", BirthDate: "1980-01-15"),
            new PatientSearchHit(PatientId: "222", DisplayName: "Smith, John", Gender: "male", BirthDate: "1972-06-03"),
        };
        var html = Render(Name("smith"), hits);

        html.Should().Contain("href=\"/app/patient/111\"");
        html.Should().Contain("href=\"/app/patient/222\"");
        html.Should().Contain("Smith, Jane");
        html.Should().Contain("Smith, John");
        html.Should().Contain("Female", because: "gender renders capitalized for clinical readability");
        html.Should().Contain("Born 1980-01-15");
        html.Should().Contain("results-count\">2</span> matches",
            because: "the meta counter shows how many results came back with the right pluralization");
    }

    [Fact]
    public void Single_Hit_Uses_Singular_Match_Label()
    {
        var hit = new[] { new PatientSearchHit("1", "Solo, Test", "male", "1990-01-01") };
        var html = Render(Name("solo"), hit);
        html.Should().Contain("results-count\">1</span> match<",
            because: "exactly one hit reads 'match', not 'matches' (note the trailing < closes the </div>)");
    }

    [Fact]
    public void Renderer_Html_Encodes_Field_Value_To_Prevent_Xss()
    {
        var html = Render(Name("<script>alert(1)</script>"), Array.Empty<PatientSearchHit>());
        html.Should().NotContain("<script>",
            because: "user-supplied criteria must be HTML-encoded into the search input");
        html.Should().Contain("&lt;script&gt;");
    }

    [Fact]
    public void Renderer_Html_Encodes_Display_Name_To_Prevent_Xss_From_FHIR_Data()
    {
        var hit = new[] { new PatientSearchHit("p1", "<img src=x onerror=alert(1)>", "", "") };
        var html = Render(Name("smith"), hit);
        html.Should().NotContain("<img src=x onerror=alert(1)>");
        html.Should().Contain("&lt;img");
    }

    [Fact]
    public void Renderer_Url_Escapes_Patient_Id_In_Href()
    {
        var hit = new[] { new PatientSearchHit("evil/../other?x=1", "Bad Id", "", "") };
        var html = Render(Name("q"), hit);
        html.Should().Contain("href=\"/app/patient/evil%2F..%2Fother%3Fx%3D1\"");
        html.Should().NotContain("href=\"/app/patient/evil/",
            because: "the slashes in the id must be percent-encoded into the href");
    }

    [Fact]
    public void Renderer_Omits_Meta_When_Gender_And_BirthDate_Both_Empty()
    {
        var hit = new[] { new PatientSearchHit("p1", "Anon", "", "") };
        var html = Render(Name("q"), hit);
        html.Should().Contain("<div class=\"patient-meta\"></div>",
            because: "with no gender and no birth date the meta line is rendered empty");
    }

    [Fact]
    public void Renderer_Omits_Separator_When_Only_Gender_Present()
    {
        var hit = new[] { new PatientSearchHit("p1", "G Only", "male", "") };
        var html = Render(Name("q"), hit);
        html.Should().Contain("Male");
        html.Should().NotContain(" · ",
            because: "only gender, no birth date — the middle-dot separator must not appear");
    }

    [Fact]
    public void Renderer_Omits_Separator_When_Only_BirthDate_Present()
    {
        var hit = new[] { new PatientSearchHit("p1", "D Only", "", "1970-01-01") };
        var html = Render(Name("q"), hit);
        html.Should().Contain("Born 1970-01-01");
        html.Should().NotContain(" · ",
            because: "only birth date, no gender — the separator must not lead the meta line");
    }

    [Fact]
    public void Warning_Renders_Above_Results_When_Present()
    {
        var html = Render(Name("x"), Array.Empty<PatientSearchHit>(),
            warning: "Search timed out. Try a more specific query.");
        html.Should().Contain("<div class=\"warn\">",
            because: "the warning is rendered in a dedicated banner under the search form");
        html.Should().Contain("Search timed out");
    }

    [Fact]
    public void Warning_Is_Html_Encoded()
    {
        var html = Render(Name("x"), Array.Empty<PatientSearchHit>(), warning: "<script>alert('xss')</script>");
        html.Should().NotContain("<script>alert('xss')</script>");
        html.Should().Contain("&lt;script&gt;");
    }

    [Fact]
    public void Warning_Banner_Omitted_When_Null()
    {
        var html = Render(Name("q"), Array.Empty<PatientSearchHit>(), warning: null);
        html.Should().NotContain("class=\"warn\"");
    }
}
