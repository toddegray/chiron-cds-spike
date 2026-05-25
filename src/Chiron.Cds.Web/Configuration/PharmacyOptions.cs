namespace Chiron.Cds.Web.Configuration;

/// <summary>Options bound from <c>Chiron:Pharmacies</c>. Each entry is a stub directory option.</summary>
public sealed class PharmacyOptions
{
    public const string SectionName = "Chiron:Pharmacies";

    /// <summary>Configured pharmacy selections offered in the order-entry dropdown.</summary>
    public List<PharmacyEntry> Entries { get; set; } = new();
}

/// <summary>
/// One pharmacy choice. <see cref="Id"/> is the URL/form key; <see cref="DisplayName"/>
/// is the human label. Production replaces this with NCPDP-keyed lookups.
/// </summary>
public sealed class PharmacyEntry
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
}
