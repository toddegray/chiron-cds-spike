using Chiron.Cds.Engine.Primitives;
using Chiron.Cds.Web.Persistence;
using FluentAssertions;

namespace Chiron.Cds.Web.IntegrationTests;

/// <summary>
/// Direct tests for <see cref="SqliteOverrideLog"/>. Each test gets a
/// fresh in-memory SQLite database so they don't interact and don't
/// touch the filesystem.
/// </summary>
public class SqliteOverrideLogTests
{
    private static SqliteOverrideLog NewLog()
    {
        // Each test gets its own named shared-cache in-memory database so
        // the multiple Open() calls inside SqliteOverrideLog see the same
        // schema + data, while different tests stay isolated.
        var dbName = "test-" + Guid.NewGuid().ToString("N");
        return new SqliteOverrideLog($"Data Source=file:{dbName}?mode=memory&cache=shared");
    }

    private static Alert MakeAlert(string ruleId = "test.rule", string substance = "creatinine") =>
        new(
            RuleId: ruleId,
            Severity: Severity.High,
            Message: "test",
            Because: new[] { Fact.Input(substance, 2.4, "mg/dL") },
            Citations: Array.Empty<Citation>(),
            OverrideOptions: Array.Empty<string>());

    [Fact]
    public void RecordFire_Then_FatigueReport_Reflects_It()
    {
        using var log = NewLog();
        var alert = MakeAlert(ruleId: "rule.a");
        log.RecordFire(alert);

        var report = log.FatigueReport().Where(r => r.RuleId == "rule.a").ToArray();
        report.Should().ContainSingle();
        report[0].Fires.Should().Be(1);
        report[0].Overrides.Should().Be(0);
        report[0].OverrideRate.Should().Be(0);
    }

    [Fact]
    public void Multiple_Fires_Of_Same_Fingerprint_Increment_Count()
    {
        using var log = NewLog();
        var alert = MakeAlert(ruleId: "rule.b");
        log.RecordFire(alert);
        log.RecordFire(alert);
        log.RecordFire(alert);

        var row = log.FatigueReport().Single(r => r.RuleId == "rule.b");
        row.Fires.Should().Be(3);
    }

    [Fact]
    public void RecordOverride_Increments_Override_Count_And_OverrideRate()
    {
        using var log = NewLog();
        var alert = MakeAlert(ruleId: "rule.c");
        log.RecordFire(alert);
        log.RecordFire(alert);
        log.RecordOverride(alert.Fingerprint, overriddenBy: "user-1", reason: "documented");

        var row = log.FatigueReport().Single(r => r.RuleId == "rule.c");
        row.Fires.Should().Be(2);
        row.Overrides.Should().Be(1);
        row.OverrideRate.Should().BeApproximately(0.5, 0.0001);
    }

    [Fact]
    public void RecordOverride_Without_Prior_Fire_Has_Override_With_Unknown_RuleId()
    {
        using var log = NewLog();
        var fingerprint = "abc123def456abc1";
        log.RecordOverride(fingerprint, overriddenBy: "user-1");

        var row = log.FatigueReport().Single(r => r.Fingerprint == fingerprint);
        row.Overrides.Should().Be(1);
        row.Fires.Should().Be(0);
        row.RuleId.Should().Be("<unknown>");
    }

    [Fact]
    public void RecordFire_Null_Alert_Throws()
    {
        using var log = NewLog();
        var act = () => log.RecordFire(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void RecordOverride_Rejects_Empty_Fingerprint(string? fp)
    {
        using var log = NewLog();
        var act = () => log.RecordOverride(fp!, overriddenBy: "user-1");
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void RecordOverride_Rejects_Empty_OverriddenBy(string? by)
    {
        using var log = NewLog();
        var act = () => log.RecordOverride("abc123def456abc1", overriddenBy: by!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void FatigueReport_Orders_By_Override_Rate_Descending()
    {
        using var log = NewLog();
        var ruleHigh = MakeAlert(ruleId: "rule.high", substance: "high");
        var ruleLow = MakeAlert(ruleId: "rule.low", substance: "low");
        // ruleHigh: 1 fire / 1 override → rate 1.0
        log.RecordFire(ruleHigh);
        log.RecordOverride(ruleHigh.Fingerprint, "u");
        // ruleLow: 10 fires / 1 override → rate 0.1
        for (var i = 0; i < 10; i++) log.RecordFire(ruleLow);
        log.RecordOverride(ruleLow.Fingerprint, "u");

        var rows = log.FatigueReport()
            .Where(r => r.RuleId is "rule.high" or "rule.low")
            .ToArray();
        rows[0].RuleId.Should().Be("rule.high",
            because: "the noisier rule (override rate 1.0) should come first");
        rows[1].RuleId.Should().Be("rule.low");
    }

    [Fact]
    public void Constructor_Rejects_Empty_Connection_String()
    {
        var act = () => new SqliteOverrideLog("");
        act.Should().Throw<ArgumentException>();
    }
}
