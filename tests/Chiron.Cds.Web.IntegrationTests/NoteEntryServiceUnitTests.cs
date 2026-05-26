using System.Text;
using Chiron.Cds.Engine;
using Chiron.Cds.Web.Configuration;
using Chiron.Cds.Web.FhirClient;
using Chiron.Cds.Web.Panel;
using Chiron.Cds.Web.Tenancy;
using FluentAssertions;
using Hl7.Fhir.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using FhirOperationException = Hl7.Fhir.Rest.FhirOperationException;
using Task = System.Threading.Tasks.Task;

namespace Chiron.Cds.Web.IntegrationTests;

/// <summary>Offline unit tests for the pure helpers on <see cref="NoteEntryService"/>.</summary>
public class NoteEntryServiceUnitTests
{
    [Fact]
    public void ComposeNoteText_Joins_Non_Empty_Sections_With_Heading_Blocks()
    {
        var draft = new NoteDraft(
            Subjective: "Cough for 3 days.",
            Objective: "",
            Assessment: "Acute bronchitis.",
            Plan: "Guaifenesin PRN. Follow up if no improvement in 7 days.");
        var text = NoteEntryService.ComposeNoteText(draft);
        text.Should().Contain("SUBJECTIVE");
        text.Should().Contain("Cough for 3 days.");
        text.Should().NotContain("OBJECTIVE",
            because: "the empty Objective section must not write an empty heading");
        text.Should().Contain("ASSESSMENT");
        text.Should().Contain("Acute bronchitis.");
        text.Should().Contain("PLAN");
        text.Should().Contain("Guaifenesin PRN.");
    }

    [Fact]
    public void ComposeNoteText_Returns_Empty_String_When_All_Sections_Blank()
    {
        var text = NoteEntryService.ComposeNoteText(NoteDraft.Empty);
        text.Should().BeEmpty();
    }

    [Fact]
    public void IsEmpty_True_For_All_Whitespace_Sections()
    {
        NoteEntryService.IsEmpty(NoteDraft.Empty).Should().BeTrue();
        NoteEntryService.IsEmpty(new NoteDraft("   ", "\n\n", "\t", "")).Should().BeTrue();
        NoteEntryService.IsEmpty(new NoteDraft("", "", "", "x")).Should().BeFalse();
    }

    [Fact]
    public void ComposeDraft_Prefills_Assessment_From_Active_Conditions()
    {
        var chart = ChartWith(conditions: new[]
        {
            ConditionFor("Type 2 diabetes mellitus", active: true),
            ConditionFor("Resolved sinusitis", active: false),
            ConditionFor("Type 2 diabetes mellitus", active: true), // duplicate
            ConditionFor("Hypertension", active: true),
        });
        var draft = NoteEntryService.ComposeDraft(chart);
        draft.Assessment.Should().Contain("- Type 2 diabetes mellitus");
        draft.Assessment.Should().Contain("- Hypertension");
        draft.Assessment.Should().NotContain("Resolved sinusitis",
            because: "inactive conditions are excluded from the pre-fill");
        draft.Assessment.Split('\n').Where(l => l.StartsWith("- ")).Should().HaveCount(2,
            because: "duplicates collapse into one line each");
    }

    [Fact]
    public void ComposeDraft_Prefills_Plan_From_Active_Medications()
    {
        var chart = ChartWith(meds: new[]
        {
            MedFor("Metformin 500 mg tablet", MedicationRequest.MedicationrequestStatus.Active),
            MedFor("Discontinued amoxicillin", MedicationRequest.MedicationrequestStatus.Stopped),
            MedFor("Lisinopril 10 mg tablet", MedicationRequest.MedicationrequestStatus.Active),
        });
        var draft = NoteEntryService.ComposeDraft(chart);
        draft.Plan.Should().StartWith("Continue:");
        draft.Plan.Should().Contain("- Metformin 500 mg tablet");
        draft.Plan.Should().Contain("- Lisinopril 10 mg tablet");
        draft.Plan.Should().NotContain("amoxicillin",
            because: "non-active medications are excluded from the Plan pre-fill");
    }

    [Fact]
    public void ComposeDraft_Leaves_Sections_Empty_When_Chart_Has_No_Active_Items()
    {
        var draft = NoteEntryService.ComposeDraft(ChartWith());
        draft.Subjective.Should().BeEmpty();
        draft.Objective.Should().BeEmpty();
        draft.Assessment.Should().BeEmpty();
        draft.Plan.Should().BeEmpty();
    }

