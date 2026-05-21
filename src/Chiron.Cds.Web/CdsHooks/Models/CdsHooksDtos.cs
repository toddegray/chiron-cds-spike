using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Chiron.Cds.Web.CdsHooks.Models;

/// <summary>Discovery response: the list of services this endpoint offers.</summary>
public sealed record CdsServicesResponse(
    [property: JsonPropertyName("services")] IReadOnlyList<CdsServiceDescriptor> Services);

/// <summary>One service in the discovery response.</summary>
public sealed record CdsServiceDescriptor(
    [property: JsonPropertyName("hook")] string Hook,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("prefetch")] IReadOnlyDictionary<string, string>? Prefetch);

/// <summary>Inbound CDS Hooks invocation.</summary>
public sealed record CdsHookRequest(
    [property: JsonPropertyName("hook")] string Hook,
    [property: JsonPropertyName("hookInstance")] string HookInstance,
    [property: JsonPropertyName("fhirServer")] string? FhirServer,
    [property: JsonPropertyName("fhirAuthorization")] CdsFhirAuthorization? FhirAuthorization,
    [property: JsonPropertyName("context")] JsonElement Context,
    [property: JsonPropertyName("prefetch")] Dictionary<string, JsonElement>? Prefetch);

/// <summary>Auth metadata an EHR may include so we can call back to the FHIR server.</summary>
public sealed record CdsFhirAuthorization(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("token_type")] string TokenType,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("scope")] string? Scope,
    [property: JsonPropertyName("subject")] string? Subject);

/// <summary>The wire response: a bag of cards.</summary>
public sealed record CdsHookResponse(
    [property: JsonPropertyName("cards")] IReadOnlyList<CdsCard> Cards);

/// <summary>A CDS Hooks card. The shape comes from the CDS Hooks 1.1 spec.</summary>
public sealed record CdsCard(
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("indicator")] string Indicator,
    [property: JsonPropertyName("source")] CdsCardSource Source,
    [property: JsonPropertyName("detail")] string? Detail = null,
    [property: JsonPropertyName("uuid")] string? Uuid = null,
    [property: JsonPropertyName("overrideReasons"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<CdsCoding>? OverrideReasons = null,
    [property: JsonPropertyName("links"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<CdsLink>? Links = null);

/// <summary>Where this card came from — required by spec to attribute the alert.</summary>
public sealed record CdsCardSource(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("url")] string? Url = null,
    [property: JsonPropertyName("icon")] string? Icon = null);

public sealed record CdsCoding(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("system")] string System,
    [property: JsonPropertyName("display")] string Display);

public sealed record CdsLink(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("type")] string Type);
