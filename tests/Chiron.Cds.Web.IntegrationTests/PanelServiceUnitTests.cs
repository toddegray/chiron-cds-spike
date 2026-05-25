using Chiron.Cds.Web.Panel;
using FluentAssertions;
using Hl7.Fhir.Model;
using FhirOperationException = Hl7.Fhir.Rest.FhirOperationException;

namespace Chiron.Cds.Web.IntegrationTests;

/// <summary>
/// Offline unit tests for the static helpers on <see cref="PanelService"/>.
/// The orchestration logic that wires these together is exercised live in
/// <c>PanelControllerLiveTests</c>; these tests pin the branch behaviour
/// without requiring network access to Cerner.
/// </summary>
public class PanelServiceUnitTests
{
    [Fact]
    public void ChartName_Falls_Back_When_No_Name_Entries()
    {
        var patient = new Patient { Id = "p1" };
        PanelService.ChartName(patient, fallbackId: "p1").Should().Be("Patient p1");
    }

    [Fact]
    public void ChartName_Uses_Text_When_Present()
    {
        var patient = new Patient { Name = { new HumanName { Text = "Smith, John Q." } } };
        PanelService.ChartName(patient, "ignored").Should().Be("Smith, John Q.");
    }

    [Fact]
    public void ChartName_Composes_Family_And_Given_When_Text_Missing()
    {
        var patient = new Patient { Name = { new HumanName { Family = "Doe", Given = new[] { "Jane" } } } };
        PanelService.ChartName(patient, "ignored").Should().Be("Doe, Jane");
    }

    [Fact]
    public void ChartName_Uses_Family_Only_When_Given_Missing()
    {
        var patient = new Patient { Name = { new HumanName { Family = "Doe" } } };
        PanelService.ChartName(patient, "ignored").Should().Be("Doe");
    }

    [Fact]
    public void ChartName_Uses_Given_Only_When_Family_Missing()
    {
        var patient = new Patient { Name = { new HumanName { Given = new[] { "Cher" } } } };
        PanelService.ChartName(patient, "ignored").Should().Be("Cher",
            because: "the leading comma left by Family-comma-Given assembly is trimmed when family is empty");
    }

    [Fact]
    public void ChartName_Falls_Back_When_Name_Entry_Has_No_Useful_Fields()
    {
        var patient = new Patient { Name = { new HumanName { Use = HumanName.NameUse.Old } } };
        PanelService.ChartName(patient, fallbackId: "fbk").Should().Be("Patient fbk");
    }

    [Theory]
    [InlineData(0, "8:00 AM")]
    [InlineData(1, "8:10 AM")]
    [InlineData(5, "8:50 AM")]
    [InlineData(6, "9:00 AM")]
    [InlineData(23, "11:50 AM")]
    [InlineData(24, "12:00 PM")]
    [InlineData(25, "12:10 PM")]
    [InlineData(30, "1:00 PM")]
    [InlineData(36, "2:00 PM")]
    public void SlotTime_Produces_Twelve_Hour_Clock_Times(int slotIndex, string expected)
    {
        PanelService.SlotTime(slotIndex).Should().Be(expected);
    }

    [Fact]
    public void SummariseError_Returns_Fhir_Status_For_FhirOperationException()
    {
        var ex = new FhirOperationException("denied", System.Net.HttpStatusCode.Forbidden);
        PanelService.SummariseError(ex).Should().Be("FHIR 403 Forbidden");
    }

    [Fact]
    public void SummariseError_Returns_TimedOut_For_TaskCanceledException()
    {
        PanelService.SummariseError(new TaskCanceledException()).Should().Be("Timed out");
    }

    [Fact]
    public void SummariseError_Returns_Network_Error_For_Generic_Exception()
    {
        PanelService.SummariseError(new HttpRequestException("connect failed"))
            .Should().Be("Network error");
    }
}
