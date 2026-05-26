using Chiron.Cds.Web.FhirClient;
using Chiron.Cds.Web.Tenancy;
using Hl7.Fhir.Model;
using Task = System.Threading.Tasks.Task;

namespace Chiron.Cds.Web.Panel;

/// <summary>Categories of <see cref="ServiceRequest"/> Chiron offers as order surfaces today.</summary>
public enum ServiceRequestCategory
{
    Laboratory,
    Imaging,
}

/// <summary>Writes <see cref="ServiceRequest"/> orders (lab + imaging) and reads recent history.</summary>
public class ServiceRequestService
{
    private const int MaxHistory = 15;

    private readonly TenantRegistry _tenants;
    private readonly ILogger<ServiceRequestService> _log;

    public ServiceRequestService(TenantRegistry tenants, ILogger<ServiceRequestService> log)
    {
        _tenants = tenants;
        _log = log;
    }

    public virtual async Task<ServiceRequestPageData> GetForPatientAsync(
        string patientId, ServiceRequestCategory category, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(patientId);
        var tenant = _tenants.Default.AsOpen();
        try
        {
            var bundle = await SearchAsync(tenant, patientId, ct).ConfigureAwait(false);
            var matching = (bundle?.Entry ?? Enumerable.Empty<Bundle.EntryComponent>())
                .Select(e => e.Resource).OfType<ServiceRequest>()
                .Where(r => IsCategory(r, category))
                .Select(ProjectSummary)
                .OrderByDescending(r => r.OccurrenceAt ?? DateTimeOffset.MinValue)
                .Take(MaxHistory)
                .ToArray();
            return new ServiceRequestPageData(matching, Error: null);
        }
        catch (Exception ex) when (ex is Hl7.Fhir.Rest.FhirOperationException or HttpRequestException or TaskCanceledException)
        {
            _log.LogWarning(ex, "ServiceRequest search failed for patient {Id}.", patientId);
            return ServiceRequestPageData.Failure(SummariseError(ex));
        }
    }

    public virtual async Task<ServiceRequestWriteResult> SignAsync(
        string patientId, ServiceRequestDraft draft, ServiceRequestCategory category,
        string? accessToken, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(patientId);
        ArgumentNullException.ThrowIfNull(draft);
        if (string.IsNullOrWhiteSpace(draft.OrderText))
            return ServiceRequestWriteResult.Failed("Enter a test or procedure to order.");
        if (string.IsNullOrEmpty(accessToken))
            return ServiceRequestWriteResult.NotAuthorised();

        var resource = BuildServiceRequest(patientId, draft, category);
        try
        {
            var id = await WriteAsync(resource, accessToken, ct).ConfigureAwait(false);
            return ServiceRequestWriteResult.Ok(id);
        }
        catch (Exception ex) when (ex is Hl7.Fhir.Rest.FhirOperationException or HttpRequestException)
        {
            _log.LogWarning(ex, "ServiceRequest write failed for patient {Id}.", patientId);
            return ServiceRequestWriteResult.Failed("FHIR write failed: " + SummariseError(ex));
        }
    }

    /// <summary>True when the request matches the category we're rendering.</summary>
    internal static bool IsCategory(ServiceRequest r, ServiceRequestCategory category)
    {
        var keyword = category switch
        {
            ServiceRequestCategory.Laboratory => "lab",
            ServiceRequestCategory.Imaging => "imag",
            _ => null,
        };
        if (keyword is null) return false;
        foreach (var cc in r.Category ?? Enumerable.Empty<CodeableConcept>())
        {
            if (cc.Text?.Contains(keyword, StringComparison.OrdinalIgnoreCase) == true) return true;
            foreach (var coding in cc.Coding ?? Enumerable.Empty<Coding>())
            {
                if (coding.Display?.Contains(keyword, StringComparison.OrdinalIgnoreCase) == true) return true;
            }
        }
        return false;
    }

