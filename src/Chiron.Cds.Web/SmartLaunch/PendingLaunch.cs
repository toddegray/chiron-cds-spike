namespace Chiron.Cds.Web.SmartLaunch;

/// <summary>
/// Pre-callback state for an in-flight SMART launch. Keyed by the OAuth
/// <c>state</c> parameter (which we generate). Holds the PKCE verifier and
/// the tenant + launch token until the callback resolves the code-for-token
/// exchange.
/// </summary>
public sealed record PendingLaunch(
    string State,
    string TenantId,
    string CodeVerifier,
    string? LaunchToken,
    string RedirectUri,
    DateTimeOffset CreatedAt)
{
    public bool IsExpired(TimeSpan ttl) => DateTimeOffset.UtcNow - CreatedAt > ttl;
}
