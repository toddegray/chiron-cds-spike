using System.Net;
using Chiron.Cds.Web.Configuration;
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
        new(BuildRegistry(), NullLogger<PatientSearchService>.Instance);

    [Fact]
    public async Task Empty_Query_Returns_Empty_Result_With_No_Warning()
    {
        var result = await Build().SearchAsync("", CancellationToken.None);
        result.Hits.Should().BeEmpty();
        result.Warning.Should().BeNull(
            because: "empty queries are a normal landing state, not an error worth warning about");
    }

    [Fact]
    public async Task Whitespace_Only_Query_Treated_As_Empty()
    {
        var result = await Build().SearchAsync("   \t  ", CancellationToken.None);
        result.Hits.Should().BeEmpty();
        result.Warning.Should().BeNull();
    }

    [Fact]
    public async Task Single_Character_Query_Returns_TooShort_Warning_Without_Hitting_The_Wire()
    {
        // If this test reaches the (unreachable) open.test FHIR endpoint
        // it will block for ~12 s. Returning fast proves the guard fires
        // before any network call.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var result = await Build().SearchAsync("a", cts.Token);
        result.Hits.Should().BeEmpty();
        result.Warning.Should().NotBeNullOrEmpty();
        result.Warning.Should().Contain("at least 2 characters");
    }

    [Fact]
    public async Task Query_Is_Trimmed_For_Min_Length_Check()
    {
        var result = await Build().SearchAsync("  a  ", CancellationToken.None);
        result.Warning.Should().Contain("at least 2 characters",
            because: "trimming happens before length is measured — '  a  ' is effectively 'a'");
    }

    [Fact]
    public async Task Timeout_From_Linked_Cts_Surfaces_TimedOut_Warning()
    {
        // ThrowingStub raises OperationCanceledException as if the linked
        // timeout fired. The outer ct is NOT cancelled so the `when` filter
        // matches and the catch returns the friendly warning.
        var svc = new ThrowingStub(() => new OperationCanceledException("simulated timeout"));
        var result = await svc.SearchAsync("smith", CancellationToken.None);
        result.Hits.Should().BeEmpty();
        result.Warning.Should().NotBeNull().And.Contain("timed out");
    }

    [Fact]
    public async Task HttpRequestException_Surfaces_Failed_Warning()
    {
        var svc = new ThrowingStub(() => new HttpRequestException("connection refused"));
        var result = await svc.SearchAsync("smith", CancellationToken.None);
        result.Hits.Should().BeEmpty();
        result.Warning.Should().NotBeNull().And.Contain("Search failed");
    }

    [Fact]
    public async Task FhirOperationException_Surfaces_Failed_Warning()
    {
        var svc = new ThrowingStub(() => new FhirOperationException("server boom", HttpStatusCode.InternalServerError));
        var result = await svc.SearchAsync("smith", CancellationToken.None);
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
        Func<Task> act = () => svc.SearchAsync("smith", cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
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
            : base(BuildRegistry(), NullLogger<PatientSearchService>.Instance)
        {
            _exceptionFactory = exceptionFactory;
        }

        protected override Task<Bundle?> ExecuteSearchAsync(
            TenantConfig tenant, string query, int maxResults, CancellationToken ct) =>
            throw _exceptionFactory();
    }
}
