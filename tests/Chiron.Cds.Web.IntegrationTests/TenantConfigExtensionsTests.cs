using Chiron.Cds.Web.Tenancy;
using FluentAssertions;

namespace Chiron.Cds.Web.IntegrationTests;

/// <summary>
/// Pins <see cref="TenantConfigExtensions.AsOpen"/>. The panel and search
/// surfaces both go through this extension to swap the authenticated FHIR
/// base for the open one — these tests pin the swap behaviour and the
/// null-guard.
/// </summary>
public class TenantConfigExtensionsTests
{
    private static TenantConfig Tenant(Uri? open) => new(
        Id: "t",
        DisplayName: "Test",
        ClientId: "client",
        ClientSecret: null,
        FhirBaseUrl: new Uri("https://auth.example.com/fhir"),
        FhirOpenBaseUrl: open,
        Scopes: "");

    [Fact]
    public void AsOpen_Swaps_The_Fhir_Base_For_The_Open_Base()
    {
        var open = new Uri("https://open.example.com/fhir");
        var t = Tenant(open);
        var swapped = t.AsOpen();

        swapped.FhirBaseUrl.Should().Be(open);
        swapped.FhirOpenBaseUrl.Should().Be(open,
            because: "the open base url is preserved on the swapped tenant — callers may still need it");
        swapped.Id.Should().Be(t.Id);
        swapped.ClientId.Should().Be(t.ClientId);
    }

    [Fact]
    public void AsOpen_Throws_When_FhirOpenBaseUrl_Is_Null()
    {
        var t = Tenant(open: null);
        var act = () => t.AsOpen();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*FhirOpenBaseUrl*");
    }

    [Fact]
    public void AsOpen_Rejects_Null_Tenant()
    {
        TenantConfig? t = null;
        var act = () => t!.AsOpen();
        act.Should().Throw<ArgumentNullException>();
    }
}
