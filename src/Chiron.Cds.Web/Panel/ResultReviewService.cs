using System.Globalization;
using Chiron.Cds.Web.FhirClient;
using Chiron.Cds.Web.Tenancy;
using Hl7.Fhir.Model;
using Task = System.Threading.Tasks.Task;

namespace Chiron.Cds.Web.Panel;

/// <summary>Backs <c>/app/patient/{id}/results</c>: fetches reports + observations, groups for trending.</summary>
public class ResultReviewService
{
    /// <summary>Last N values to retain per lab trend group.</summary>
    private const int TrendSeriesLength = 6;

    /// <summary>How many reports to surface on the page.</summary>
    private const int MaxReports = 30;

    private readonly TenantRegistry _tenants;
    private readonly ILogger<ResultReviewService> _log;

    public ResultReviewService(TenantRegistry tenants, ILogger<ResultReviewService> log)
    {
        _tenants = tenants;
        _log = log;
    }

    public virtual async Task<ResultReviewData> GetForPatientAsync(string patientId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(patientId);
        var tenant = _tenants.Default.AsOpen();

        Patient? patient;
        Bundle? reports;
        Bundle? labs;
        Bundle? vitals;
        try
        {
            (patient, reports, labs, vitals) = await FetchAsync(tenant, patientId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is Hl7.Fhir.Rest.FhirOperationException or HttpRequestException or TaskCanceledException)
        {
            _log.LogWarning(ex, "Result review fetch failed for patient {Id}.", patientId);
            return ResultReviewData.Failure(patientId, SummariseError(ex));
        }

        var demographics = ProjectDemographics(patient, mrn: patientId);
        var reportSummaries = (reports?.Entry ?? Enumerable.Empty<Bundle.EntryComponent>())
            .Select(e => e.Resource).OfType<DiagnosticReport>()
            .Select(ProjectReport)
            .OrderByDescending(r => r.IssuedAt ?? DateTimeOffset.MinValue)
            .Take(MaxReports)
            .ToArray();

        var observations = (labs?.Entry ?? Enumerable.Empty<Bundle.EntryComponent>())
            .Concat(vitals?.Entry ?? Enumerable.Empty<Bundle.EntryComponent>())
            .Select(e => e.Resource).OfType<Observation>()
            .ToArray();
        var trends = GroupTrends(observations);

        return new ResultReviewData(
            Demographics: demographics,
            Reports: reportSummaries,
            Trends: trends,
            Error: null);
    }

    /// <summary>
    /// Issue the four FHIR fetches in parallel. Exposed as virtual so tests
    /// can stub the live network without standing up a real FHIR server.
    /// </summary>
    protected virtual async Task<(Patient? Patient, Bundle? Reports, Bundle? Labs, Bundle? Vitals)>
        FetchAsync(TenantConfig tenant, string patientId, CancellationToken ct)
    {
        using var client = new TenantFhirClient(tenant, accessToken: null);
        var patientTask = client.ReadAsync<Patient>(patientId, ct);
        var reportsTask = client.SearchAsync<DiagnosticReport>(
            new[] { $"patient={patientId}", "_count=30" }, ct);
        var labsTask = client.SearchAsync<Observation>(
            new[] { $"patient={patientId}", "category=laboratory", "_count=50" }, ct);
        var vitalsTask = client.SearchAsync<Observation>(
            new[] { $"patient={patientId}", "category=vital-signs", "_count=50" }, ct);
        await Task.WhenAll(patientTask, reportsTask, labsTask, vitalsTask).ConfigureAwait(false);
        return (await patientTask, await reportsTask, await labsTask, await vitalsTask);
    }

    internal static PatientDemographics ProjectDemographics(Patient? patient, string mrn)
    {
        if (patient is null)
            return new PatientDemographics($"Patient {mrn}", string.Empty, null, mrn);

        var displayName = PanelService.ChartName(patient, mrn);
        var age = AgeYearsFrom(patient.BirthDate);
        var ageSex = age > 0
            ? $"{age}y · {SexLabel(patient.Gender)}"
            : SexLabel(patient.Gender);
        return new PatientDemographics(
            DisplayName: displayName,
            AgeSex: ageSex,
            DateOfBirth: patient.BirthDate,
            Mrn: mrn);
    }

    internal static IReadOnlyList<LabTrend> GroupTrends(IReadOnlyList<Observation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);
        return observations
            .Select(ProjectPoint)
            .Where(p => p.Trend.Title.Length > 0 && p.Point is not null)
            .GroupBy(p => p.Trend.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                var first = g.First().Trend;
                var points = g
                    .Select(p => p.Point!)
                    .OrderByDescending(pt => pt.EffectiveAt ?? DateTimeOffset.MinValue)
                    .Take(TrendSeriesLength)
                    .ToArray();
                return first with { Points = points };
            })
            .OrderByDescending(t => t.Points.Count > 0
                ? t.Points[0].EffectiveAt ?? DateTimeOffset.MinValue
                : DateTimeOffset.MinValue)
            .ToArray();
    }

    private static (LabTrend Trend, TrendPoint? Point) ProjectPoint(Observation o)
    {
        var (key, loinc, title) = LookupTrendIdentity(o.Code);
        if (string.IsNullOrEmpty(title)) return (LabTrend.Empty, null);

        var (value, unit, isNumeric) = ProjectValue(o.Value);
        if (!isNumeric) return (LabTrend.Empty, null);

        DateTimeOffset? effective = null;
        if (o.Effective is FhirDateTime dt && DateTimeOffset.TryParse(
            dt.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            effective = parsed;
        else if (o.Effective is Period p && DateTimeOffset.TryParse(
            p.Start, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var pstart))
            effective = pstart;

        var abnormal = o.Interpretation?
            .SelectMany(c => c.Coding ?? Enumerable.Empty<Coding>())
            .Any(c => c.Code is "H" or "HH" or "L" or "LL" or "A" or "AA") == true;

        var trend = new LabTrend(
            Key: key,
            Loinc: loinc,
            Title: title,
            Unit: unit,
            Points: Array.Empty<TrendPoint>());
        var point = new TrendPoint(effective, value, unit, abnormal);
        return (trend, point);
    }

    private static (string Value, string Unit, bool IsNumeric) ProjectValue(DataType? value)
    {
        if (value is Quantity q && q.Value is not null)
        {
            var v = ((double)q.Value).ToString("0.##", CultureInfo.InvariantCulture);
            return (v, q.Unit ?? q.Code ?? string.Empty, true);
        }
        return (string.Empty, string.Empty, false);
    }

    private static (string Key, string? Loinc, string Title) LookupTrendIdentity(CodeableConcept? code)
    {
        if (code is null) return (string.Empty, null, string.Empty);
        var loinc = code.Coding?
            .FirstOrDefault(c => c.System is not null
                && c.System.Contains("loinc", StringComparison.OrdinalIgnoreCase))
            ?.Code;
        var title = !string.IsNullOrWhiteSpace(code.Text)
            ? code.Text
            : code.Coding?.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.Display))?.Display
                ?? string.Empty;
        var key = !string.IsNullOrEmpty(loinc) ? "loinc:" + loinc : "text:" + title;
        return (key, loinc, title);
    }

    internal static ReportSummary ProjectReport(DiagnosticReport r)
    {
        var title = r.Code?.Text
            ?? r.Code?.Coding?.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.Display))?.Display
            ?? "Diagnostic report";
        var category = r.Category?
            .SelectMany(c => c.Coding ?? Enumerable.Empty<Coding>())
            .FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.Display))?.Display
            ?? r.Category?.FirstOrDefault()?.Text;
        DateTimeOffset? issued = null;
        if (r.Issued is DateTimeOffset i) issued = i;
        return new ReportSummary(
            Title: title,
            Category: category,
            Status: r.Status?.ToString() ?? "unknown",
            IssuedAt: issued,
            Conclusion: r.Conclusion);
    }

    private static int AgeYearsFrom(string? birthDate)
    {
        if (string.IsNullOrWhiteSpace(birthDate)) return 0;
        if (!DateOnly.TryParse(birthDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dob)) return 0;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var age = today.Year - dob.Year;
        if (today < dob.AddYears(age)) age--;
        return age < 0 ? 0 : age;
    }

    private static string SexLabel(AdministrativeGender? g) => g switch
    {
        AdministrativeGender.Female => "Female",
        AdministrativeGender.Male => "Male",
        _ => "Other",
    };

    internal static string SummariseError(Exception ex) => ex switch
    {
        Hl7.Fhir.Rest.FhirOperationException fop => $"FHIR {(int)fop.Status} {fop.Status}",
        TaskCanceledException => "Timed out",
        _ => "Network error",
    };
}

