using Chiron.Cds.Web.FhirClient;
using FluentAssertions;
using Hl7.Fhir.Model;

namespace Chiron.Cds.Web.IntegrationTests;

/// <summary>
/// Unit tests for <see cref="PatientMrn.Extract"/> — the MRN is an org-assigned
/// identifier, never the FHIR resource id. Resolution prefers the tenant's
/// configured system (Epic exposes the MRN under an org OID with no standard
/// type coding) and falls back to the HL7 "MR" / "MRN" identifier type.
/// </summary>
public class PatientMrnTests
{
    private const string MrnSystem = "urn:oid:1.2.840.114350.1.13.0.1.7.5.737384.14";

    [Fact]
    public void Extracts_Value_From_Configured_System()
    {
        var patient = new Patient
        {
            Identifier =
            {
                new Identifier("http://open.epic.com/FHIR/StructureDefinition/patient-fhir-id", "erXuFYUfucBZaryVksYEcMg3"),
                new Identifier(MrnSystem, "203713"),
            },
        };
        PatientMrn.Extract(patient, MrnSystem).Should().Be("203713",
            because: "the configured MRN system selects the MRN over the FHIR id");
    }

    [Fact]
    public void Trims_Whitespace_From_The_Value()
    {
        var patient = new Patient { Identifier = { new Identifier(MrnSystem, "   203713  ") } };
        PatientMrn.Extract(patient, MrnSystem).Should().Be("203713");
    }

    [Fact]
    public void Falls_Back_To_MR_Typed_Identifier_When_No_System_Configured()
    {
        var patient = new Patient
        {
            Identifier =
            {
                new Identifier("urn:other", "E4007"),
                new Identifier("urn:org", "555123")
                {
                    Type = new CodeableConcept("http://terminology.hl7.org/CodeSystem/v2-0203", "MR"),
                },
            },
        };
        PatientMrn.Extract(patient, mrnSystem: null).Should().Be("555123");
    }

    [Fact]
    public void Falls_Back_To_MRN_Text_Typed_Identifier()
    {
        var patient = new Patient
        {
            Identifier = { new Identifier("urn:org", "777") { Type = new CodeableConcept { Text = "MRN" } } },
        };
        PatientMrn.Extract(patient, mrnSystem: null).Should().Be("777");
    }

    [Fact]
    public void Returns_Null_When_Nothing_Matches()
    {
        var patient = new Patient { Identifier = { new Identifier("urn:other", "abc") } };
        PatientMrn.Extract(patient, MrnSystem).Should().BeNull(
            because: "no identifier matches the configured system or an MR type — better null than a fake MRN");
    }

    [Fact]
    public void Returns_Null_When_Patient_Has_No_Identifiers()
    {
        PatientMrn.Extract(new Patient(), MrnSystem).Should().BeNull();
    }
}
