using Chiron.Cds.Web.Panel;
using FluentAssertions;

namespace Chiron.Cds.Web.IntegrationTests;

/// <summary>
/// Pure unit tests for <see cref="PatientSearchRenderer"/>. The renderer
/// is a static function — render some hits, render an empty result, render
/// an empty query — and assert the HTML carries the expected anchors.
/// </summary>
public class PatientSearchRendererTests
{
    private const string NavBar = "<span class=\"brand\">Chiron</span>";

    [Fact]
    public void Empty_Query_Renders_Hint_Not_Results()
    {
        var html = PatientSearchRenderer.Render("", Array.Empty<PatientSearchHit>(), NavBar, "/app/patient");
        html.Should().Contain("Start typing a last name",
            because: "the empty-query state guides the user instead of showing 'no results'");
        html.Should().NotContain("No patients matched",
            because: "the empty-query state must not show the empty-results message");
    }

    [Fact]
    public void Empty_Results_For_Query_Renders_Empty_State()
    {
        var html = PatientSearchRenderer.Render("xyzzy", Array.Empty<PatientSearchHit>(), NavBar, "/app/patient");
        html.Should().Contain("No patients matched");
        html.Should().Contain("xyzzy",
            because: "the empty-state echoes the query the user typed so they can correct it");
    }

    [Fact]
    public void Hits_Render_As_Rows_Linked_To_Visit_Brief()
    {
        var hits = new[]
        {
            new PatientSearchHit(PatientId: "111", DisplayName: "Smith, Jane", Gender: "female", BirthDate: "1980-01-15"),
            new PatientSearchHit(PatientId: "222", DisplayName: "Smith, John", Gender: "male", BirthDate: "1972-06-03"),
        };
        var html = PatientSearchRenderer.Render("smith", hits, NavBar, "/app/patient");

        html.Should().Contain("href=\"/app/patient/111\"");
        html.Should().Contain("href=\"/app/patient/222\"");
        html.Should().Contain("Smith, Jane");
        html.Should().Contain("Smith, John");
        html.Should().Contain("Female",
            because: "gender renders capitalized for clinical readability");
        html.Should().Contain("Born 1980-01-15");
        html.Should().Contain("results-count\">2</span> matches",
            because: "the meta counter shows how many results came back with the right pluralization");
    }

    [Fact]
    public void Single_Hit_Uses_Singular_Match_Label()
    {
        var hit = new[] { new PatientSearchHit("1", "Solo, Test", "male", "1990-01-01") };
        var html = PatientSearchRenderer.Render("solo", hit, NavBar, "/app/patient");
        html.Should().Contain("results-count\">1</span> match<",
            because: "exactly one hit reads 'match', not 'matches' (note the trailing < closes the </div>)");
    }

    [Fact]
    public void Renderer_Html_Encodes_Query_To_Prevent_Xss()
    {
        var malicious = "<script>alert(1)</script>";
        var html = PatientSearchRenderer.Render(malicious, Array.Empty<PatientSearchHit>(), NavBar, "/app/patient");
        html.Should().NotContain("<script>",
            because: "user-supplied query must be HTML-encoded into the search input and empty-state echo");
        html.Should().Contain("&lt;script&gt;");
    }

    [Fact]
    public void Renderer_Html_Encodes_Display_Name_To_Prevent_Xss_From_FHIR_Data()
    {
        // FHIR data flows from a remote server we don't control — names
        // copied through must be HTML-encoded the same as the user-typed
        // query. This is the gap the original test missed.
        var hit = new[] { new PatientSearchHit("p1", "<img src=x onerror=alert(1)>", "", "") };
        var html = PatientSearchRenderer.Render("smith", hit, NavBar, "/app/patient");
        html.Should().NotContain("<img src=x onerror=alert(1)>");
        html.Should().Contain("&lt;img");
    }

    [Fact]
    public void Renderer_Url_Escapes_Patient_Id_In_Href()
    {
        // A patient id containing '/' or '?' would otherwise inject extra
        // path segments or a query string. WebUtility.HtmlEncode does NOT
        // escape these — Uri.EscapeDataString does.
        var hit = new[] { new PatientSearchHit("evil/../other?x=1", "Bad Id", "", "") };
        var html = PatientSearchRenderer.Render("q", hit, NavBar, "/app/patient");
        html.Should().Contain("href=\"/app/patient/evil%2F..%2Fother%3Fx%3D1\"");
        html.Should().NotContain("href=\"/app/patient/evil/",
            because: "the slashes in the id must be percent-encoded into the href");
    }

    [Fact]
    public void Renderer_Omits_Meta_When_Gender_And_BirthDate_Both_Empty()
    {
        var hit = new[] { new PatientSearchHit("p1", "Anon", "", "") };
        var html = PatientSearchRenderer.Render("q", hit, NavBar, "/app/patient");
        // Meta div is present (it always renders) but should be empty —
        // no separator, no labels.
        html.Should().Contain("<div class=\"patient-meta\"></div>",
            because: "with no gender and no birth date the meta line is rendered empty");
    }

    [Fact]
    public void Renderer_Omits_Separator_When_Only_Gender_Present()
    {
        var hit = new[] { new PatientSearchHit("p1", "G Only", "male", "") };
        var html = PatientSearchRenderer.Render("q", hit, NavBar, "/app/patient");
        html.Should().Contain("Male");
        html.Should().NotContain(" · ",
            because: "only gender, no birth date — the middle-dot separator must not appear");
    }

    [Fact]
    public void Renderer_Omits_Separator_When_Only_BirthDate_Present()
    {
        var hit = new[] { new PatientSearchHit("p1", "D Only", "", "1970-01-01") };
        var html = PatientSearchRenderer.Render("q", hit, NavBar, "/app/patient");
        html.Should().Contain("Born 1970-01-01");
        html.Should().NotContain(" · ",
            because: "only birth date, no gender — the separator must not lead the meta line");
    }
}
