using Chiron.Cds.Engine;
using Chiron.Cds.Web.CdsHooks.Models;
using Chiron.Cds.Web.Configuration;
using Chiron.Cds.Web.FhirClient;
using Chiron.Cds.Web.Mappers;
using Chiron.Cds.Web.Panel;
using Chiron.Cds.Web.Tenancy;
using FluentAssertions;
using Hl7.Fhir.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using FhirOperationException = Hl7.Fhir.Rest.FhirOperationException;
using ReasoningEngine = Chiron.Cds.Engine.Engine;
using Task = System.Threading.Tasks.Task;

namespace Chiron.Cds.Web.IntegrationTests;

/// <summary>Offline tests for the pure helpers on <see cref="OrderEntryService"/>.</summary>
public class OrderEntryServiceUnitTests
{
    private static OrderDraft Draft(
        string drug = "metformin",
        string strength = "500 mg",
        string? form = "tablet",
        string? route = "Oral",
        string? frequency = "Twice daily",
        string? quantity = "60 tablets",
        int refills = 3,
        bool asNeeded = false,
        string? prnReason = null,
        string? pharmacyDisplay = "CVS Pharmacy #4521 — sandbox stub",
        bool substitutionAllowed = true,
        string? note = null) => new(
            DrugName: drug, Strength: strength, Form: form, Route: route, Frequency: frequency,
            Quantity: quantity, Refills: refills, AsNeeded: asNeeded, PrnReason: prnReason,
            PharmacyId: "ph", PharmacyDisplay: pharmacyDisplay,
            SubstitutionAllowed: substitutionAllowed, NoteToPharmacist: note);

    [Theory]
    [InlineData("metformin", "500 mg", "tablet", "metformin 500 mg tablet")]
    [InlineData("Lisinopril", "10 MG", "Tablet", "Lisinopril 10 MG tablet")]
    [InlineData("warfarin", "", null, "warfarin")]
    [InlineData("", "", null, "Unspecified medication")]
    public void ComposeMedicationText_Assembles_Drug_Strength_Form(string drug, string strength, string? form, string expected)
    {
        var draft = Draft(drug: drug, strength: strength, form: form);
        OrderEntryService.ComposeMedicationText(draft).Should().Be(expected);
    }

    [Fact]
    public void ComposeSigText_Joins_Strength_Route_Frequency_With_Commas()
    {
        OrderEntryService.ComposeSigText(Draft()).Should().Be("500 mg, Oral, Twice daily");
    }

    [Fact]
    public void ComposeSigText_Appends_As_Needed_When_Prn()
    {
        var d = Draft(asNeeded: true, prnReason: "pain");
        OrderEntryService.ComposeSigText(d).Should().EndWith("— as needed for pain");
    }

    [Fact]
    public void ComposeSigText_Bare_As_Needed_When_Prn_With_No_Reason()
    {
        var d = Draft(asNeeded: true, prnReason: null);
        OrderEntryService.ComposeSigText(d).Should().EndWith("— as needed");
    }

    [Fact]
    public void ComposeSigText_Falls_Back_When_All_Sig_Fields_Empty()
    {
        var d = Draft(strength: "", route: null, frequency: null);
        OrderEntryService.ComposeSigText(d).Should().Be("Take as directed.");
    }

    [Fact]
    public void ProjectDraftMedication_Normalises_Drug_Name_And_Parses_Dose()
    {
        var med = OrderEntryService.ProjectDraftMedication(Draft(drug: "Metformin XR", strength: "500 mg"));
        med.Name.Should().Be("metformin",
            because: "the engine-side name is lower-cased to the first word so rules can match against it");
        med.DoseMg.Should().Be(500);
        med.Route.Should().Be("Oral");
        med.Frequency.Should().Be("Twice daily");
        med.Active.Should().BeTrue();
    }

    [Fact]
    public void ProjectDraftMedication_Leaves_Dose_Null_When_Strength_Has_No_Digits()
    {
        var med = OrderEntryService.ProjectDraftMedication(Draft(strength: "as directed"));
        med.DoseMg.Should().BeNull();
    }

