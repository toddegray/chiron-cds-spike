using Chiron.Cds.Engine.Primitives;
using Chiron.Cds.Web.Tenancy;
using Hl7.Fhir.Model;

namespace Chiron.Cds.Web.FhirClient;

/// <summary>
/// Writes a Chiron-generated <see cref="DiagnosticReport"/> back to the EHR.
/// One report per evaluation; the alert's <see cref="Alert.Explain"/> output
/// goes into the report's <c>conclusion</c> so the derivation is visible
/// even if the receiver doesn't render Markdown.
/// </summary>
public sealed class DiagnosticReportWriter
{
    private const string ChironSystem = "https://chiron.health/cds";
    private readonly ILogger<DiagnosticReportWriter> _log;

    public DiagnosticReportWriter(ILogger<DiagnosticReportWriter> log)
    {
        _log = log;
    }

    public async Task<string> WriteAsync(
        TenantConfig tenant,
        string accessToken,
        string patientId,
        Alert alert,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(alert);
        ArgumentException.ThrowIfNullOrWhiteSpace(patientId);

        var report = BuildReport(patientId, alert);
        using var client = new TenantFhirClient(tenant, accessToken);
        var created = await client.CreateAsync(report, ct).ConfigureAwait(false);
        var location = created.Id ?? throw new InvalidOperationException("FHIR server returned a report without an id.");
        _log.LogInformation("Wrote DiagnosticReport {Id} for patient {PatientId}.", location, patientId);
        return location;
    }

    internal static DiagnosticReport BuildReport(string patientId, Alert alert) => new()
    {
        Status = DiagnosticReport.DiagnosticReportStatus.Final,
        Category = new List<CodeableConcept>
        {
            new("http://terminology.hl7.org/CodeSystem/v2-0074", "OTH", "Other", text: null)
        },
        Code = new CodeableConcept(ChironSystem, alert.RuleId, alert.Message, text: null),
        Subject = new ResourceReference($"Patient/{patientId}"),
        Effective = new FhirDateTime(DateTimeOffset.UtcNow),
        Issued = DateTimeOffset.UtcNow,
        Conclusion = alert.Explain(),
        Identifier = new List<Identifier>
        {
            new Identifier { System = ChironSystem + "/fingerprint", Value = alert.Fingerprint },
        },
    };
}
