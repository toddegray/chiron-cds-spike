namespace Chiron.Cds.Web.SmartLaunch;

/// <summary>
/// Server-side session created when a SMART launch completes. Carries the
/// access token, the SMART context (<c>patient</c>, <c>encounter</c>), and
/// the granted scopes. Looked up by session id from the cookie or query
/// parameter on subsequent requests.
/// </summary>
public sealed record SmartSession(
    string SessionId,
    string TenantId,
    string AccessToken,
    string? RefreshToken,
    string PatientId,
    string? EncounterId,
    string? IdToken,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<string> GrantedScopes)
{
    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;
}
