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

/// <summary>Offline unit tests for <see cref="ServiceRequestService"/>.</summary>
public class ServiceRequestServiceUnitTests
{
    [Theory]
    [InlineData(ServiceRequestCategory.Laboratory, "Lipid Panel", "108252007", "Laboratory procedure")]
    [InlineData(ServiceRequestCategory.Imaging, "Chest X-ray PA/LAT", "363679005", "Imaging")]
    public void BuildServiceRequest_Sets_Category_Subject_Code_And_Intent(
        ServiceRequestCategory category, string orderText, string expectedCode, string expectedDisplay)
    {
        var draft = new ServiceRequestDraft(orderText, Reason: "follow-up", Priority: "routine");
        var sr = ServiceRequestService.BuildServiceRequest("12674028", draft, category);
        sr.Status.Should().Be(RequestStatus.Active);
        sr.Intent.Should().Be(RequestIntent.Order);
        sr.Priority.Should().Be(RequestPriority.Routine);
        sr.Subject!.Reference.Should().Be("Patient/12674028");
        sr.Code!.Text.Should().Be(orderText);
        sr.Category.Should().ContainSingle()
            .Which.Coding.Should().ContainSingle()
            .Which.Code.Should().Be(expectedCode,
                because: $"the FHIR/SNOMED CT code for {expectedDisplay} is {expectedCode}");
        sr.ReasonCode.Should().ContainSingle()
            .Which.Text.Should().Be("follow-up");
    }

    [Fact]
    public void BuildServiceRequest_Omits_ReasonCode_When_Reason_Blank()
    {
        var draft = new ServiceRequestDraft("CBC", Reason: "", Priority: null);
        var sr = ServiceRequestService.BuildServiceRequest("p", draft, ServiceRequestCategory.Laboratory);
        sr.ReasonCode.Should().BeNullOrEmpty();
        sr.Priority.Should().BeNull(
            because: "no priority on the draft means no Priority element on the resource");
    }

    [Theory]
    [InlineData("routine", RequestPriority.Routine)]
    [InlineData("urgent", RequestPriority.Urgent)]
    [InlineData("stat", RequestPriority.Stat)]
    [InlineData("STAT", RequestPriority.Stat)]
    [InlineData("nope", null)]
    public void BuildServiceRequest_Maps_Priority_String(string priorityIn, RequestPriority? expected)
    {
        var draft = new ServiceRequestDraft("CBC", null, priorityIn);
        var sr = ServiceRequestService.BuildServiceRequest("p", draft, ServiceRequestCategory.Laboratory);
        sr.Priority.Should().Be(expected);
    }

    [Theory]
    [InlineData("Laboratory procedure", ServiceRequestCategory.Laboratory, true)]
    [InlineData("Imaging", ServiceRequestCategory.Imaging, true)]
    [InlineData("Patient Care", ServiceRequestCategory.Laboratory, false)]
    [InlineData("Surgical", ServiceRequestCategory.Imaging, false)]
    public void IsCategory_Matches_By_Keyword_In_Text_Or_Display(string categoryText, ServiceRequestCategory askedFor, bool expected)
    {
        var sr = new ServiceRequest
        {
            Category = new List<CodeableConcept> { new() { Text = categoryText } },
        };
        ServiceRequestService.IsCategory(sr, askedFor).Should().Be(expected);
    }

    [Fact]
    public void IsCategory_Matches_By_Coding_Display_When_Text_Absent()
    {
        var sr = new ServiceRequest
        {
            Category = new List<CodeableConcept>
            {
                new() { Coding = new List<Coding> { new() { Display = "Imaging procedure" } } },
            },
        };
        ServiceRequestService.IsCategory(sr, ServiceRequestCategory.Imaging).Should().BeTrue();
    }

