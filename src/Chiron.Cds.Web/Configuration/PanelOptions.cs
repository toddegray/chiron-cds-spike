namespace Chiron.Cds.Web.Configuration;

/// <summary>
/// Options bound from the <c>Chiron:Panel</c> configuration section. The
/// panel is the replacement-mode entry surface — a list of patient ids the
/// clinician would be seeing today. In Phase 1 the list is a static config
/// against the open sandbox; once the authenticated FHIR endpoint is wired
/// up the source becomes <c>Appointment?practitioner=&lt;user&gt;&amp;date=today</c>.
/// </summary>
public sealed class PanelOptions
{
    public const string SectionName = "Chiron:Panel";

    /// <summary>Patient ids visible on the panel. Order is preserved in the rendered worklist.</summary>
    public List<string> Patients { get; set; } = new();
}
