using Chiron.Cds.Web.Panel;
using FluentAssertions;
using Hl7.Fhir.Model;
using FhirOperationException = Hl7.Fhir.Rest.FhirOperationException;

namespace Chiron.Cds.Web.IntegrationTests;

/// <summary>
/// Offline unit tests for the pure helpers in <see cref="ResultReviewService"/>:
/// demographics projection, observation grouping, and the report-shape
/// projection. The live FHIR path is covered in
/// <c>ResultsControllerLiveTests</c>; these tests pin the branches an
/// offline harness can reach.
/// </summary>
public class ResultReviewServiceUnitTests
{
    [Fact]
    public void GroupTrends_Groups_By_Loinc_And_Keeps_Six_Most_Recent_Per_Group()
    {
        var observations = new[]
        {
            Obs(loinc: "2345-7", title: "Glucose Level", value: 5.5, unit: "mmol/L", at: "2026-05-21T15:21:40Z"),
            Obs(loinc: "2345-7", title: "Glucose Level", value: 6.0, unit: "mmol/L", at: "2026-05-21T15:21:45Z"),
            Obs(loinc: "2345-7", title: "Glucose Level", value: 7.1, unit: "mmol/L", at: "2024-09-26T12:00:00Z"),
            Obs(loinc: "2345-7", title: "Glucose Level", value: 5.0, unit: "mmol/L", at: "2024-05-16T08:00:00Z"),
            Obs(loinc: "2345-7", title: "Glucose Level", value: 4.9, unit: "mmol/L", at: "2024-01-04T07:30:00Z"),
            Obs(loinc: "2345-7", title: "Glucose Level", value: 5.3, unit: "mmol/L", at: "2023-09-12T07:30:00Z"),
            Obs(loinc: "2345-7", title: "Glucose Level", value: 5.8, unit: "mmol/L", at: "2023-02-01T07:30:00Z"),
            Obs(loinc: "8462-4", title: "Diastolic blood pressure", value: 80, unit: "mmHg", at: "2026-05-01T09:00:00Z"),
        };

        var trends = ResultReviewService.GroupTrends(observations);

        trends.Should().HaveCount(2);

        var glucose = trends.Single(t => t.Loinc == "2345-7");
        glucose.Title.Should().Be("Glucose Level");
        glucose.Unit.Should().Be("mmol/L");
        glucose.Points.Should().HaveCount(6, because: "the trend series is capped at six values");
        glucose.Points[0].Value.Should().Be("6",
            because: "the latest value (2026-05-21T15:21:45Z) is the head of the list");
        glucose.Points[5].Value.Should().Be("5.3",
            because: "after dropping the oldest two, 5.3 from 2023-09-12 is the tail of the kept window");
    }

    [Fact]
    public void GroupTrends_Orders_Trends_By_Most_Recent_Value_Descending()
    {
        var observations = new[]
        {
            Obs(loinc: "OLD", title: "Old lab", value: 1, unit: "x", at: "2020-01-01T00:00:00Z"),
            Obs(loinc: "NEW", title: "New lab", value: 2, unit: "x", at: "2026-05-21T00:00:00Z"),
            Obs(loinc: "MID", title: "Middle lab", value: 3, unit: "x", at: "2024-01-01T00:00:00Z"),
        };
        var trends = ResultReviewService.GroupTrends(observations);
        trends.Select(t => t.Loinc).Should().ContainInOrder("NEW", "MID", "OLD");
    }

    [Fact]
    public void GroupTrends_Drops_Observations_Without_A_Numeric_Value()
    {
        var observations = new[]
        {
            // text-only result — skip
            new Observation { Code = Code("STR", "String result"), Value = new FhirString("positive") },
            // numeric — keep
            Obs(loinc: "2345-7", title: "Glucose Level", value: 6.0, unit: "mmol/L", at: "2026-05-21T00:00:00Z"),
        };
        var trends = ResultReviewService.GroupTrends(observations);
        trends.Should().HaveCount(1);
        trends[0].Title.Should().Be("Glucose Level");
    }

    [Fact]
    public void GroupTrends_Flags_Abnormal_Via_Interpretation_Coding()
    {
        var hi = Obs(loinc: "2345-7", title: "Glucose Level", value: 14.0, unit: "mmol/L", at: "2026-05-21T00:00:00Z");
        hi.Interpretation = new List<CodeableConcept>
        {
            new() { Coding = new List<Coding> { new() { Code = "H" } } },
        };
        var trends = ResultReviewService.GroupTrends(new[] { hi });
        trends.Single().Points.Single().IsAbnormal.Should().BeTrue();
    }

