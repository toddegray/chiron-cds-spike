using Chiron.Cds.Web.Panel;
using FluentAssertions;
using Hl7.Fhir.Model;

namespace Chiron.Cds.Web.IntegrationTests;

/// <summary>
/// Offline unit tests for the <see cref="PatientSearchService.ToHit"/>
/// projection. The HTTP path lives behind Firely's FhirClient and is
/// exercised live in <c>PanelControllerLiveTests</c>; these tests pin the
/// FHIR-to-DTO mapping shape without needing network.
/// </summary>
public class PatientSearchServiceUnitTests
{
    [Fact]
    public void ToHit_Uses_Text_When_Present()
    {
        var p = new Patient { Id = "id1", Name = { new HumanName { Text = "Smith, Jane Q." } } };
        var hit = PatientSearchService.ToHit(p);
        hit.PatientId.Should().Be("id1");
        hit.DisplayName.Should().Be("Smith, Jane Q.");
    }

    [Fact]
    public void ToHit_Composes_Family_And_Given_When_Text_Missing()
    {
        var p = new Patient
        {
            Id = "id2",
            Name = { new HumanName { Family = "Doe", Given = new[] { "John", "Q." } } },
        };
        PatientSearchService.ToHit(p).DisplayName.Should().Be("Doe, John Q.");
    }

    [Fact]
    public void ToHit_Uses_Family_Only_When_Given_Missing()
    {
        var p = new Patient { Id = "id3", Name = { new HumanName { Family = "Cher" } } };
        PatientSearchService.ToHit(p).DisplayName.Should().Be("Cher");
    }

    [Fact]
    public void ToHit_Uses_Given_Only_When_Family_Missing()
    {
        var p = new Patient { Id = "id4", Name = { new HumanName { Given = new[] { "Madonna" } } } };
        PatientSearchService.ToHit(p).DisplayName.Should().Be("Madonna");
    }

    [Fact]
    public void ToHit_Falls_Back_To_Patient_Id_When_Name_Empty()
    {
        var p = new Patient { Id = "id5" };
        PatientSearchService.ToHit(p).DisplayName.Should().Be("Patient id5");
    }

    [Fact]
    public void ToHit_Falls_Back_When_Name_Entry_Has_Only_Empty_Fields()
    {
        var p = new Patient { Id = "id6", Name = { new HumanName { Family = "", Given = new[] { "" } } } };
        PatientSearchService.ToHit(p).DisplayName.Should().Be("Patient id6");
    }

    [Fact]
    public void ToHit_Captures_Gender_When_Present()
    {
        var p = new Patient { Id = "id7", Gender = AdministrativeGender.Female };
        PatientSearchService.ToHit(p).Gender.Should().Be("Female");
    }

    [Fact]
    public void ToHit_Gender_Is_Empty_String_When_Null()
    {
        var p = new Patient { Id = "id8" };
        PatientSearchService.ToHit(p).Gender.Should().BeEmpty();
    }

    [Fact]
    public void ToHit_Captures_BirthDate_When_Present()
    {
        var p = new Patient { Id = "id9", BirthDate = "1980-06-15" };
        PatientSearchService.ToHit(p).BirthDate.Should().Be("1980-06-15");
    }

    [Fact]
    public void ToHit_BirthDate_Is_Empty_String_When_Null()
    {
        var p = new Patient { Id = "id10" };
        PatientSearchService.ToHit(p).BirthDate.Should().BeEmpty();
    }

    [Fact]
    public void ToHit_PatientId_Is_Empty_String_When_Resource_Has_No_Id()
    {
        var p = new Patient { Name = { new HumanName { Family = "Anon" } } };
        PatientSearchService.ToHit(p).PatientId.Should().BeEmpty();
    }
}
