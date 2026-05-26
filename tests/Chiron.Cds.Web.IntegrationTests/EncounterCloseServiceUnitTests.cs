using Chiron.Cds.Web.Configuration;
using Chiron.Cds.Web.Panel;
using Chiron.Cds.Web.Tenancy;
using FluentAssertions;
using Hl7.Fhir.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using FhirOperationException = Hl7.Fhir.Rest.FhirOperationException;
using Task = System.Threading.Tasks.Task;

namespace Chiron.Cds.Web.IntegrationTests;

/// <summary>Offline unit tests for <see cref="EncounterCloseService"/>.</summary>
public class EncounterCloseServiceUnitTests
{
    [Fact]
    public void ApplyClose_Marks_Status_Finished_And_Stamps_Period_End()
    {
        var enc = new Encounter
        {
            Status = Encounter.EncounterStatus.InProgress,
            Period = new Period { Start = "2026-05-25T10:00:00Z" },
        };
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        EncounterCloseService.ApplyClose(enc);
        enc.Status.Should().Be(Encounter.EncounterStatus.Finished);
        enc.Period!.Start.Should().Be("2026-05-25T10:00:00Z",
            because: "start time is preserved");
        DateTimeOffset.TryParse(enc.Period.End, out var endParsed).Should().BeTrue();
        endParsed.Should().BeOnOrAfter(before,
            because: "end timestamp is fresh — within a second of close-time");
    }