    [Fact]
    public void BuildMedicationRequest_Produces_Order_Intent_With_Subject_And_Substitution()
    {
        var mr = OrderEntryService.BuildMedicationRequest("12674028", Draft());
        mr.Status.Should().Be(MedicationRequest.MedicationrequestStatus.Active);
        mr.Intent.Should().Be(MedicationRequest.MedicationRequestIntent.Order);
        mr.Subject.Reference.Should().Be("Patient/12674028");
        mr.Medication.Should().BeOfType<CodeableConcept>()
            .Which.Text.Should().Be("metformin 500 mg tablet");
        mr.DosageInstruction.Should().HaveCount(1);
        mr.DosageInstruction[0].Text.Should().Be("500 mg, Oral, Twice daily");
        ((FhirBoolean)mr.DosageInstruction[0].AsNeeded!).Value.Should().BeFalse();
        mr.DispenseRequest!.NumberOfRepeatsAllowed.Should().Be(3);
        mr.DispenseRequest.Performer!.Display.Should().Be("CVS Pharmacy #4521 — sandbox stub");
        ((FhirBoolean)mr.Substitution!.Allowed).Value.Should().BeTrue();
    }

    [Fact]
    public void BuildMedicationRequest_Omits_Pharmacy_Reference_When_None_Selected()
    {
        var draft = Draft() with { PharmacyDisplay = null };
        var mr = OrderEntryService.BuildMedicationRequest("p", draft);
        mr.DispenseRequest!.Performer.Should().BeNull();
    }

    [Fact]
    public void BuildMedicationRequest_Adds_Note_When_Present()
    {
        var draft = Draft(note: "Patient prefers chewables.");
        var mr = OrderEntryService.BuildMedicationRequest("p", draft);
        var notes = mr.Note;
        Assert.NotNull(notes);
        notes.Should().HaveCount(1);
        // Annotation.Text on Firely R4 is a primitive whose ToString() yields
        // the underlying markdown string; comparing via ToString sidesteps the
        // version-dependent property name (Value vs implicit conversion).
        notes[0]!.Text!.ToString().Should().Be("Patient prefers chewables.");
    }

