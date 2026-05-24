using Chiron.Cds.Engine;
using Chiron.Cds.Engine.Primitives;
using FluentAssertions;

namespace Chiron.Cds.Engine.Tests;

/// <summary>
/// Pins the <see cref="InMemoryOverrideLog"/> contract. The SQLite
/// implementation lives in the Web project and has its own test class;
/// these tests ensure the in-memory fallback (used in
/// <c>WebApplicationFactory</c> overrides and any single-process demo)
/// matches the same behavioural surface.
/// </summary>
public class InMemoryOverrideLogTests
{
    private static Alert MakeAlert(string ruleId = "test.rule") =>
        new(
            RuleId: ruleId,
            Severity: Severity.High,
            Message: "test",
            Because: new[] { Fact.Input("creatinine", 2.4, "mg/dL") },
            Citations: Array.Empty<Citation>(),
            OverrideOptions: Array.Empty<string>());

    [Fact]
    public void RecordFire_Increments_Fires()
    {
        var log = new InMemoryOverrideLog();
        var alert = MakeAlert();
        log.RecordFire(alert);
        log.RecordFire(alert);
        log.RecordFire(alert);
        var row = log.FatigueReport().Single(r => r.Fingerprint == alert.Fingerprint);
        row.Fires.Should().Be(3);
        row.RuleId.Should().Be("test.rule");
    }

    [Fact]
    public void RecordOverride_Increments_Overrides_And_Rate()
    {
        var log = new InMemoryOverrideLog();
        var alert = MakeAlert();
        log.RecordFire(alert);
        log.RecordFire(alert);
        log.RecordOverride(alert.Fingerprint, "user-1", reason: "documented");
        var row = log.FatigueReport().Single(r => r.Fingerprint == alert.Fingerprint);
        row.Fires.Should().Be(2);
        row.Overrides.Should().Be(1);
        row.OverrideRate.Should().BeApproximately(0.5, 0.0001);
    }

    [Fact]
    public void Override_Without_Prior_Fire_Has_Unknown_RuleId()
    {
        var log = new InMemoryOverrideLog();
        log.RecordOverride("orphan-fingerprint", "user-1");
        var row = log.FatigueReport().Single(r => r.Fingerprint == "orphan-fingerprint");
        row.RuleId.Should().Be("<unknown>");
        row.Fires.Should().Be(0);
        row.Overrides.Should().Be(1);
    }

    [Fact]
    public void Empty_Log_Produces_Empty_Report()
    {
        var log = new InMemoryOverrideLog();
        log.FatigueReport().Should().BeEmpty();
    }

    [Fact]
    public void Report_Ordered_By_Override_Rate_Descending()
    {
        var log = new InMemoryOverrideLog();
        var noisy = MakeAlert("rule.noisy");
        var quiet = MakeAlert("rule.quiet");
        log.RecordFire(noisy);
        log.RecordOverride(noisy.Fingerprint, "u");
        for (var i = 0; i < 10; i++) log.RecordFire(quiet);
        log.RecordOverride(quiet.Fingerprint, "u");
        var rows = log.FatigueReport().Where(r => r.RuleId is "rule.noisy" or "rule.quiet").ToArray();
        rows[0].RuleId.Should().Be("rule.noisy");
    }

    [Fact]
    public void RecordFire_Rejects_Null()
    {
        var log = new InMemoryOverrideLog();
        var act = () => log.RecordFire(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RecordOverride_Rejects_Empty_Fingerprint(string? fp)
    {
        var log = new InMemoryOverrideLog();
        var act = () => log.RecordOverride(fp!, "user");
        act.Should().Throw<ArgumentException>();
    }
}
