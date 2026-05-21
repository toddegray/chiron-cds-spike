namespace Chiron.Cds.Engine.Primitives;

/// <summary>
/// Severity tier for an <see cref="Alert"/>. Maps cleanly to the CDS Hooks
/// indicator field (info / warning / critical).
/// </summary>
public enum Severity
{
    /// <summary>Informational; no action required.</summary>
    Info,

    /// <summary>Low priority; consider reviewing.</summary>
    Low,

    /// <summary>Medium priority; review recommended.</summary>
    Medium,

    /// <summary>High priority; action recommended.</summary>
    High,

    /// <summary>Critical; immediate action.</summary>
    Critical,
}