    [Fact]
    public void BuildMedicationRequest_Omits_Note_When_Blank()
    {
        var draft = Draft(note: "");
        var mr = OrderEntryService.BuildMedicationRequest("p", draft);
        mr.Note.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task SignAsync_With_Two_Unacked_Critical_Cards_Returns_Pluralised_Blocked_Message()
    {
        // Drives the real `SignAsync` plural branch ('Acknowledge 2 critical
        // alerts to sign.'). A stub overrides EvaluateAsync to return two
        // critical cards so the gating logic in SignAsync runs unchanged.
        var svc = new TwoCriticalStub();
        var result = await svc.SignAsync(
            patientId: "p1",
            draft: Draft(),
            accessToken: "any-non-empty",
            acknowledgedFingerprints: new HashSet<string>(StringComparer.Ordinal),
            ct: CancellationToken.None);
        result.Status.Should().Be(OrderWriteStatus.Blocked);
        result.Message.Should().Be("Acknowledge 2 critical alerts to sign.",
            because: "two unacked criticals must produce the plural noun and the count 2");
    }

    [Fact]
    public async Task SignAsync_With_One_Unacked_Critical_Card_Returns_Singular_Blocked_Message()
    {
        var svc = new OneCriticalStub();
        var result = await svc.SignAsync(
            patientId: "p1",
            draft: Draft(),
            accessToken: "any-non-empty",
            acknowledgedFingerprints: new HashSet<string>(StringComparer.Ordinal),
            ct: CancellationToken.None);
        result.Status.Should().Be(OrderWriteStatus.Blocked);
        result.Message.Should().Be("Acknowledge 1 critical alert to sign.",
            because: "one unacked critical must use the singular noun (no trailing s)");
    }

    [Fact]
    public async Task SignAsync_Returns_Failed_When_Chart_Cannot_Be_Loaded()
    {
        var svc = new ChartErrorStub();
        var result = await svc.SignAsync(
            patientId: "p1",
            draft: Draft(),
            accessToken: "any-non-empty",
            acknowledgedFingerprints: new HashSet<string>(StringComparer.Ordinal),
            ct: CancellationToken.None);
        result.Status.Should().Be(OrderWriteStatus.Failed);
        result.Message.Should().StartWith("Could not load chart: ",
            because: "the chart-error short-circuit prefixes the engine's summary so the user sees what went wrong");
        result.Message.Should().Contain("boom");
    }

    [Fact]
    public async Task SignAsync_Without_Access_Token_Returns_NotAuthorised()
    {
        // No SMART session = honest "not authorised" — never a synthesised
        // FHIR-JSON dump or any other dev-toolbox surface.
        var svc = new NoCardsStub();
        var result = await svc.SignAsync(
            patientId: "p1",
            draft: Draft(),
            accessToken: null,
            acknowledgedFingerprints: new HashSet<string>(StringComparer.Ordinal),
            ct: CancellationToken.None);
        result.Status.Should().Be(OrderWriteStatus.NotAuthorised);
        result.Cards.Should().BeEmpty();
        result.Message.Should().BeNull();
        result.WrittenId.Should().BeNull();
    }

    [Fact]
    public void SummariseError_Maps_Exception_Types_To_Short_Strings()
    {
        OrderEntryService.SummariseError(
            new FhirOperationException("denied", System.Net.HttpStatusCode.Forbidden))
            .Should().Be("FHIR 403 Forbidden");
        OrderEntryService.SummariseError(new TaskCanceledException()).Should().Be("Timed out");
        OrderEntryService.SummariseError(new HttpRequestException("boom")).Should().Be("Network error");
    }

    /// <summary>Stubs <see cref="OrderEntryService.EvaluateAsync"/> so SignAsync's gating logic can run offline.</summary>
    private abstract class FixedCardsStub : OrderEntryService
    {
        protected FixedCardsStub() : base(
            BuildTenants(),
            new PatientChartFetcher(NullLogger<PatientChartFetcher>.Instance),
            new FhirToFactMapper(NullLogger<FhirToFactMapper>.Instance),
            new AlertToCdsCardMapper(),
            new ReasoningEngine(),
            Options.Create(new PharmacyOptions()),
            NullLogger<OrderEntryService>.Instance)
        { }

        protected abstract IReadOnlyList<CdsCard> Cards { get; }

        public override Task<OrderEvaluation> EvaluateAsync(string patientId, OrderDraft draft, CancellationToken ct) =>
            Task.FromResult(new OrderEvaluation(Cards, ChartError: null));

        private static TenantRegistry BuildTenants() => new(Options.Create(new ChironOptions
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

    private sealed class TwoCriticalStub : FixedCardsStub
    {
        protected override IReadOnlyList<CdsCard> Cards { get; } = new[]
        {
            new CdsCard("Critical A", "critical",
                new CdsCardSource("CDS"), "stop A", "fp-a", Array.Empty<CdsCoding>()),
            new CdsCard("Critical B", "critical",
                new CdsCardSource("CDS"), "stop B", "fp-b", Array.Empty<CdsCoding>()),
        };
    }

    private sealed class OneCriticalStub : FixedCardsStub
    {
        protected override IReadOnlyList<CdsCard> Cards { get; } = new[]
        {
            new CdsCard("Critical Only", "critical",
                new CdsCardSource("CDS"), "stop", "fp", Array.Empty<CdsCoding>()),
        };
    }

    private sealed class NoCardsStub : FixedCardsStub
    {
        protected override IReadOnlyList<CdsCard> Cards { get; } = Array.Empty<CdsCard>();
    }

    /// <summary>Stubs EvaluateAsync to return a chart-load error so SignAsync's failure branch runs.</summary>
    private sealed class ChartErrorStub : FixedCardsStub
    {
        protected override IReadOnlyList<CdsCard> Cards { get; } = Array.Empty<CdsCard>();
        public override Task<OrderEvaluation> EvaluateAsync(string patientId, OrderDraft draft, CancellationToken ct) =>
            Task.FromResult(OrderEvaluation.ChartLoadFailed("boom"));
    }
}