    [Fact]
    public void GroupTrends_Reads_Period_Effective_Start_When_DateTime_Absent()
    {
        // FHIR R4 lets Observation.effective be either dateTime or Period;
        // Cerner's data uses dateTime, but the Period branch is on the
        // contract so it must be exercised by a test.
        var periodObs = new Observation
        {
            Code = Code("2345-7", "Glucose Level"),
            Value = new Quantity { Value = 5m, Unit = "mmol/L" },
            Effective = new Period { Start = "2025-04-01T08:00:00Z" },
        };
        var trends = ResultReviewService.GroupTrends(new[] { periodObs });
        trends.Should().ContainSingle()
            .Which.Points.Single().EffectiveAt!.Value.UtcDateTime
            .Should().Be(new DateTime(2025, 4, 1, 8, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void GroupTrends_Falls_Back_To_Code_Text_When_No_Loinc_Present()
    {
        var anonymous = new Observation
        {
            Code = new CodeableConcept { Text = "Glucose Level" },
            Value = new Quantity { Value = 5m, Unit = "mmol/L" },
            Effective = new FhirDateTime("2026-05-21T00:00:00Z"),
        };
        var trends = ResultReviewService.GroupTrends(new[] { anonymous });
        trends.Single().Loinc.Should().BeNull();
        trends.Single().Key.Should().StartWith("text:");
        trends.Single().Title.Should().Be("Glucose Level");
    }

    [Fact]
    public void ProjectReport_Captures_Title_Category_Issued_And_Status()
    {
        var report = new DiagnosticReport
        {
            Id = "R1",
            Status = DiagnosticReport.DiagnosticReportStatus.Amended,
            Code = new CodeableConcept { Text = "Lipid Panel" },
            Category = new List<CodeableConcept>
            {
                new() { Coding = new List<Coding> { new() { Display = "Laboratory" } } },
            },
            Issued = DateTimeOffset.Parse("2025-01-13T06:46:59Z"),
            Conclusion = "Total cholesterol mildly elevated.",
        };
        var summary = ResultReviewService.ProjectReport(report);
        summary.Title.Should().Be("Lipid Panel");
        summary.Category.Should().Be("Laboratory");
        summary.Status.Should().Be("Amended");
        summary.IssuedAt!.Value.UtcDateTime.Should().Be(new DateTime(2025, 1, 13, 6, 46, 59, DateTimeKind.Utc));
        summary.Conclusion.Should().Be("Total cholesterol mildly elevated.");
    }

    [Fact]
    public void ProjectReport_Defaults_Title_When_Code_Missing()
    {
        var report = new DiagnosticReport
        {
            Id = "R2",
            Status = DiagnosticReport.DiagnosticReportStatus.Final,
        };
        ResultReviewService.ProjectReport(report).Title.Should().Be("Diagnostic report");
    }

    [Fact]
    public void ProjectReport_Uses_Coding_Display_When_Code_Text_Missing()
    {
        var report = new DiagnosticReport
        {
            Status = DiagnosticReport.DiagnosticReportStatus.Final,
            Code = new CodeableConcept
            {
                Coding = new List<Coding> { new() { Display = "Renal Panel" } },
            },
        };
        ResultReviewService.ProjectReport(report).Title.Should().Be("Renal Panel",
            because: "the fallback chain prefers coding[0].display when code.text is empty");
    }

    [Fact]
    public void ProjectReport_Uses_Category_Text_When_No_Coding_Display()
    {
        var report = new DiagnosticReport
        {
            Status = DiagnosticReport.DiagnosticReportStatus.Final,
            Code = new CodeableConcept { Text = "X" },
            Category = new List<CodeableConcept>
            {
                new() { Text = "Pathology", Coding = new List<Coding>() },
            },
        };
        ResultReviewService.ProjectReport(report).Category.Should().Be("Pathology",
            because: "when no Coding.Display is present the fallback is the CodeableConcept.Text");
    }

    [Fact]
    public void GroupTrends_Leaves_Effective_Null_When_FhirDateTime_Is_Unparseable()
    {
        var obs = new Observation
        {
            Code = Code("2345-7", "Glucose Level"),
            Value = new Quantity { Value = 5m, Unit = "mmol/L" },
            Effective = new FhirDateTime("not-a-date"),
        };
        var trend = ResultReviewService.GroupTrends(new[] { obs }).Single();
        trend.Points.Single().EffectiveAt.Should().BeNull();
    }

    [Fact]
    public void GroupTrends_Leaves_Effective_Null_When_Period_Start_Is_Empty()
    {
        var obs = new Observation
        {
            Code = Code("2345-7", "Glucose Level"),
            Value = new Quantity { Value = 5m, Unit = "mmol/L" },
            Effective = new Period { Start = string.Empty },
        };
        var trend = ResultReviewService.GroupTrends(new[] { obs }).Single();
        trend.Points.Single().EffectiveAt.Should().BeNull();
    }

    [Theory]
    [InlineData(AdministrativeGender.Male, "Male")]
    [InlineData(AdministrativeGender.Other, "Other")]
    [InlineData(AdministrativeGender.Unknown, "Other")]
    public void ProjectDemographics_Maps_Gender_Codes(AdministrativeGender g, string expected)
    {
        var patient = new Patient { Id = "p", Gender = g, BirthDate = "1980-01-01" };
        ResultReviewService.ProjectDemographics(patient, "p").AgeSex.Should().EndWith(expected);
    }

    [Fact]
    public void ProjectDemographics_Drops_Age_When_BirthDate_Is_Null()
    {
        // No BirthDate → no "Ny · " prefix on the age/sex string.
        var patient = new Patient { Id = "p", Gender = AdministrativeGender.Female };
        ResultReviewService.ProjectDemographics(patient, "p").AgeSex.Should().Be("Female");
    }

    [Fact]
    public void ProjectDemographics_Drops_Age_When_BirthDate_Is_Malformed()
    {
        var patient = new Patient { Id = "p", Gender = AdministrativeGender.Male, BirthDate = "not-a-date" };
        ResultReviewService.ProjectDemographics(patient, "p").AgeSex.Should().Be("Male");
    }

    [Fact]
    public void ProjectDemographics_Decrements_Age_When_Birthday_Has_Not_Passed_Yet_This_Year()
    {
        // Construct a birth date whose anniversary is tomorrow → age must
        // be (years-diff − 1), exercising the pre-birthday decrement.
        var tomorrow = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1));
        var birthDate = tomorrow.AddYears(-40).ToString("yyyy-MM-dd");
        var patient = new Patient { Id = "p", Gender = AdministrativeGender.Female, BirthDate = birthDate };

        ResultReviewService.ProjectDemographics(patient, "p").AgeSex
            .Should().Be("39y · Female",
                because: "the anniversary hasn't arrived yet this year, so we report 39 not 40");
    }

    [Fact]
    public void ProjectDemographics_Computes_Age_From_BirthDate()
    {
        // Use a BirthDate far enough in the past that a sliding wall-clock
        // doesn't make the age band ambiguous near the user's local birthday.
        var bd = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-40).AddDays(-30));
        var patient = new Patient { Id = "p", BirthDate = bd.ToString("yyyy-MM-dd"), Gender = AdministrativeGender.Female };
        var d = ResultReviewService.ProjectDemographics(patient, "p");
        d.AgeSex.Should().StartWith("40y").And.EndWith("Female");
        d.DateOfBirth.Should().Be(patient.BirthDate);
        d.Mrn.Should().Be("p");
    }

    [Fact]
    public void ProjectDemographics_Falls_Back_When_Patient_Is_Null()
    {
        var d = ResultReviewService.ProjectDemographics(null, "p99");
        d.DisplayName.Should().Be("Patient p99");
        d.AgeSex.Should().BeEmpty();
        d.Mrn.Should().Be("p99");
    }

    [Fact]
    public void SummariseError_Maps_Exception_Types_To_Short_Strings()
    {
        ResultReviewService.SummariseError(
            new FhirOperationException("denied", System.Net.HttpStatusCode.Forbidden))
            .Should().Be("FHIR 403 Forbidden");
        ResultReviewService.SummariseError(new TaskCanceledException()).Should().Be("Timed out");
        ResultReviewService.SummariseError(new HttpRequestException("conn refused")).Should().Be("Network error");
    }

    private static Observation Obs(string loinc, string title, double value, string unit, string at) => new()
    {
        Code = Code(loinc, title),
        Value = new Quantity { Value = (decimal)value, Unit = unit, System = "http://unitsofmeasure.org", Code = unit },
        Effective = new FhirDateTime(at),
    };

    private static CodeableConcept Code(string loinc, string title) => new()
    {
        Text = title,
        Coding = new List<Coding>
        {
            new() { System = "http://loinc.org", Code = loinc, Display = title },
        },
    };
}
