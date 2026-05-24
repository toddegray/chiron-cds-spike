namespace Chiron.Cds.Web.SmartLaunch;

/// <summary>
/// One row on the Day View / worklist. Each row represents a patient on
/// today's schedule (or the doctor's panel) and carries the headline
/// attention flag the engine produced for that patient — so a clinician
/// can glance down the day and see who needs them most before walking
/// into any visit.
/// </summary>
/// <param name="PatientId">FHIR patient id (used to drill into the Visit Brief).</param>
/// <param name="DisplayName">Patient display name for the row.</param>
/// <param name="AgeSex">Pre-formatted "78y · Male" string.</param>
/// <param name="AppointmentTime">Optional pre-formatted appointment time ("8:20 AM") or null when off-schedule.</param>
/// <param name="ChiefComplaint">Optional chief-complaint text from the appointment.</param>
/// <param name="HeadlineFlag">One-line attention flag derived from the highest-severity card; null if the chart is clean.</param>
/// <param name="HeadlineSeverity">CDS Hooks indicator string ("critical" / "warning" / "info" / null when clean).</param>
/// <param name="AlertCount">Total number of cards the engine produced.</param>
internal sealed record WorklistRow(
    string PatientId,
    string DisplayName,
    string AgeSex,
    string? AppointmentTime,
    string? ChiefComplaint,
    string? HeadlineFlag,
    string? HeadlineSeverity,
    int AlertCount);
