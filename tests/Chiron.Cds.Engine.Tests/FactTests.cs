using Chiron.Cds.Engine.Primitives;
using FluentAssertions;

namespace Chiron.Cds.Engine.Tests;

public class FactTests
{
    [Fact]
    public void Input_Fact_Has_Empty_Parents_And_Citations()
    {
        var f = Fact.Input("creatinine", 2.4, "mg/dL");
        f.Parents.Should().BeEmpty();
        f.Citations.Should().BeEmpty();
    }

    [Fact]
    public void Fingerprint_Is_Deterministic_For_Identical_Facts()
    {
        var a = Fact.Input("creatinine", 2.4, "mg/dL");
        var b = Fact.Input("creatinine", 2.4, "mg/dL");
        a.Fingerprint.Should().Be(b.Fingerprint);
    }

    [Fact]
    public void Fingerprint_Diverges_When_Value_Changes()
    {
        var a = Fact.Input("creatinine", 2.4, "mg/dL");
        var b = Fact.Input("creatinine", 2.5, "mg/dL");
        a.Fingerprint.Should().NotBe(b.Fingerprint);
    }

    [Fact]
    public void Fingerprint_Includes_Parents()
    {
        var creat = Fact.Input("creatinine", 2.4, "mg/dL");
        var age = Fact.Input("age_years", 78.0, "years");
        var derived = new Fact("egfr", 27.0, "mL/min/1.73m²",
            Parents: new[] { creat, age },
            Citations: Array.Empty<Citation>());
        var withoutParents = new Fact("egfr", 27.0, "mL/min/1.73m²",
            Parents: Array.Empty<Fact>(),
            Citations: Array.Empty<Citation>());
        derived.Fingerprint.Should().NotBe(withoutParents.Fingerprint);
    }

    [Fact]
    public void Fingerprint_Is_Sixteen_Lowercase_Hex_Chars()
    {
        var f = Fact.Input("any", 1.0);
        f.Fingerprint.Should().HaveLength(16);
        f.Fingerprint.Should().MatchRegex("^[0-9a-f]{16}$");
    }

    [Fact]
    public void Parent_Ordering_Does_Not_Affect_Fingerprint()
    {
        var p1 = Fact.Input("a", 1.0);
        var p2 = Fact.Input("b", 2.0);
        var ordered = new Fact("x", 3.0, null,
            Parents: new[] { p1, p2 },
            Citations: Array.Empty<Citation>());
        var reversed = new Fact("x", 3.0, null,
            Parents: new[] { p2, p1 },
            Citations: Array.Empty<Citation>());
        ordered.Fingerprint.Should().Be(reversed.Fingerprint);
    }
}
