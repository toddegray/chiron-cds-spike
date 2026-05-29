using System.Net;
using Chiron.Cds.Web.Configuration;
using Chiron.Cds.Web.FhirClient;
using Chiron.Cds.Web.Panel;
using Chiron.Cds.Web.Tenancy;
using FluentAssertions;
using Hl7.Fhir.Model;
using Hl7.Fhir.Rest;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Task = System.Threading.Tasks.Task;

namespace Chiron.Cds.Web.IntegrationTests;

/// <summary>
/// Offline behaviour tests for <see cref="PatientSearchService.SearchAsync"/>
/// — the short-circuits and the friendly-warning surface. The network path
/// is covered live in <c>PanelControllerLiveTests</c>; this class pins the
/// branches a unit harness can reach.
/// </summary>
public class PatientSearchServiceBehaviourTests
{
    private static TenantRegistry BuildRegistry()
    {
        var options = Options.Create(new ChironOptions
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
        });
        return new TenantRegistry(options);
    }

    private static PatientSearchService Build() =>
        new(new StubReadConnection(BuildRegistry().Default), NullLogger<PatientSearchService>.Instance);

    private static PatientSearchCriteria ByName(string name, string? dob = null) =>
        new(Name: name, BirthDate: dob, Mrn: null, EncounterId: null);

    [Fact]
    public async Task Empty_Criteria_Returns_Empty_Result_With_No_Warning()
    {
        var result = await Build().SearchAsync(new PatientSearchCriteria(null, null, null, null), CancellationToken.None);
        result.Hits.Should().BeEmpty();
        result.Warning.Should().BeNull(
            because: "an empty form is a normal landing state, not an error worth warning about");
    }

    [Fact]
    public async Task Whitespace_Only_Criteria_Treated_As_Empty()
    {
        var result = await Build().SearchAsync(ByName("   \t  "), CancellationToken.None);
        result.Hits.Should().BeEmpty();
        result.Warning.Should().BeNull();
    }

    [Fact]
    public async Task Single_Character_Name_Returns_TooShort_Warning_Without_Hitting_The_Wire()
    {
        // If this test reaches the (unreachable) open.test FHIR endpoint
        // it will block for ~12 s. Returning fast proves the guard fires
        // before any network call.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var result = await Build().SearchAsync(ByName("a"), cts.Token);
        result.Hits.Should().BeEmpty();
        result.Warning.Should().NotBeNullOrEmpty();
        result.Warning.Should().Contain("at least 2 characters");
    }

    [Fact]
    public async Task Name_Is_Trimmed_For_Min_Length_Check()
    {
        var result = await Build().SearchAsync(ByName("  a  "), CancellationToken.None);
        result.Warning.Should().Contain("at least 2 characters",
            because: "trimming happens before length is measured — '  a  ' is effectively 'a'");
    }

    [Fact]
    public async Task Name_Without_Date_Of_Birth_Warns()
    {
        var result = await Build().SearchAsync(ByName("Lopez"), CancellationToken.None);
        result.Hits.Should().BeEmpty();
        result.Warning.Should().Contain("date of birth",
            because: "the sandbox rejects a bare name search, so a date of birth is required");
    }

    [Fact]
    public async Task Timeout_From_Linked_Cts_Surfaces_TimedOut_Warning()
    {
        // ThrowingStub raises OperationCanceledException as if the linked
        // timeout fired. The outer ct is NOT cancelled so the `when` filter
        // matches and the catch returns the friendly warning.
        var svc = new ThrowingStub(() => new OperationCanceledException("simulated timeout"));
        var result = await svc.SearchAsync(ByName("smith", "1980-01-01"), CancellationToken.None);
        result.Hits.Should().BeEmpty();
        result.Warning.Should().NotBeNull().And.Contain("timed out");
    }

    [Fact]
    public async Task HttpRequestException_Surfaces_Failed_Warning()
    {
        var svc = new ThrowingStub(() => new HttpRequestException("connection refused"));
        var result = await svc.SearchAsync(ByName("smith", "1980-01-01"), CancellationToken.None);
        result.Hits.Should().BeEmpty();
        result.Warning.Should().NotBeNull().And.Contain("Search failed");
    }

    [Fact]
    public async Task FhirOperationException_Surfaces_Failed_Warning()
    {
        var svc = new ThrowingStub(() => new FhirOperationException("server boom", HttpStatusCode.InternalServerError));
        var result = await svc.SearchAsync(ByName("smith", "1980-01-01"), CancellationToken.None);
        result.Hits.Should().BeEmpty();
        result.Warning.Should().NotBeNull().And.Contain("Search failed");
    }

    [Fact]
    public async Task Pre_Cancelled_Caller_Token_Propagates_OperationCanceledException()
    {
        // When the caller's own token is cancelled the `when (!ct.IsCancellationRequested)`
        // filter on the timeout catch must reject — caller cancellation
        // should propagate, not be transformed into a "timed out" warning.
        var svc = new ThrowingStub(() => new OperationCanceledException("matches caller cancel"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Func<Task> act = () => svc.SearchAsync(ByName("smith", "1980-01-01"), cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Mrn_Routes_To_Identifier_Search()
    {
        var stub = new CapturingStub();
        await stub.SearchAsync(new PatientSearchCriteria(Name: null, BirthDate: null, Mrn: "203713", EncounterId: null), CancellationToken.None);
        stub.CapturedCriteria.Should().Contain("identifier=203713",
            because: "an MRN is looked up via the FHIR identifier search parameter");
    }

    [Fact]
    public async Task Name_And_Dob_Route_To_Family_Birthdate_Search()
    {
        var stub = new CapturingStub();
        await stub.SearchAsync(new PatientSearchCriteria(Name: "Camila Lopez", BirthDate: "1987-09-12", Mrn: null, EncounterId: null), CancellationToken.None);
        stub.CapturedCriteria.Should().Contain("family=Lopez")
            .And.Contain("given=Camila")
            .And.Contain("birthdate=1987-09-12");
    }

    [Fact]
    public async Task Encounter_Id_Routes_To_Encounter_Resolve_And_Returns_The_Patient()
    {
        var patient = new Patient { Id = "p1", Name = { new HumanName { Family = "Lopez", Given = new[] { "Camila" } } } };
        var stub = new CapturingStub(patient);
        var result = await stub.SearchAsync(new PatientSearchCriteria(Name: null, BirthDate: null, Mrn: null, EncounterId: "enc-1"), CancellationToken.None);
        stub.CapturedEncounterId.Should().Be("enc-1");
        result.Hits.Should().ContainSingle().Which.PatientId.Should().Be("p1");
    }

    [Theory]
    [InlineData("Lopez", "Lopez", null)]
    [InlineData("Camila Lopez", "Lopez", "Camila")]
    [InlineData("Lopez, Camila", "Lopez", "Camila")]
    [InlineData("Mary Anne Lopez", "Lopez", "Mary Anne")]
    public void SplitName_Parses_Family_And_Given(string input, string expectedFamily, string? expectedGiven)
    {
        var (family, given) = PatientSearchService.SplitName(input);
        family.Should().Be(expectedFamily);
        given.Should().Be(expectedGiven);
    }

    /// <summary>
    /// Stub that records the FHIR criteria / encounter id a search routes to,
    /// so tests can assert the strategy selection without a real server.
    /// </summary>
    private sealed class CapturingStub : PatientSearchService
    {
        public string[]? CapturedCriteria;
        public string? CapturedEncounterId;
        private readonly Patient? _encounterPatient;

        public CapturingStub(Patient? encounterPatient = null)
            : base(new StubReadConnection(BuildRegistry().Default), NullLogger<PatientSearchService>.Instance)
        {
            _encounterPatient = encounterPatient;
        }

        protected override Task<Bundle?> ExecutePatientSearchAsync(
            TenantConfig tenant, string? accessToken, string[] criteria, CancellationToken ct)
        {
            CapturedCriteria = criteria;
            return Task.FromResult<Bundle?>(new Bundle { Entry = new List<Bundle.EntryComponent>() });
        }

        protected override Task<Patient?> ResolveEncounterPatientAsync(
            TenantConfig tenant, string? accessToken, string encounterId, CancellationToken ct)
        {
            CapturedEncounterId = encounterId;
            return Task.FromResult(_encounterPatient);
        }
    }

    /// <summary>
    /// Stub <see cref="PatientSearchService"/> that lets a test trigger the
    /// timeout / failure catches deterministically by throwing whatever
    /// exception the supplied factory produces.
    /// </summary>
    private sealed class ThrowingStub : PatientSearchService
    {
        private readonly Func<Exception> _exceptionFactory;
        public ThrowingStub(Func<Exception> exceptionFactory)
            : base(new StubReadConnection(BuildRegistry().Default), NullLogger<PatientSearchService>.Instance)
        {
            _exceptionFactory = exceptionFactory;
        }

        protected override Task<Bundle?> ExecutePatientSearchAsync(
            TenantConfig tenant, string? accessToken, string[] criteria, CancellationToken ct) =>
            throw _exceptionFactory();
    }
}
