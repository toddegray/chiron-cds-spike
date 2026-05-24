using Chiron.Cds.Engine.Primitives;
using Chiron.Cds.Engine.Rules.Scores;
using FluentAssertions;

namespace Chiron.Cds.Engine.Tests;

public class ASCVDTests
{
    private static Engine BuildEngine() =>
        new Engine().RegisterRule(ASCVDRule.Rules.Single());

    private static IEnumerable<Lab> BaselineLabs(
        double totalChol = 200,
        double hdl = 50,
        double systolic = 130) => new[]
    {
        new Lab("total_cholesterol", totalChol, "mg/dL"),
        new Lab("hdl_cholesterol", hdl, "mg/dL"),
        new Lab("systolic_bp", systolic, "mmHg"),
    };

    [Fact]
    public void Healthy_55yo_Male_Below_Threshold_Does_Not_Fire()
    {
        var patient = new Patient("p", AgeYears: 55, Sex: "M");
        var result = BuildEngine().Evaluate(
            patient,
            Array.Empty<Medication>(),
            BaselineLabs(totalChol: 180, hdl: 55, systolic: 120),
            Array.Empty<Condition>());
        result.Alerts.Should().BeEmpty();
    }

    [Fact]
    public void High_Risk_Male_Smoker_With_Diabetes_Fires()
    {
        // 65yo male smoker with diabetes, TC 240, HDL 35, SBP 150 on BP meds.
        // This profile is well above the 7.5% PCE threshold.
        var patient = new Patient("p", AgeYears: 65, Sex: "M");
        var result = BuildEngine().Evaluate(
            patient,
            new[] { new Medication("lisinopril") },
            BaselineLabs(totalChol: 240, hdl: 35, systolic: 150),
            new[] { new Condition("diabetes"), new Condition("current_smoker") });

        var alert = result.Alerts.Should().ContainSingle().Subject;
        alert.RuleId.Should().Be("ascvd.10y.statin_eligible");
        alert.Severity.Should().BeOneOf(Severity.Medium, Severity.High);
        alert.Message.Should().Contain("ASCVD");
    }

    [Fact]
    public void Patient_Outside_Age_Range_Does_Not_Fire()
    {
        // PCE is calibrated for ages 40-79; under 40 doesn't fire.
        var patient = new Patient("p", AgeYears: 35, Sex: "F");
        var result = BuildEngine().Evaluate(
            patient,
            Array.Empty<Medication>(),
            BaselineLabs(totalChol: 280, hdl: 30, systolic: 160),
            new[] { new Condition("diabetes") });
        result.Alerts.Should().BeEmpty(because: "PCE doesn't apply below age 40");
    }

    [Fact]
    public void Patient_On_Statin_Does_Not_Fire()
    {
        // Already on a statin → don't re-recommend it.
        var patient = new Patient("p", AgeYears: 70, Sex: "M");
        var result = BuildEngine().Evaluate(
            patient,
            new[] { new Medication("atorvastatin") },
            BaselineLabs(totalChol: 240, hdl: 35, systolic: 150),
            new[] { new Condition("diabetes") });
        result.Alerts.Should().BeEmpty();
    }

    [Fact]
    public void Patient_With_Established_ASCVD_Does_Not_Fire_Primary_Prevention_Rule()
    {
        var patient = new Patient("p", AgeYears: 65, Sex: "M");
        var result = BuildEngine().Evaluate(
            patient,
            Array.Empty<Medication>(),
            BaselineLabs(totalChol: 240, hdl: 35, systolic: 150),
            new[] { new Condition("myocardial_infarction") });
        result.Alerts.Should().BeEmpty(because: "secondary prevention is a different track");
    }

    [Fact]
    public void Missing_Required_Lab_Does_Not_Fire()
    {
        var patient = new Patient("p", AgeYears: 65, Sex: "M");
        var result = BuildEngine().Evaluate(
            patient,
            Array.Empty<Medication>(),
            new[] { new Lab("total_cholesterol", 240, "mg/dL") }, // no HDL, no SBP
            Array.Empty<Condition>());
        result.Alerts.Should().BeEmpty();
    }

    [Theory]
    // Reference points from the official Pooled Cohort Equation calculator.
    // Sanity-bounds rather than exact equality; the equation has rounding
    // sensitivity at intermediate ages.
    [InlineData(55, false, 213, 50, 120, false, false, false, 0.02, 0.06)] // healthy middle-aged male
    [InlineData(65, true,  220, 55, 140, true,  true,  true,  0.10, 0.40)] // 65yo female, diabetic, smoker, treated HTN
    [InlineData(70, false, 200, 45, 150, true,  true,  false, 0.18, 0.50)] // 70yo male, diabetic, treated HTN
    public void Pooled_Cohort_Risk_Falls_In_Expected_Range(
        int age, bool isFemale, double tc, double hdl, double sbp,
        bool onBpTx, bool hasDm, bool smoker, double minExpected, double maxExpected)
    {
        var risk = ASCVDRule.ComputePooledCohortRisk(age, isFemale, tc, hdl, sbp, onBpTx, hasDm, smoker);
        risk.Should().BeInRange(minExpected, maxExpected,
            because: "PCE risk for this profile must land in a clinically reasonable band");
    }

    [Fact]
    public void Borderline_Risk_Fires_At_Low_Severity()
    {
        // Profile chosen so PCE 10y risk lands between the 7.5% statin threshold
        // (rule fires) and the 10% Medium-severity threshold (rule fires as Low).
        // 55yo male, TC 215, HDL 42, SBP 134, no other factors.
        var patient = new Patient("p", AgeYears: 55, Sex: "M");
        var rawRisk = ASCVDRule.ComputePooledCohortRisk(
            ageYears: 55, isFemale: false, totalCholMgDl: 215, hdlMgDl: 42,
            systolicBpMmHg: 134, onBpTreatment: false, hasDiabetes: false, isSmoker: false);
        rawRisk.Should().BeInRange(0.075, 0.10,
            because: "test profile must land in the Low-severity band to exercise that arm");

        var result = BuildEngine().Evaluate(
            patient,
            Array.Empty<Medication>(),
            BaselineLabs(totalChol: 215, hdl: 42, systolic: 134),
            Array.Empty<Condition>());

        var alert = result.Alerts.Should().ContainSingle().Subject;
        alert.Severity.Should().Be(Severity.Low);
    }

    [Fact]
    public void Severity_Scales_With_Risk()
    {
        var patient = new Patient("p", AgeYears: 78, Sex: "M");
        // Very high risk: elderly male, severe hyperchol, low HDL, high BP, all factors.
        var result = BuildEngine().Evaluate(
            patient,
            new[] { new Medication("hydrochlorothiazide") },
            BaselineLabs(totalChol: 280, hdl: 28, systolic: 170),
            new[] { new Condition("diabetes"), new Condition("current_smoker") });
        result.Alerts.Should().ContainSingle()
            .Which.Severity.Should().Be(Severity.High);
    }

    [Fact]
    public void Fingerprint_Stable_For_Same_Inputs()
    {
        var patient = new Patient("p", AgeYears: 65, Sex: "M");
        string Run() => BuildEngine().Evaluate(
            patient,
            new[] { new Medication("lisinopril") },
            BaselineLabs(240, 35, 150),
            new[] { new Condition("diabetes"), new Condition("current_smoker") })
            .Alerts.Single().Fingerprint;
        Run().Should().Be(Run());
    }
}
