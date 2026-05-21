using System.Text.Json.Serialization;

namespace Chiron.Cds.Shared;

/// <summary>
/// The subset of <c>.well-known/smart-configuration</c> we consume. Cerner
/// returns more fields; this DTO keeps the deserializer permissive (extra
/// fields are ignored) so any future additions don't break the spike.
/// </summary>
public sealed record SmartConfiguration(
    [property: JsonPropertyName("issuer")] string? Issuer,
    [property: JsonPropertyName("authorization_endpoint")] string AuthorizationEndpoint,
    [property: JsonPropertyName("token_endpoint")] string TokenEndpoint,
    [property: JsonPropertyName("jwks_uri")] string? JwksUri,
    [property: JsonPropertyName("introspection_endpoint")] string? IntrospectionEndpoint,
    [property: JsonPropertyName("revocation_endpoint")] string? RevocationEndpoint,
    [property: JsonPropertyName("capabilities")] IReadOnlyList<string>? Capabilities,
    [property: JsonPropertyName("scopes_supported")] IReadOnlyList<string>? ScopesSupported,
    [property: JsonPropertyName("code_challenge_methods_supported")] IReadOnlyList<string>? CodeChallengeMethodsSupported);
