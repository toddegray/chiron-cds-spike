using Chiron.Cds.Web.Configuration;
using Chiron.Cds.Web.Tenancy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Chiron.Cds.Web.SmartLaunch;

/// <summary>EHR-initiated SMART launch entry point.</summary>
[ApiController]
[Route("smart")]
public sealed class LaunchController : ControllerBase
{
    private readonly TenantRegistry _tenants;
    private readonly AuthorizationService _auth;
    private readonly ChironOptions _options;
    private readonly ILogger<LaunchController> _log;

    public LaunchController(
        TenantRegistry tenants,
        AuthorizationService auth,
        IOptions<ChironOptions> options,
        ILogger<LaunchController> log)
    {
        _tenants = tenants;
        _auth = auth;
        _options = options.Value;
        _log = log;
    }

    /// <summary>
    /// EHR-initiated launch. The EHR redirects the browser here with
    /// <c>iss</c> (the FHIR base) and <c>launch</c> (an opaque launch token).
    /// We resolve the tenant by <c>iss</c>, build the authorize URL, and
    /// 302 the browser to the EHR's authorize endpoint.
    /// </summary>
    [HttpGet("launch")]
    public async Task<IActionResult> Launch(
        [FromQuery] string? iss,
        [FromQuery] string? launch,
        [FromQuery] string? tenant,
        [FromQuery] string? context,
        CancellationToken ct)
    {
        var tenantConfig = !string.IsNullOrWhiteSpace(iss)
            ? _tenants.GetByFhirBase(iss)
            : !string.IsNullOrWhiteSpace(tenant)
                ? _tenants.GetById(tenant)
                : _tenants.Default;
        var redirectUri = BuildRedirectUri();

        var requestPatientContext = string.Equals(context, "patient", StringComparison.OrdinalIgnoreCase);
        var authorizeUri = await _auth.BuildAuthorizeUriAsync(
            tenantConfig, launch, redirectUri, ct, requestPatientContext).ConfigureAwait(false);
        _log.LogInformation("Redirecting to authorize endpoint for tenant {Tenant}.", tenantConfig.Id);
        return Redirect(authorizeUri.AbsoluteUri);
    }

    private string BuildRedirectUri()
    {
        var baseUrl = string.IsNullOrEmpty(_options.BaseUrl)
            ? $"{Request.Scheme}://{Request.Host}"
            : _options.BaseUrl.TrimEnd('/');
        return $"{baseUrl}/smart/callback";
    }
}
