# Architecture

This document explains how the spike is organized and why. It is **not** a directory listing — for that, read the source.

## Layers

1. **`Chiron.Cds.Engine`** — Pure-logic reasoning engine. Zero external dependencies. Easy to verify clean-room ports against the Python and TypeScript implementations because the runtime substrate is trivial.
2. **`Chiron.Cds.Shared`** — Wire-format DTOs and Chiron-specific error types shared between the engine and the web layer.
3. **`Chiron.Cds.Web`** — ASP.NET Core 9. SMART App Launch, FHIR I/O via Firely, CDS Hooks endpoints, the post-launch UI.

The engine doesn't reference the web layer; the web layer references the engine. The engine knows nothing about FHIR resources or SMART tokens — those are translated at the seams (`Mappers/FhirToFactMapper.cs`, `Mappers/AlertToCdsCardMapper.cs`).

## Reasoning engine

### Primitives

- `Fact(name, value, unit, parents, citations)` — a frozen value with a derivation history. The `Fingerprint` property is a stable SHA-256 over its canonical serialization, including the sorted fingerprints of its `parents`.
- `Citation(source, identifier, accessed, url?)` — references back to the source of truth. The engine refuses to register a `Rule` without at least one citation.
- `Rule(id, description, evaluate, citations)` — a delegate + metadata. The `evaluate` function takes an `EvaluationContext` and returns an `Alert?`.
- `Alert(ruleId, severity, message, because, citations, overrideOptions)` — what a rule produces when it fires. The `Fingerprint` is a stable SHA-256 over `(ruleId, severity, sorted(because.Fingerprints))`.
- `EvaluationContext` — per-evaluation accessors over the patient, labs, conditions, medications. Throws `MissingInputException` when a rule asks for an input that isn't there; the engine catches that and treats it as a no-fire.

### The fingerprint contract

Two engines (Python, TypeScript, .NET) given the same inputs must produce alerts with identical fingerprints. This is what makes the override log work across language/runtime boundaries.

The canonical serialization, as implemented:

```
Fact{name=<name>;value=<canonical value>;unit=<unit>;parents=[<sorted, comma-joined parent fingerprints>]}
Alert{rule=<ruleId>;severity=<severity lower>;because=[<sorted, comma-joined because fingerprints>]}
```

SHA-256 the UTF-8 bytes, take the first 8 → 16 hex chars lowercase.

Numeric canonicalization uses `Round(value, 4)` for derived facts (e.g. eGFR) and `IFormattable` with `InvariantCulture` for everything else. See [`docs/PARITY.md`](PARITY.md) for the exact rules.

### Rule packs

A "rule pack" is a static class with a static `IEnumerable<Rule> Rules` property. `Engine.RegisterPack(Assembly)` reflects over the assembly and registers everything it finds. The three packs in the spike:

- `Rules/Renal/MetforminRenalRule` — eGFR derived from creatinine + age + sex via CKD-EPI 2021; metformin contraindicated below 30 mL/min/1.73m².
- `Rules/Scores/CHA2DS2VAScRule` — atrial-fibrillation stroke risk score, composed so each component is a Fact in the alert's derivation.
- `Rules/Interactions/WarfarinNsaidRule` — warfarin + NSAID bleeding-risk interaction.

## SMART App Launch

Flow:

```
EHR Launch button
  → GET /smart/launch?iss=<fhirBase>&launch=<token>
    → LaunchController validates iss against TenantRegistry (anti-spoofing)
    → AuthorizationService.BuildAuthorizeUriAsync
      → SmartConfigurationClient fetches /.well-known/smart-configuration
      → PKCE verifier + challenge generated
      → PendingLaunch saved keyed by state
      → 302 to Cerner authorize endpoint
  → user authenticates at Cerner
  → GET /smart/callback?code=…&state=…
    → AuthorizationService.ExchangeCodeAsync
      → PendingLaunch retrieved by state (anti-replay)
      → POST to token endpoint with code + code_verifier + Basic auth
      → SmartSession built and saved
      → 302 to /app?session=<sessionId>
```

PKCE is mandatory per SMART App Launch v2 even for confidential clients. The client secret comes from `dotnet user-secrets` (development) or `Chiron__Tenants__cerner-code-sandbox__ClientSecret` (other environments) — never `appsettings.json`.

## FHIR client

`TenantFhirClient` wraps Firely's `FhirClient` with per-tenant config and the bearer token from the SMART session. One client per request; not thread-safe across tenants because the underlying HTTP client carries auth state.

`PatientChartFetcher` fans out: Patient + Conditions + Observations + active MedicationRequests + optional Encounter. All in parallel via `Task.WhenAll`.

`DiagnosticReportWriter` writes a Chiron alert back as a `DiagnosticReport` resource. The alert's full `Explain()` output goes into the report's `conclusion`, and the alert fingerprint goes into an `Identifier` so the report can be looked up by fingerprint later.

## Mappers

- `FhirToFactMapper.Project(PatientChart)` returns `EngineInputs(Patient, Medications, Labs, Conditions)`. Conservative: a resource missing a required field is logged and skipped, not thrown. Numeric values use `InvariantCulture` parsing to match the Python/TS engines.
- `AlertToCdsCardMapper.Map(Alert)` returns a `CdsCard`. The `detail` field is Markdown (CDS Hooks 1.1 spec says EHR clients render it inline). The `uuid` field is the alert's stable fingerprint.

## CDS Hooks

`DiscoveryController` answers `GET /cds-services` with a single service descriptor (`chiron-patient-view`). The prefetch declaration tells the EHR what resources to fetch on our behalf:

```json
{
  "patient":      "Patient/{{context.patientId}}",
  "conditions":   "Condition?patient={{context.patientId}}",
  "observations": "Observation?patient={{context.patientId}}&category=laboratory",
  "medications":  "MedicationRequest?patient={{context.patientId}}&status=active"
}
```

`PatientViewController` answers `POST /cds-services/chiron-patient-view`. It uses inbound prefetch when present and falls back to direct FHIR reads via the inbound `fhirAuthorization` bearer otherwise. Same engine, same cards as the SMART launch UI — the demo proves that the engine output is reachable through both channels.

## Multi-tenant

`TenantRegistry` is built once at startup from `ChironOptions` and is the only place tenants are looked up. The registry indexes by id and by FHIR base URL. Inbound SMART launches resolve the tenant by `iss` (which equals the FHIR base in SMART) — an unknown `iss` is rejected as `UntrustedIssuerException`.

Adding a tenant is a config change:

```json
"Chiron": {
  "Tenants": {
    "epic-sandbox": {
      "ClientId": "…",
      "FhirBaseUrl": "https://fhir.epic.com/…",
      "Scopes": "launch fhirUser openid user/Patient.read …"
    }
  }
}
```

No code change required.