    internal static ServiceRequestSummary ProjectSummary(ServiceRequest r)
    {
        var name = r.Code?.Text
            ?? r.Code?.Coding?.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.Display))?.Display
            ?? "Order";
        var reason = r.ReasonCode?.FirstOrDefault()?.Text
            ?? r.ReasonCode?.SelectMany(c => c.Coding ?? Enumerable.Empty<Coding>())
                .FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.Display))?.Display;
        DateTimeOffset? when = null;
        if (r.Occurrence is FhirDateTime dt && DateTimeOffset.TryParse(dt.Value, out var p))
            when = p;
        return new ServiceRequestSummary(
            Name: name,
            Status: r.Status?.ToString() ?? "unknown",
            Priority: r.Priority?.ToString(),
            Reason: reason,
            OccurrenceAt: when);
    }

    internal static ServiceRequest BuildServiceRequest(
        string patientId, ServiceRequestDraft draft, ServiceRequestCategory category)
    {
        var resource = new ServiceRequest
        {
            Status = RequestStatus.Active,
            Intent = RequestIntent.Order,
            Priority = MapPriority(draft.Priority),
            Subject = new ResourceReference($"Patient/{patientId}"),
            AuthoredOnElement = new FhirDateTime(DateTimeOffset.UtcNow),
            Code = new CodeableConcept { Text = draft.OrderText.Trim() },
            Category = new List<CodeableConcept>
            {
                CategoryConcept(category),
            },
        };
        if (!string.IsNullOrWhiteSpace(draft.Reason))
            resource.ReasonCode = new List<CodeableConcept> { new() { Text = draft.Reason.Trim() } };
        return resource;
    }

    private static CodeableConcept CategoryConcept(ServiceRequestCategory category) => category switch
    {
        ServiceRequestCategory.Laboratory => new CodeableConcept
        {
            Text = "Laboratory procedure",
            Coding = new List<Coding>
            {
                new() { System = "http://snomed.info/sct", Code = "108252007", Display = "Laboratory procedure" },
            },
        },
        ServiceRequestCategory.Imaging => new CodeableConcept
        {
            Text = "Imaging",
            Coding = new List<Coding>
            {
                new() { System = "http://snomed.info/sct", Code = "363679005", Display = "Imaging" },
            },
        },
        _ => throw new ArgumentOutOfRangeException(nameof(category)),
    };

    private static RequestPriority? MapPriority(string? priority) => priority?.ToLowerInvariant() switch
    {
        "routine" => RequestPriority.Routine,
        "urgent" => RequestPriority.Urgent,
        "stat" => RequestPriority.Stat,
        _ => null,
    };

    protected virtual async Task<Bundle?> SearchAsync(TenantConfig tenant, string patientId, CancellationToken ct)
    {
        using var client = new TenantFhirClient(tenant, accessToken: null);
        return await client.SearchAsync<ServiceRequest>(
            new[] { $"patient={patientId}", "_count=30" }, ct).ConfigureAwait(false);
    }

    protected virtual async Task<string> WriteAsync(ServiceRequest resource, string accessToken, CancellationToken ct)
    {
        var tenant = _tenants.Default;
        using var client = new TenantFhirClient(tenant, accessToken);
        var created = await client.CreateAsync(resource, ct).ConfigureAwait(false);
        return created.Id ?? string.Empty;
    }

    internal static string SummariseError(Exception ex) => ex switch
    {
        Hl7.Fhir.Rest.FhirOperationException fop => $"FHIR {(int)fop.Status} {fop.Status}",
        TaskCanceledException => "Timed out",
        _ => "Network error",
    };
}

public sealed record ServiceRequestDraft(string OrderText, string? Reason, string? Priority)
{
    public static readonly ServiceRequestDraft Empty = new(string.Empty, null, null);
}

public sealed record ServiceRequestSummary(
    string Name,
    string Status,
    string? Priority,
    string? Reason,
    DateTimeOffset? OccurrenceAt);

public sealed record ServiceRequestPageData(
    IReadOnlyList<ServiceRequestSummary> History,
    string? Error)
{
    public static ServiceRequestPageData Failure(string error) =>
        new(Array.Empty<ServiceRequestSummary>(), error);
}

public sealed record ServiceRequestWriteResult(
    ServiceRequestWriteStatus Status, string? WrittenId, string? Message)
{
    public static ServiceRequestWriteResult Ok(string id) => new(ServiceRequestWriteStatus.Ok, id, null);
    public static ServiceRequestWriteResult NotAuthorised() => new(ServiceRequestWriteStatus.NotAuthorised, null, null);
    public static ServiceRequestWriteResult Failed(string message) => new(ServiceRequestWriteStatus.Failed, null, message);
}

public enum ServiceRequestWriteStatus { Ok, NotAuthorised, Failed }
