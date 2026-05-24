using Chiron.Cds.Engine.Primitives;
using Chiron.Cds.Engine.Rules.Preventive;
using FluentAssertions;

namespace Chiron.Cds.Engine.Tests;

public class USPSTFScreeningTests
{
    private static Engine BuildMammographyEngine() =>
        new Engine().RegisterRule(USPSTFScreeningRules.Rules.Single(r => r.Id == "uspstf.mammography.gap"));

    private static Engine BuildColorectalEngine() =>
        new Engine().RegisterRule(USPSTFScreeningRules.Rules.Single(r => r.Id == "uspstf.colorectal.gap"));

    private static DateTimeOffset DaysAgo(int days) => DateTimeOffset.UtcNow.AddDays(-days);

    // -------- Mammography --------

    [Fact]
    public void Mammography_Does_Not_Fire_For_Males()
    {
        var result = BuildMammographyEngine().Evaluate(
            new Patient("p", AgeYears: 55, Sex: "M"),
            Array.Empty<Medication>(),
            Array.Empty<Lab>(),
            Array.Empty<Condition>(),
            allergies: null, immunizations: null,
            procedures: Array.Empty<Procedure>());
        result.Alerts.Should().BeEmpty();
    }

    [Fact]
    public void Mammography_Does_Not_Fire_Under_40()
    {
        var result = BuildMammographyEngine().Evaluate(
            new Patient("p", AgeYears: 38, Sex: "F"),
            Array.Empty<Medication>(),
            Array.Empty<Lab>(),
            Array.Empty<Condition>(),
            allergies: null, immunizations: null,
            procedures: Array.Empty<Procedure>());
        result.Alerts.Should().BeEmpty();
    }

    [Fact]
    public void Mammography_Does_Not_Fire_Over_74()
    {
        var result = BuildMammographyEngine().Evaluate(
            new Patient("p", AgeYears: 80, Sex: "F"),
            Array.Empty<Medication>(),
            Array.Empty<Lab>(),
            Array.Empty<Condition>(),
            allergies: null, immunizations: null,
            procedures: Array.Empty<Procedure>());
        result.Alerts.Should().BeEmpty();
    }

    [Fact]
    public void Mammography_Fires_When_Never_Performed_In_Eligible_Range()
    {
        var result = BuildMammographyEngine().Evaluate(
            new Patient("p", AgeYears: 55, Sex: "F"),
            Array.Empty<Medication>(),
            Array.Empty<Lab>(),
            Array.Empty<Condition>(),
            allergies: null, immunizations: null,
            procedures: Array.Empty<Procedure>());
        var alert = result.Alerts.Should().ContainSingle().Subject;
        alert.Severity.Should().Be(Severity.Low);
        alert.Message.Should().Contain("Mammography overdue");
        alert.Message.Should().Contain("no record");
    }

    [Fact]
    public void Mammography_Recent_Within_Two_Years_Does_Not_Fire()
    {
        var result = BuildMammographyEngine().Evaluate(
            new Patient("p", AgeYears: 60, Sex: "F"),
            Array.Empty<Medication>(),
            Array.Empty<Lab>(),
            Array.Empty<Condition>(),
            allergies: null, immunizations: null,
            procedures: new[] { new Procedure("mammography", DaysAgo(400)) });
        result.Alerts.Should().BeEmpty();
    }

    [Fact]
    public void Mammography_Overdue_Beyond_Two_Years_Fires_With_Date_In_Message()
    {
        var lastDate = DaysAgo(800);
        var result = BuildMammographyEngine().Evaluate(
            new Patient("p", AgeYears: 60, Sex: "F"),
            Array.Empty<Medication>(),
            Array.Empty<Lab>(),
            Array.Empty<Condition>(),
            allergies: null, immunizations: null,
            procedures: new[] { new Procedure("mammography", lastDate) });
        var alert = result.Alerts.Should().ContainSingle().Subject;
        alert.Message.Should().Contain("Mammography overdue");
        alert.Message.Should().Contain(lastDate.Year.ToString());
    }

    [Fact]
    public void Mammography_Not_Completed_Does_Not_Satisfy()
    {
        // A "in-progress" or "not-done" procedure doesn't count as
        // surveillance coverage — the LatestProcedure filter rejects it.
        var result = BuildMammographyEngine().Evaluate(
            new Patient("p", AgeYears: 60, Sex: "F"),
            Array.Empty<Medication>(),
            Array.Empty<Lab>(),
            Array.Empty<Condition>(),
            allergies: null, immunizations: null,
            procedures: new[] { new Procedure("mammography", DaysAgo(100), Status: "not-done") });
        result.Alerts.Should().ContainSingle();
    }

    // -------- Colorectal --------

    [Fact]
    public void Colorectal_Does_Not_Fire_Under_45()
    {
        var result = BuildColorectalEngine().Evaluate(
            new Patient("p", AgeYears: 44, Sex: "M"),
            Array.Empty<Medication>(),
            Array.Empty<Lab>(),
            Array.Empty<Condition>(),
            allergies: null, immunizations: null,
            procedures: Array.Empty<Procedure>());
        result.Alerts.Should().BeEmpty();
    }

    [Fact]
    public void Colorectal_Fires_For_50yo_With_No_Prior_Screening()
    {
        var result = BuildColorectalEngine().Evaluate(
            new Patient("p", AgeYears: 50, Sex: "M"),
            Array.Empty<Medication>(),
            Array.Empty<Lab>(),
            Array.Empty<Condition>(),
            allergies: null, immunizations: null,
            procedures: Array.Empty<Procedure>());
        var alert = result.Alerts.Should().ContainSingle().Subject;
        alert.Message.Should().Contain("Colorectal cancer screening overdue");
    }

    [Fact]
    public void Colorectal_Recent_Colonoscopy_Within_Ten_Years_Does_Not_Fire()
    {
        var result = BuildColorectalEngine().Evaluate(
            new Patient("p", AgeYears: 60, Sex: "M"),
            Array.Empty<Medication>(),
            Array.Empty<Lab>(),
            Array.Empty<Condition>(),
            allergies: null, immunizations: null,
            procedures: new[] { new Procedure("colonoscopy", DaysAgo(365 * 5)) });
        result.Alerts.Should().BeEmpty();
    }

    [Fact]
    public void Colorectal_Overdue_Beyond_Ten_Years_Fires()
    {
        var result = BuildColorectalEngine().Evaluate(
            new Patient("p", AgeYears: 65, Sex: "F"),
            Array.Empty<Medication>(),
            Array.Empty<Lab>(),
            Array.Empty<Condition>(),
            allergies: null, immunizations: null,
            procedures: new[] { new Procedure("colonoscopy", DaysAgo(365 * 12)) });
        var alert = result.Alerts.Should().ContainSingle().Subject;
        alert.Message.Should().Contain("Colorectal");
        alert.Message.Should().Contain(">10y");
    }

    [Fact]
    public void Both_Rules_Register_Via_Pack_Discovery()
    {
        var engine = new Engine().RegisterPack(typeof(USPSTFScreeningRules).Assembly);
        engine.Rules.Select(r => r.Id).Should().Contain(new[]
        {
            "uspstf.mammography.gap",
            "uspstf.colorectal.gap",
        });
    }
}
