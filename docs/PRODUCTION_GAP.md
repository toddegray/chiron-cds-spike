# What would change for production

The spike was built to production quality where doing so cost no extra time, and to clear-shortcut quality otherwise. This file catalogues the shortcuts.

## Token / session storage

**Spike:** in-memory `ConcurrentDictionary` (`SmartLaunch/TokenStore.cs`).
**Production:** Data Protection API-encrypted Redis (or Postgres) so:
- Sessions survive process restart.
- Tokens are never cleartext at rest.
- The app can scale horizontally.

The `ITokenStore` interface is in place to make this a swap, not a rewrite.

## `id_token` validation

**Spike:** the `id_token` is captured in the session but its JWS signature is **not** validated against Cerner's JWKS.
**Production:** validate against `jwks_uri` from `.well-known/smart-configuration` using `Microsoft.IdentityModel.Tokens.JsonWebTokenHandler`. Cache the JWKS for 1 hour with a hard fallback re-fetch on signature failure.

## Refresh token persistence

**Spike:** refresh tokens are kept in the same in-memory store as access tokens, alongside them.
**Production:** refresh tokens are sensitive credentials with a longer lifetime than access tokens. They need separate, more conservative storage (column-encrypted, audit-logged), and a background sweeper that revokes refresh tokens after configurable idle time.

## CDS Hooks endpoint authentication

**Spike:** `POST /cds-services/chiron-patient-view` is unauthenticated, for ease of demo.
**Production:** the CDS Hooks 1.1 spec relies on mutual TLS or a JWT in the `Authorization` header signed with a key the EHR knows. Implement the JWT path via `JsonWebTokenHandler` and validate against a configured JWKS per EHR.

## Drug knowledge base

**Spike:** `WarfarinNsaidRule` carries a hand-rolled list of NSAID generic names.
**Production:** license First Databank, Lexicomp, or Multum and consume their interaction database. The engine's `Rule` contract doesn't change — the rule's body changes to query the vendor's API.

## `ServiceRequest` write

**Spike:** the Cerner Code sandbox doesn't accept `ServiceRequest.create` (read-only resource). Not implemented.
**Production:** `ServiceRequestWriter` symmetric to `DiagnosticReportWriter`, gated on the tenant's declared write capabilities.

## Logging and tracing

**Spike:** `ILogger<T>` with default JSON console formatter, no PHI ever logged.
**Production:** OpenTelemetry traces + metrics exported to the customer's observability backend; structured logs to the customer's SIEM; PHI scrubbing middleware on the inbound and outbound boundaries.

## Per-tenant overrides

**Spike:** rules are global. The same metformin threshold applies regardless of which EHR launched us.
**Production:** rules carry a tenant predicate. Some institutions ramp metformin at eGFR 45 (instead of 30) for safety; that's a per-tenant config override on the rule itself, not a new rule.

## Engine packs

**Spike:** rule packs are statically registered at startup.
**Production:** load rule packs from versioned artifacts (signed `.dll` or `.wasm` per rule pack version). Engineering can deploy a new rule pack without redeploying the host.

## Override log persistence

**Spike:** in-memory; resets on restart.
**Production:** SQLite (matching the Python/TS engines) or Postgres. The override log is *the* source of truth for the alert-fatigue dashboard, so it has to be durable and queryable.

## Multi-instance deployment

**Spike:** single instance, single process.
**Production:** the only stateful components are the token store and the override log. Both have swap-in production implementations called out above. Everything else is stateless (the engine, the FHIR client, the mappers).

## Authentication of operators

**Spike:** the post-launch UI is reachable by anyone with the session id.
**Production:** the session id is HttpOnly + Secure + SameSite=Strict cookie; the cookie is signed; logout endpoint invalidates server-side.

## Configuration validation

**Spike:** `Validate(o => o.Tenants.Count > 0, …)` on startup.
**Production:** schema-validate the entire `ChironOptions` against a JSON schema; per-tenant smoke test (fetch `.well-known/smart-configuration` for each tenant) on startup so a misregistered tenant fails fast.

## Recruiter / hiring-manager note

These gaps are documented because knowing what production-grade means — and consciously choosing to clip it for a spike — is part of senior engineering. None of these gaps would be acceptable in a production deployment. None of them are technically interesting to implement; they're "press the button" given the architecture already in place.