    [Fact]
    public void ProjectSummary_Captures_Name_Status_Priority_Reason_Occurrence()
    {
        var sr = new ServiceRequest
        {
            Status = RequestStatus.Active,
            Priority = RequestPriority.Urgent,
            Code = new CodeableConcept { Text = "MRI brain w/o contrast" },
            ReasonCode = new List<CodeableConcept> { new() { Text = "persistent headache" } },
            Occurrence = new FhirDateTime("2026-04-15T08:00:00Z"),
        };
        var s = ServiceRequestService.ProjectSummary(sr);
        s.Name.Should().Be("MRI brain w/o contrast");
        s.Status.Should().Be("Active");
        s.Priority.Should().Be("Urgent");
        s.Reason.Should().Be("persistent headache");
        s.OccurrenceAt!.Value.UtcDateTime.Should().Be(new DateTime(2026, 4, 15, 8, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void ProjectSummary_Defaults_Name_When_Code_Empty()
    {
        var sr = new ServiceRequest { Status = RequestStatus.Active };
        ServiceRequestService.ProjectSummary(sr).Name.Should().Be("Order");
    }

    [Fact]
    public async Task SignAsync_Without_Order_Text_Returns_Failed()
    {
        var svc = new NoNetworkStub();
        var result = await svc.SignAsync("p", new ServiceRequestDraft("  ", null, null),
            ServiceRequestCategory.Laboratory, "tok", CancellationToken.None);
        result.Status.Should().Be(ServiceRequestWriteStatus.Failed);
        result.Message.Should().Contain("test or procedure");
    }

    [Fact]
    public async Task SignAsync_Without_Token_Returns_NotAuthorised()
    {
        var svc = new NoNetworkStub();
        var result = await svc.SignAsync("p", new ServiceRequestDraft("CBC", null, null),
            ServiceRequestCategory.Laboratory, accessToken: null, CancellationToken.None);
        result.Status.Should().Be(ServiceRequestWriteStatus.NotAuthorised);
    }

    [Fact]
    public async Task SignAsync_Returns_Ok_With_Server_Id_When_Write_Succeeds()
    {
        var svc = new WriteStub(id: "sr-99");
        var result = await svc.SignAsync("p1", new ServiceRequestDraft("Chest X-ray", "cough", "routine"),
            ServiceRequestCategory.Imaging, accessToken: "tok", CancellationToken.None);
        result.Status.Should().Be(ServiceRequestWriteStatus.Ok);
        result.WrittenId.Should().Be("sr-99");
    }

    [Fact]
    public async Task SignAsync_Returns_Failed_With_Fhir_Prefix_When_Write_Throws()
    {
        var svc = new WriteStub(toThrow: new FhirOperationException("denied", System.Net.HttpStatusCode.Forbidden));
        var result = await svc.SignAsync("p", new ServiceRequestDraft("CBC", null, null),
            ServiceRequestCategory.Laboratory, "tok", CancellationToken.None);
        result.Status.Should().Be(ServiceRequestWriteStatus.Failed);
        result.Message.Should().StartWith("FHIR write failed: ").And.Contain("403 Forbidden");
    }

    [Fact]
    public async Task GetForPatientAsync_Filters_By_Category_And_Orders_By_Most_Recent()
    {
        var svc = new SearchStub(new[]
        {
            Sr("old-lab", "Laboratory procedure", "2020-01-01T00:00:00Z"),
            Sr("new-lab", "Laboratory procedure", "2026-04-15T08:00:00Z"),
            Sr("imaging", "Imaging", "2025-03-01T08:00:00Z"),
        });
        var lab = await svc.GetForPatientAsync("p", ServiceRequestCategory.Laboratory, CancellationToken.None);
        lab.History.Select(h => h.Name).Should().ContainInOrder("new-lab", "old-lab");
        lab.History.Should().NotContain(h => h.Name == "imaging",
            because: "the imaging entry must not appear in the lab category filter");
    }

    [Fact]
    public async Task GetForPatientAsync_Returns_Failure_On_Search_Exception()
    {
        var svc = new SearchFailsStub();
        var page = await svc.GetForPatientAsync("p", ServiceRequestCategory.Laboratory, CancellationToken.None);
        page.Error.Should().Be("Timed out");
        page.History.Should().BeEmpty();
    }

    private static ServiceRequest Sr(string text, string categoryText, string when) => new()
    {
        Status = RequestStatus.Active,
        Code = new CodeableConcept { Text = text },
        Category = new List<CodeableConcept> { new() { Text = categoryText } },
        Occurrence = new FhirDateTime(when),
    };

    private class NoNetworkStub : ServiceRequestService
    {
        public NoNetworkStub() : base(BuildTenants(), NullLogger<ServiceRequestService>.Instance) { }
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

    private sealed class WriteStub : NoNetworkStub
    {
        private readonly Exception? _throw;
        private readonly string _id;
        public WriteStub(Exception? toThrow = null, string id = "sr-default")
        {
            _throw = toThrow;
            _id = id;
        }
        protected override Task<string> WriteAsync(ServiceRequest resource, string accessToken, CancellationToken ct) =>
            _throw is null ? Task.FromResult(_id) : throw _throw;
    }

    private sealed class SearchStub : NoNetworkStub
    {
        private readonly Bundle _bundle;
        public SearchStub(IEnumerable<ServiceRequest> requests)
        {
            _bundle = new Bundle
            {
                Entry = requests.Select(r => new Bundle.EntryComponent { Resource = r }).ToList(),
            };
        }
        protected override Task<Bundle?> SearchAsync(TenantConfig tenant, string patientId, CancellationToken ct) =>
            Task.FromResult<Bundle?>(_bundle);
    }

    private sealed class SearchFailsStub : NoNetworkStub
    {
        protected override Task<Bundle?> SearchAsync(TenantConfig tenant, string patientId, CancellationToken ct) =>
            throw new TaskCanceledException("search timed out");
    }
}
