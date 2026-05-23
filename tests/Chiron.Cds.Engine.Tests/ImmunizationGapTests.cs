using Chiron.Cds.Engine.Primitives;
using Chiron.Cds.Engine.Rules.Preventive;
using FluentAssertions;

namespace Chiron.Cds.Engine.Tests;

public class ImmunizationGapTests
{
    private static Engine BuildEngine() =>
        new Engine().RegisterRule(ImmunizationGapRule.Rules.Single());

    private static Patient YoungAdult() => new("p", AgeYears: 30, Sex: "F");
    private static Patient Elder() => new("p", AgeYears: 72, Sex: "M");

    private static DateTimeOffset RecentlyAdministered(int monthsAgo) =>
        DateTimeOffset.UtcNow.AddMonths(-monthsAgo);

    [Fact]
    public void Young_Adult_With_Recent_Flu_And_Tdap_Has_No_Gap()
    {
        var result = BuildEngine().Evaluate(
            YoungAdult(),
            Array.Empty<Medication>(),
            Array.Empty<Lab>(),
            Array.Empty<Condition>(),
            allergies: null,
            immunizations: new[]
            {
                new Immunization("influenza", RecentlyAdministered(2)),
                new Immunization("tdap", RecentlyAdministered(12)),
                new Immunization("covid19", RecentlyAdministered(3)),
            });
        result.Alerts.Should().BeEmpty();
    }

    [Fact]
    public void Missing_Annual_Flu_Fires_Gap()
    {
        var result = BuildEngine().Evaluate(
            YoungAdult(),
            Array.Empty<Medication>(),
            Array.Empty<Lab>(),
            Array.Empty<Condition>(),
            allergies: null,
            immunizations: new[]
            {
                new Immunization("tdap", RecentlyAdministered(12)),
                new Immunization("covid19", RecentlyAdministered(3)),
                // No influenza
            });
        var alert = result.Alerts.Should().ContainSingle().Subject;
        alert.RuleId.Should().Be("immunization.gap");
        alert.Message.Should().Contain("Influenza");
        alert.Message.Should().Contain("never been administered");
    }

    [Fact]
    public void Overdue_Tdap_Fires_With_Last_Dose_Date_In_Message()
    {
        var lastTdap = DateTimeOffset.UtcNow.AddYears(-11);
        var result = BuildEngine().Evaluate(
            YoungAdult(),
            Array.Empty<Medication>(),
            Array.Empty<Lab>(),
            Array.Empty<Condition>(),
            allergies: null,
            immunizations: new[]
            {
                new Immunization("influenza", RecentlyAdministered(2)),
                new Immunization("tdap", lastTdap),
                new Immunization("covid19", RecentlyAdministered(3)),
            });
        var alert = result.Alerts.Should().ContainSingle().Subject;
        alert.Message.Should().Contain("Tdap");
        alert.Message.Should().Contain("overdue");
        alert.Message.Should().Contain(lastTdap.Year.ToString());
    }

    [Fact]
    public void Pneumococcal_Required_Only_At_65_Plus()
    {
        // 64yo with no pneumococcal: no pneumococcal gap (still below age).
        var result64 = BuildEngine().Evaluate(
            new Patient("p", AgeYears: 64, Sex: "F"),
            Array.Empty<Medication>(),
            Array.Empty<Lab>(),
            Array.Empty<Condition>(),
            allergies: null,
            immunizations: new[]
            {
                new Immunization("influenza", RecentlyAdministered(2)),
                new Immunization("tdap", RecentlyAdministered(12)),
                new Immunization("covid19", RecentlyAdministered(3)),
                new Immunization("zoster_recombinant", RecentlyAdministered(36)),
            });
        result64.Alerts.Should().BeEmpty(because: "at 64, pneumococcal is not yet recommended");

        // 72yo with no pneumococcal: gap fires.
        var result72 = BuildEngine().Evaluate(
            Elder(),
            Array.Empty<Medication>(),
            Array.Empty<Lab>(),
            Array.Empty<Condition>(),
            allergies: null,
            immunizations: new[]
            {
                new Immunization("influenza", RecentlyAdministered(2)),
                new Immunization("tdap", RecentlyAdministered(12)),
                new Immunization("covid19", RecentlyAdministered(3)),
                new Immunization("zoster_recombinant", RecentlyAdministered(36)),
            });
        var alert = result72.Alerts.Should().ContainSingle().Subject;
        alert.Message.Should().Contain("Pneumococcal");
    }

    [Fact]
    public void Not_Done_Immunization_Does_Not_Count_Toward_Coverage()
    {
        // Flu was "not-done" 2 months ago — should NOT satisfy the annual flu recommendation.
        var result = BuildEngine().Evaluate(
            YoungAdult(),
            Array.Empty<Medication>(),
            Array.Empty<Lab>(),
            Array.Empty<Condition>(),
            allergies: null,
            immunizations: new[]
            {
                new Immunization("influenza", RecentlyAdministered(2), Status: "not-done"),
                new Immunization("tdap", RecentlyAdministered(12)),
                new Immunization("covid19", RecentlyAdministered(3)),
            });
        result.Alerts.Should().ContainSingle()
            .Which.Message.Should().Contain("Influenza");
    }

    [Fact]
    public void Multiple_Gaps_Summarized_In_Message()
    {
        // Elder with no influenza, no Tdap, no covid → 3 gaps.
        var result = BuildEngine().Evaluate(
            Elder(),
            Array.Empty<Medication>(),
            Array.Empty<Lab>(),
            Array.Empty<Condition>(),
            allergies: null,
            immunizations: new[]
            {
                new Immunization("zoster_recombinant", RecentlyAdministered(60)),
                new Immunization("pneumococcal_pcv20", RecentlyAdministered(12)),
            });
        var alert = result.Alerts.Should().ContainSingle().Subject;
        alert.Message.Should().Contain("3 immunization gaps");
        alert.Because.Should().HaveCountGreaterThanOrEqualTo(3);
    }

    [Fact]
    public void Severity_Is_Low_For_Preventive_Gaps()
    {
        var result = BuildEngine().Evaluate(
            YoungAdult(),
            Array.Empty<Medication>(),
            Array.Empty<Lab>(),
            Array.Empty<Condition>(),
            allergies: null,
            immunizations: Array.Empty<Immunization>());
        result.Alerts.Should().ContainSingle()
            .Which.Severity.Should().Be(Severity.Low);
    }

    [Fact]
    public void Fingerprint_Stable_For_Same_Inputs()
    {
        var inputs = new[]
        {
            new Immunization("tdap", new DateTimeOffset(2020, 1, 15, 0, 0, 0, TimeSpan.Zero)),
            new Immunization("covid19", new DateTimeOffset(2025, 3, 5, 0, 0, 0, TimeSpan.Zero)),
        };
        string Run() => BuildEngine().Evaluate(
            new Patient("p", AgeYears: 40, Sex: "F"),
            Array.Empty<Medication>(), Array.Empty<Lab>(),
            Array.Empty<Condition>(), allergies: null, immunizations: inputs)
            .Alerts.Single().Fingerprint;
        Run().Should().Be(Run());
    }
}