/// <summary>Aggregate the Results page renders; non-null <see cref="Error"/> swaps sections for a banner.</summary>
public sealed record ResultReviewData(
    PatientDemographics Demographics,
    IReadOnlyList<ReportSummary> Reports,
    IReadOnlyList<LabTrend> Trends,
    string? Error)
{
    public static ResultReviewData Failure(string patientId, string error) => new(
        Demographics: new PatientDemographics($"Patient {patientId}", string.Empty, null, patientId),
        Reports: Array.Empty<ReportSummary>(),
        Trends: Array.Empty<LabTrend>(),
        Error: error);
}

public sealed record PatientDemographics(
    string DisplayName,
    string AgeSex,
    string? DateOfBirth,
    string Mrn);

public sealed record ReportSummary(
    string Title,
    string? Category,
    string Status,
    DateTimeOffset? IssuedAt,
    string? Conclusion);

public sealed record LabTrend(
    string Key,
    string? Loinc,
    string Title,
    string Unit,
    IReadOnlyList<TrendPoint> Points)
{
    public static readonly LabTrend Empty = new(string.Empty, null, string.Empty, string.Empty, Array.Empty<TrendPoint>());
}

public sealed record TrendPoint(
    DateTimeOffset? EffectiveAt,
    string Value,
    string Unit,
    bool IsAbnormal);
