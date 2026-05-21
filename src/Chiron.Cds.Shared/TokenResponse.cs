using System.Text.Json.Serialization;

namespace Chiron.Cds.Shared;

/// <summary>
/// The token endpoint response per SMART App Launch v2: standard OAuth2
/// fields plus SMART-specific launch context (<c>patient</c>, <c>encounter</c>,
/// <c>need_patient_banner</c>, etc.).
/// </summary>
public sealed record TokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("token_type")] string TokenType,
    [property: JsonPropertyName("expires_in")] int ExpiresInSeconds,
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken,
    [property: JsonPropertyName("id_token")] string? IdToken,
    [property: JsonPropertyName("patient")] string? Patient,
    [property: JsonPropertyName("encounter")] string? Encounter,
    [property: JsonPropertyName("need_patient_banner")] bool? NeedPatientBanner,
    [property: JsonPropertyName("smart_style_url")] string? SmartStyleUrl);