    [Fact]
    public void ApplyClose_Creates_Period_When_Encounter_Had_None()
    {
        var enc = new Encounter { Status = Encounter.EncounterStatus.Planned };
        EncounterCloseService.ApplyClose(enc);
        enc.Period.Should().NotBeNull();
        enc.Period!.End.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void ApplyClose_Preserves_Existing_End_If_Already_Set()
    {
        var enc = new Encounter
        {
            Status = Encounter.EncounterStatus.InProgress,
            Period = new Period { End = "2025-01-01T00:00:00Z" },
        };
        EncounterCloseService.ApplyClose(enc);
        enc.Period!.End.Should().Be("2025-01-01T00:00:00Z",
            because: "an explicit end time on the resource is honored rather than overwritten");
    }

    [Fact]
    public void ProjectSummary_Captures_Type_Class_Status_Period()
    {
        var e = new Encounter
        {
            Id = "97959298",
            Status = Encounter.EncounterStatus.InProgress,
            Class = new Coding { Display = "ambulatory", Code = "AMB" },
            Type = new List<CodeableConcept> { new() { Text = "Outpatient" } },
            Period = new Period { Start = "2026-05-25T09:00:00Z", End = "2026-05-25T09:30:00Z" },
        };
        var s = EncounterCloseService.ProjectSummary(e);
        s.EncounterId.Should().Be("97959298");
        s.Type.Should().Be("Outpatient");
        s.Class.Should().Be("ambulatory");
        s.Status.Should().Be("InProgress");
        s.PeriodStart!.Value.UtcDateTime.Should().Be(new DateTime(2026, 5, 25, 9, 0, 0, DateTimeKind.Utc));
        s.PeriodEnd!.Value.UtcDateTime.Should().Be(new DateTime(2026, 5, 25, 9, 30, 0, DateTimeKind.Utc));
        s.IsInProgress.Should().BeTrue();
    }

    [Fact]
    public void ProjectSummary_Defaults_Type_When_Encounter_Has_None()
    {
        var e = new Encounter { Status = Encounter.EncounterStatus.Finished };
        EncounterCloseService.ProjectSummary(e).Type.Should().Be("Encounter");
    }

    [Fact]
    public async Task CloseAsync_Without_Token_Returns_NotAuthorised()
    {
        var svc = new NoNetworkStub();
        var result = await svc.CloseAsync("p1", "enc-1", accessToken: null, CancellationToken.None);
        result.Status.Should().Be(EncounterCloseStatus.NotAuthorised);
    }

    [Fact]
    public async Task CloseAsync_Returns_NotFound_Message_When_Encounter_Read_Returns_Null()
    {
        var svc = new ReadStub(read: null);
        var result = await svc.CloseAsync("p1", "missing", accessToken: "tok", CancellationToken.None);
        result.Status.Should().Be(EncounterCloseStatus.Failed);
        result.Message.Should().Contain("not found");
    }

    [Fact]
    public async Task CloseAsync_Rejects_Encounter_Belonging_To_Different_Patient()
    {
        var enc = new Encounter
        {
            Id = "e1",
            Status = Encounter.EncounterStatus.InProgress,
            Subject = new ResourceReference("Patient/other"),
        };
        var svc = new ReadStub(read: enc);
        var result = await svc.CloseAsync("p1", "e1", accessToken: "tok", CancellationToken.None);
        result.Status.Should().Be(EncounterCloseStatus.Failed);
        result.Message.Should().Contain("does not belong to this patient",
            because: "encounter ownership is checked before any write attempt");
    }

    [Fact]
    public async Task CloseAsync_Returns_AlreadyClosed_When_Status_Is_Finished()
    {
        var enc = new Encounter
        {
            Id = "e1",
            Status = Encounter.EncounterStatus.Finished,
            Subject = new ResourceReference("Patient/p1"),
        };
        var svc = new ReadStub(read: enc);
        var result = await svc.CloseAsync("p1", "e1", accessToken: "tok", CancellationToken.None);
        result.Status.Should().Be(EncounterCloseStatus.AlreadyClosed);
    }

    [Fact]
    public async Task CloseAsync_Writes_And_Returns_Ok_With_Server_Id()
    {
        var enc = new Encounter
        {
            Id = "e1",
            Status = Encounter.EncounterStatus.InProgress,
            Subject = new ResourceReference("Patient/p1"),
        };
        var svc = new ReadStub(read: enc, writeReturnId: "e1-v2");
        var result = await svc.CloseAsync("p1", "e1", accessToken: "tok", CancellationToken.None);
        result.Status.Should().Be(EncounterCloseStatus.Ok);
        result.UpdatedId.Should().Be("e1-v2",
            because: "the server-assigned id flows back from WriteAsync into the Ok result");
    }

    [Fact]
    public async Task CloseAsync_Returns_Failed_With_Fhir_Prefix_When_Write_Throws()
    {
        var enc = new Encounter
        {
            Id = "e1",
            Status = Encounter.EncounterStatus.InProgress,
            Subject = new ResourceReference("Patient/p1"),
        };
        var svc = new ReadStub(read: enc,
            writeThrow: new FhirOperationException("denied", System.Net.HttpStatusCode.Forbidden));
        var result = await svc.CloseAsync("p1", "e1", accessToken: "tok", CancellationToken.None);
        result.Status.Should().Be(EncounterCloseStatus.Failed);
        result.Message.Should().StartWith("FHIR update failed: ");
        result.Message.Should().Contain("403 Forbidden");
    }

    [Fact]
    public async Task GetForPatientAsync_Returns_Failure_When_Search_Throws()
    {
        var svc = new SearchFailsStub();
        var page = await svc.GetForPatientAsync("p1", CancellationToken.None);
        page.Error.Should().Be("Timed out");
        page.Encounters.Should().BeEmpty();
    }

    [Fact]
    public async Task GetForPatientAsync_Returns_Encounters_Sorted_By_Most_Recent_Start()
    {
        var svc = new SearchStub(new[]
        {
            EncounterAt("e-old", "2020-01-01T00:00:00Z"),
            EncounterAt("e-new", "2026-05-25T09:00:00Z"),
            EncounterAt("e-mid", "2024-06-01T00:00:00Z"),
        });
        var page = await svc.GetForPatientAsync("p1", CancellationToken.None);
        page.Encounters.Select(e => e.EncounterId).Should().ContainInOrder("e-new", "e-mid", "e-old");
    }

    private static Encounter EncounterAt(string id, string start) => new()
    {
        Id = id,
        Status = Encounter.EncounterStatus.InProgress,
        Class = new Coding { Display = "ambulatory" },
        Type = new List<CodeableConcept> { new() { Text = "Outpatient" } },
        Period = new Period { Start = start },
    };

    [Fact]
    public void SummariseError_Maps_Exception_Types_To_Short_Strings()
    {
        EncounterCloseService.SummariseError(
            new FhirOperationException("denied", System.Net.HttpStatusCode.Forbidden))
            .Should().Be("FHIR 403 Forbidden");
        EncounterCloseService.SummariseError(new TaskCanceledException()).Should().Be("Timed out");
        EncounterCloseService.SummariseError(new HttpRequestException("boom")).Should().Be("Network error");
    }

    // ---- helpers ----

    private class NoNetworkStub : EncounterCloseService
    {
        public NoNetworkStub() : base(BuildTenants(), NullLogger<EncounterCloseService>.Instance) { }

        protected static TenantRegistry BuildTenants() => new(Options.Create(new ChironOptions
        {
            DefaultTenant = "t",
            Tenants = new(StringComparer.OrdinalIgnoreCase)
            {
                ["t"] = new TenantOptions
                {
                    DisplayName = "T", ClientId = "c",
                    FhirBaseUrl = "https://auth.test/fhir",
                    FhirOpenBaseUrl = "https://open.test/fhir",
                    Scopes = "",
                },
            },
        }));
    }

    private sealed class ReadStub : NoNetworkStub
    {
        private readonly Encounter? _read;
        private readonly Exception? _writeThrow;
        private readonly string _writeReturnId;

        public ReadStub(Encounter? read, Exception? writeThrow = null, string writeReturnId = "e-default")
        {
            _read = read;
            _writeThrow = writeThrow;
            _writeReturnId = writeReturnId;
        }

        protected override Task<Encounter?> FetchEncounterAsync(string encounterId, string accessToken, CancellationToken ct) =>
            Task.FromResult(_read);

        protected override Task<Encounter> WriteAsync(Encounter encounter, string accessToken, CancellationToken ct)
        {
            if (_writeThrow is not null) throw _writeThrow;
            encounter.Id = _writeReturnId;
            return Task.FromResult(encounter);
        }
    }

    private sealed class SearchStub : NoNetworkStub
    {
        private readonly Bundle _bundle;
        public SearchStub(IEnumerable<Encounter> encounters)
        {
            _bundle = new Bundle
            {
                Entry = encounters.Select(e => new Bundle.EntryComponent { Resource = e }).ToList(),
            };
        }
        protected override Task<Bundle?> SearchEncountersAsync(TenantConfig tenant, string patientId, CancellationToken ct) =>
            Task.FromResult<Bundle?>(_bundle);
    }

    private sealed class SearchFailsStub : NoNetworkStub
    {
        protected override Task<Bundle?> SearchEncountersAsync(TenantConfig tenant, string patientId, CancellationToken ct) =>
            throw new TaskCanceledException("search timed out");
    }
}