    [Fact]
    public void ProjectNoteSummary_Captures_Title_Category_Status_Date()
    {
        var d = new DocumentReference
        {
            Type = new CodeableConcept { Text = "Inpatient Clinical Summary" },
            Category = new List<CodeableConcept>
            {
                new() { Coding = new List<Coding> { new() { Display = "Clinical Note" } } },
            },
            Status = DocumentReferenceStatus.Current,
            Date = DateTimeOffset.Parse("2024-03-15T10:00:00Z"),
        };
        var s = NoteEntryService.ProjectNoteSummary(d);
        s.Title.Should().Be("Inpatient Clinical Summary");
        s.Category.Should().Be("Clinical Note");
        s.Status.Should().Be("Current");
        s.AuthoredAt!.Value.UtcDateTime.Should().Be(new DateTime(2024, 3, 15, 10, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void ProjectNoteSummary_Defaults_Title_When_Type_Is_Empty()
    {
        var d = new DocumentReference { Status = DocumentReferenceStatus.Current };
        NoteEntryService.ProjectNoteSummary(d).Title.Should().Be("Note");
    }

    [Fact]
    public void BuildDocumentReference_Sets_Progress_Note_Loinc_And_Subject_And_Content()
    {
        var draft = new NoteDraft(
            Subjective: "Cough.", Objective: "Lungs clear.",
            Assessment: "URI.", Plan: "Symptomatic care.");
        var d = NoteEntryService.BuildDocumentReference("12674028", draft);
        d.Status.Should().Be(DocumentReferenceStatus.Current);
        d.Type!.Coding.Should().ContainSingle()
            .Which.Code.Should().Be("11506-3",
                because: "the LOINC code for a progress note is the canonical Type discriminator");
        d.Subject!.Reference.Should().Be("Patient/12674028");
        d.Content.Should().ContainSingle();
        var attachment = d.Content[0]!.Attachment;
        attachment.ContentType.Should().Be("text/plain");
        attachment.Data.Should().NotBeNullOrEmpty();
        var roundtrip = Encoding.UTF8.GetString(attachment.Data!);
        roundtrip.Should().Contain("Cough.").And.Contain("Symptomatic care.");
    }

    [Fact]
    public async Task SignAsync_Without_Access_Token_Returns_NotAuthorised()
    {
        var svc = new NoChartFetchStub();
        var result = await svc.SignAsync(
            patientId: "p1",
            draft: new NoteDraft("S", "O", "A", "P"),
            accessToken: null,
            ct: CancellationToken.None);
        result.Status.Should().Be(NoteWriteStatus.NotAuthorised);
        result.WrittenId.Should().BeNull();
        result.Message.Should().BeNull();
    }

    [Fact]
    public async Task SignAsync_Refuses_Empty_Draft_Even_With_Session()
    {
        var svc = new NoChartFetchStub();
        var result = await svc.SignAsync(
            patientId: "p1",
            draft: NoteDraft.Empty,
            accessToken: "any-non-empty",
            ct: CancellationToken.None);
        result.Status.Should().Be(NoteWriteStatus.Failed);
        result.Message.Should().Contain("at least one section",
            because: "writing a completely blank DocumentReference is a guaranteed-bad clinical record");
    }

    [Fact]
    public async Task GetForPatientAsync_Returns_Failure_When_Chart_Fetch_Throws()
    {
        var svc = new FetchFailsStub();
        var page = await svc.GetForPatientAsync("p1", CancellationToken.None);
        page.Error.Should().Be("FHIR 403 Forbidden",
            because: "the outer catch maps FhirOperationException through SummariseError into the page Error");
        page.History.Should().BeEmpty();
        page.Draft.Should().Be(NoteDraft.Empty);
    }

    [Fact]
    public async Task GetForPatientAsync_Renders_Empty_History_When_Only_The_Notes_Search_Fails()
    {
        var svc = new HistorySearchFailsStub();
        var page = await svc.GetForPatientAsync("p1", CancellationToken.None);
        page.Error.Should().BeNull(
            because: "a flaky DocumentReference search must not gate the page — the form stays usable");
        page.History.Should().BeEmpty();
    }

    [Fact]
    public async Task SignAsync_Returns_Ok_When_Write_Succeeds()
    {
        var svc = new WriteStub(id: "DR-test-42");
        var result = await svc.SignAsync(
            patientId: "p1",
            draft: new NoteDraft("Cough.", "", "URI.", "Care."),
            accessToken: "any-non-empty",
            ct: CancellationToken.None);
        result.Status.Should().Be(NoteWriteStatus.Ok);
        result.WrittenId.Should().Be("DR-test-42",
            because: "the server-assigned id from WriteAsync flows back into NoteWriteResult.Ok");
    }

    [Fact]
    public async Task SignAsync_Returns_Failed_With_Fhir_Prefix_When_Write_Throws()
    {
        var svc = new WriteStub(toThrow: new FhirOperationException("denied", System.Net.HttpStatusCode.Forbidden));
        var result = await svc.SignAsync(
            patientId: "p1",
            draft: new NoteDraft("S", "O", "A", "P"),
            accessToken: "any-non-empty",
            ct: CancellationToken.None);
        result.Status.Should().Be(NoteWriteStatus.Failed);
        result.Message.Should().StartWith("FHIR write failed: ");
        result.Message.Should().Contain("403 Forbidden");
    }

    [Fact]
    public void SummariseError_Maps_Exception_Types_To_Short_Strings()
    {
        NoteEntryService.SummariseError(
            new FhirOperationException("denied", System.Net.HttpStatusCode.Forbidden))
            .Should().Be("FHIR 403 Forbidden");
        NoteEntryService.SummariseError(new TaskCanceledException()).Should().Be("Timed out");
        NoteEntryService.SummariseError(new HttpRequestException("boom")).Should().Be("Network error");
    }

    // ---- helpers ----

    private static PatientChart ChartWith(
        IReadOnlyList<Condition>? conditions = null,
        IReadOnlyList<MedicationRequest>? meds = null) => new(
            Patient: new Patient { Id = "p" },
            Conditions: conditions ?? Array.Empty<Condition>(),
            Observations: Array.Empty<Observation>(),
            MedicationRequests: meds ?? Array.Empty<MedicationRequest>(),
            Allergies: Array.Empty<AllergyIntolerance>(),
            Immunizations: Array.Empty<Immunization>(),
            Procedures: Array.Empty<Procedure>(),
            Encounter: null);

    private static Condition ConditionFor(string text, bool active) => new()
    {
        Code = new CodeableConcept { Text = text },
        ClinicalStatus = new CodeableConcept
        {
            Coding = new List<Coding> { new() { Code = active ? "active" : "resolved" } },
        },
    };

    private static MedicationRequest MedFor(string text, MedicationRequest.MedicationrequestStatus status) => new()
    {
        Status = status,
        Medication = new CodeableConcept { Text = text },
    };

    /// <summary>Stub that bypasses the chart fetch so SignAsync can run offline.</summary>
    private class NoChartFetchStub : NoteEntryService
    {
        public NoChartFetchStub() : base(
            BuildTenants(),
            new PatientChartFetcher(NullLogger<PatientChartFetcher>.Instance),
            NullLogger<NoteEntryService>.Instance) { }

        protected static TenantRegistry BuildTenants() => new(Options.Create(new ChironOptions
        {
            DefaultTenant = "t",
            Tenants = new(StringComparer.OrdinalIgnoreCase)
            {
                ["t"] = new TenantOptions
                {
                    DisplayName = "T",
                    ClientId = "c",
                    FhirBaseUrl = "https://auth.test/fhir",
                    FhirOpenBaseUrl = "https://open.test/fhir",
                    Scopes = "",
                },
            },
        }));
    }

    /// <summary>Stub that fails the entire chart/history fetch, exercising the outer catch.</summary>
    private sealed class FetchFailsStub : NoChartFetchStub
    {
        protected override Task<(PatientChart Chart, Bundle? Notes)> FetchAsync(
            TenantConfig tenant, string patientId, CancellationToken ct) =>
            throw new FhirOperationException("boom", System.Net.HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// Stub that lets the chart fetch succeed (with an empty chart) but
    /// fails only the DR-search seam — exercises the inner swallow in
    /// FetchAsync so a flaky history endpoint doesn't gate the form.
    /// </summary>
    private sealed class HistorySearchFailsStub : NoChartFetchStub
    {
        protected override Task<PatientChart> FetchChartAsync(
            TenantConfig tenant, string patientId, CancellationToken ct) =>
            Task.FromResult(EmptyChart());

        protected override Task<Bundle?> SearchNotesAsync(
            TenantConfig tenant, string patientId, CancellationToken ct) =>
            throw new TaskCanceledException("history timed out");

        private static PatientChart EmptyChart() => new(
            Patient: new Hl7.Fhir.Model.Patient { Id = "p" },
            Conditions: Array.Empty<Hl7.Fhir.Model.Condition>(),
            Observations: Array.Empty<Hl7.Fhir.Model.Observation>(),
            MedicationRequests: Array.Empty<Hl7.Fhir.Model.MedicationRequest>(),
            Allergies: Array.Empty<Hl7.Fhir.Model.AllergyIntolerance>(),
            Immunizations: Array.Empty<Hl7.Fhir.Model.Immunization>(),
            Procedures: Array.Empty<Hl7.Fhir.Model.Procedure>(),
            Encounter: null);
    }

    /// <summary>Stub that lets SignAsync's WriteAsync return / throw deterministically.</summary>
    private sealed class WriteStub : NoChartFetchStub
    {
        private readonly Exception? _throw;
        private readonly string _id;
        public WriteStub(Exception? toThrow = null, string id = "DR-test-42")
        {
            _throw = toThrow;
            _id = id;
        }
        protected override Task<string> WriteAsync(DocumentReference resource, string accessToken, CancellationToken ct) =>
            _throw is null ? Task.FromResult(_id) : throw _throw;
    }
}
