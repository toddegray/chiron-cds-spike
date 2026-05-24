using Chiron.Cds.Engine.Primitives;
using Chiron.Cds.Engine.Rules.Interactions;
using FluentAssertions;

namespace Chiron.Cds.Engine.Tests;

public class DrugDoseRenalTests
{
    private static Engine BuildEngine() =>
        new Engine().RegisterRule(DrugDoseRenalRule.Rules.Single());

    private static Patient Adult(int age = 70, string sex = "M") => new("p", AgeYears: age, Sex: sex);

    [Fact]
    public void No_Creatinine_No_Fire()
    {
        var result = BuildEngine().Evaluate(
            Adult(),
            new[] { new Medication("dabigatran") },
            Array.Empty<Lab>(),
            Array.Empty<Condition>());
        result.Alerts.Should().BeEmpty();
    }

    [Fact]
    public void No_Renal_Listed_Med_No_Fire()
    {
        var result = BuildEngine().Evaluate(
            Adult(),
            new[] { new Medication("lisinopril") },
            new[] { new Lab("creatinine", 2.5, "mg/dL") },
            Array.Empty<Condition>());
        result.Alerts.Should().BeEmpty();
    }

    [Fact]
    public void Normal_Egfr_No_Fire_Even_On_Renal_Drug()
    {
        // 50yo female, creatinine 0.8 → eGFR comfortably above all thresholds.
        var result = BuildEngine().Evaluate(
            Adult(age: 50, sex: "F"),
            new[] { new Medication("dabigatran") },
            new[] { new Lab("creatinine", 0.8, "mg/dL") },
            Array.Empty<Condition>());
        result.Alerts.Should().BeEmpty();
    }

    [Fact]
    public void Low_Egfr_On_Dabigatran_Fires_High()
    {
        // 78yo male, creatinine 2.4 → eGFR ~27 < dabigatran threshold 30.
        var result = BuildEngine().Evaluate(
            Adult(age: 78),
            new[] { new Medication("dabigatran") },
            new[] { new Lab("creatinine", 2.4, "mg/dL") },
            Array.Empty<Condition>());

        var alert = result.Alerts.Should().ContainSingle().Subject;
        alert.RuleId.Should().Be("drug.dose.renal_threshold");
        alert.Severity.Should().Be(Severity.High);
        alert.Message.Should().Contain("dabigatran");
        alert.Message.Should().Contain("Avoid below eGFR 30");
    }

    [Fact]
    public void Multiple_Renal_Drugs_All_In_Because_Tree_And_Multi_Hit_Message_Format()
    {
        // Multiple renally-dosed meds with the patient below their respective thresholds.
        var result = BuildEngine().Evaluate(
            Adult(age: 78),
            new[]
            {
                new Medication("dabigatran"),       // threshold 30
                new Medication("apixaban"),         // threshold 25
                new Medication("gabapentin"),       // threshold 30
                new Medication("metformin"),        // ignored (other rule)
            },
            new[] { new Lab("creatinine", 2.6, "mg/dL") },  // eGFR ~25, below dabigatran + gabapentin
            Array.Empty<Condition>());

        var alert = result.Alerts.Should().ContainSingle().Subject;
        alert.Because.Should().HaveCountGreaterThanOrEqualTo(2,
            because: "dabigatran and gabapentin both trigger below eGFR 30");
        var becauseNames = alert.Because.Select(f => f.Name).ToArray();
        becauseNames.Should().Contain(new[] { "renal_threshold.dabigatran", "renal_threshold.gabapentin" });

        // Multi-hit message format must include the count, the "require renal
        // dose review" wording, and at least the first triggering medication.
        alert.Message.Should().MatchRegex(@"\d+ medications require renal dose review at eGFR",
            because: "the multi-hit branch surfaces the count + canonical wording");
        alert.Message.Should().Contain("dabigatran");
        alert.Message.Should().Contain("gabapentin");
    }

    [Fact]
    public void Egfr_Just_Above_Threshold_Does_Not_Fire()
    {
        // dabigatran threshold is 30. eGFR ~32 (computed) should not fire.
        // 65yo female, creatinine 1.5 → female PCE eGFR ~38; above the line.
        var result = BuildEngine().Evaluate(
            Adult(age: 65, sex: "F"),
            new[] { new Medication("dabigatran") },
            new[] { new Lab("creatinine", 1.5, "mg/dL") },
            Array.Empty<Condition>());
        result.Alerts.Should().BeEmpty();
    }

    [Fact]
    public void Inactive_Renal_Drug_Does_Not_Fire()
    {
        var result = BuildEngine().Evaluate(
            Adult(age: 78),
            new[] { new Medication("dabigatran", Active: false) },
            new[] { new Lab("creatinine", 2.4, "mg/dL") },
            Array.Empty<Condition>());
        result.Alerts.Should().BeEmpty();
    }

    [Theory]
    [InlineData("empagliflozin",   20.0, "Avoid initiation below eGFR 20")]
    [InlineData("dapagliflozin",   25.0, "Avoid initiation below eGFR 25")]
    [InlineData("canagliflozin",   30.0, "Do not initiate below eGFR 30")]
    [InlineData("dabigatran",      30.0, "Avoid below eGFR 30")]
    [InlineData("rivaroxaban",     30.0, "Reduce dose for atrial fibrillation")]
    [InlineData("apixaban",        25.0, "2.5 mg BID dose adjustment")]
    [InlineData("alendronate",     35.0, "Avoid below eGFR 35")]
    [InlineData("nitrofurantoin",  30.0, "Avoid below eGFR 30")]
    [InlineData("rosuvastatin",    30.0, "Start at 5 mg")]
    [InlineData("gabapentin",      30.0, "Reduce dose proportional to eGFR")]
    [InlineData("pregabalin",      30.0, "150 mg/day below eGFR 30")]
    public void Renal_Dose_Table_Entry_Has_Expected_Threshold_And_Rationale(string med, double threshold, string rationaleFragment)
    {
        DrugDoseRenalRule.RenalDoseTable.Should().ContainKey(med);
        var entry = DrugDoseRenalRule.RenalDoseTable[med];
        entry.EgfrThreshold.Should().Be(threshold);
        entry.Rationale.Should().Contain(rationaleFragment);
    }

    [Fact]
    public void Renal_Dose_Table_Has_Exactly_Eleven_Entries()
    {
        // Lock the table size so a future drift is caught by the theory above
        // exhaustively. The theory covers each entry by name; this guard
        // catches additions that aren't paired with a theory row.
        DrugDoseRenalRule.RenalDoseTable.Should().HaveCount(11);
    }

    [Fact]
    public void Pack_Discovery_Includes_Rule()
    {
        var engine = new Engine().RegisterPack(typeof(DrugDoseRenalRule).Assembly);
        engine.Rules.Select(r => r.Id).Should().Contain("drug.dose.renal_threshold");
    }
}
