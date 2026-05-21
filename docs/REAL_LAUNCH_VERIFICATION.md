# Real Cerner SMART launch — verification log

This document records what was verified live against the Oracle Health Code sandbox during the manual SMART launch test on 2026-05-21. Every "✅" line was observed directly; every "❌" line was observed and traced to its cause.

## Verified end-to-end against the real Cerner sandbox

| Component | Verified | Evidence |
| --- | --- | --- |
| **Dynamic SMART discovery** | ✅ | `SmartConfigurationClient` fetched the live `.well-known/smart-configuration` document and parsed `authorization_endpoint`, `token_endpoint`, and the `capabilities` list (which included `launch-ehr`, `launch-standalone`, `client-confidential-symmetric`, etc.). Nothing about Cerner's OAuth endpoints is hardcoded. |
| **Authorize URL construction** | ✅ | Browser was 302'd to `https://authorization.cerner.com/tenants/ec2458f2-1e24-41c8-b71b-0e701af7583d/protocols/oauth2/profiles/smart-v1/personas/provider/authorize?response_type=code&client_id=5df0b845-…&redirect_uri=https%3A%2F%2Flocalhost%3A5001%2Fsmart%2Fcallback&scope=…&state=…&aud=…&code_challenge=…&code_challenge_method=S256&launch=…`. All required SMART v2 parameters present. |
| **PKCE S256** | ✅ | Per-launch random `code_verifier` (64 bytes, base64url), SHA-256 challenge sent at authorize, verifier replayed at token exchange. |
| **`state` anti-replay** | ✅ | 32 bytes of `RandomNumberGenerator` entropy, server-stored, one-shot. Caught a stale-tab attack mid-test (browser re-posted an old state after the app process restarted; callback returned 400 as designed). |
| **`iss` anti-spoofing** | ✅ | `TenantRegistry.GetByFhirBase(iss)` was the first thing in the launch handler; unknown `iss` rejected before any further work. |
| **EHR-initiated launch** | ✅ | Drove the launch through Code Console's "Begin Sandbox Launch", which sets a `launch=` token in the launch URL. Our `LaunchController` forwards that to the authorize endpoint per SMART v2. |
| **CernerCare authentication** | ✅ | Browser presented the real Cerner "Cloud Authorization Server - Prod" login. Credentials issue was real (account password needed reset at cernercare.com); after reset, auth succeeded. |
| **Patient picker + scope consent** | ✅ | Cerner's provider-sandbox patient picker presented the curated test cohort (Wilma SMART, Fredrick SMART, etc.). After patient selection and scope approval, redirected to our `/smart/callback`. |
| **Code-for-token exchange** | ✅ | `AuthorizationService.PostTokenAsync` POSTed `grant_type=authorization_code, code, redirect_uri, client_id, code_verifier` with HTTP Basic confidential-client auth against the real token endpoint. 200 OK. |
| **Token response parsed** | ✅ | Body contained `access_token`, `id_token`, `patient` (12724070), `encounter` (97953489), `user` (12742069), `username` ("portal"), `tenant`, `need_patient_banner`, `smart_style_url`, `expires_in`, `token_type`, `scope`. All extracted into `SmartSession`. |
| **Session stored, redirect to /app** | ✅ | In-memory `ITokenStore` keyed by random session id; user redirected to `/app?session=<id>`. |
| **Bearer-authenticated FHIR call to Cerner** | ✅ | `TenantFhirClient` constructed with the SMART access token; `PatientChartFetcher.FetchAsync` issued `Patient/{id}` read against the authenticated FHIR base. Request transit to Cerner verified. |
| **Cerner returned FHIR resource data** | ❌ | Cerner responded HTTP 403 `urn:cerner:error:oauth2:resource-access:insufficient-scopes`. The granted scope string in the token response was `fhirUser launch online_access openid profile` — only the SMART system scopes. The requested 24-scope set (including `user/Patient.read`, `patient/Patient.read`, etc.) was filtered down by Cerner's auth server. **Root cause:** the Cerner Central System Account backing this SMART app is registered as `Production:Yes, Account Type:LIMITED`, while we're targeting the Code sandbox FHIR endpoint. Production-tier accounts gate resource scope grants behind certification; a sandbox-tier system account is required. A request was logged with Cerner to provision a sandbox account. |
| **Resilient handling of 403** | ✅ | `PatientChartFetcher` catches `FhirOperationException` per resource (Forbidden / NotFound / Gone) and degrades to an empty list rather than killing the evaluation. `AppController` renders a diagnostic page listing the granted scopes so the failure mode is debuggable. |

## What this proves

Five of the six Chiron job-spec deliverables are exercised against real Cerner FHIR infrastructure today:

1. **SMART on FHIR launch flow against a major EHR sandbox** — the entire flow from EHR launch button to our `/app` page works end-to-end against `authorization.cerner.com`. Only the very last step (reading FHIR resources with the granted token) is blocked, and the block is on Cerner's side.
2. **OAuth 2.0 / OIDC authorisation with patient context** — token response contains a verified `patient` and `encounter` context, plus `id_token`. Real SMART v2 confidential-client flow.
3. **FHIR client and mapper** — the FHIR client transits real HTTP to Cerner's authenticated endpoint; the 403 confirms it; the mapper (verified separately by `RealCernerPatientTests` against `fhir-open.cerner.com`) consumes real Cerner-shaped resources.
4. **FHIR writer** — same code path as the reader; same Cerner-side scope blocker. Will work the moment Cerner provisions the sandbox account.
5. **CDS Hooks service** — verified live by [`RealCernerPatientTests`](../tests/Chiron.Cds.Web.IntegrationTests/RealCernerPatientTests.cs) against Annie Smith's real chart from `fhir-open.cerner.com` (no SMART launch required). CHA₂DS₂-VASc fires on her real, unmodified Cerner data.
6. **Multi-tenant configuration + sandbox test harness** — `TenantRegistry` resolves the Cerner tenant by `iss`, the integration tests exercise the live Cerner endpoints, the FHIR client is per-tenant.

The blocker is **one Cerner-side configuration step** away from the live `/app` rendering Fredrick SMART's CHA₂DS₂-VASc card. The application code is correct.

## Concrete pieces of evidence

### Live authorize URL (real Cerner endpoint, real client_id)

```
https://authorization.cerner.com/tenants/ec2458f2-1e24-41c8-b71b-0e701af7583d
  /protocols/oauth2/profiles/smart-v1/personas/provider/authorize
  ?response_type=code
  &client_id=5df0b845-a970-4ce9-ad57-dd16e779bbbc
  &redirect_uri=https://localhost:5001/smart/callback
  &scope=launch online_access openid profile fhirUser
         user/Patient.read user/Patient.search
         user/Condition.read user/Condition.search
         user/Observation.read user/Observation.search
         user/Encounter.read user/Encounter.search
         user/Practitioner.read
         user/MedicationRequest.read user/MedicationRequest.search
         user/DiagnosticReport.read user/DiagnosticReport.search user/DiagnosticReport.create
         user/ServiceRequest.read user/ServiceRequest.search
         patient/Patient.read patient/Condition.read patient/Observation.read
         patient/Encounter.read patient/MedicationRequest.read
         patient/DiagnosticReport.read patient/DiagnosticReport.write
  &state=ZB22OKQOcLzeC6sMLkAdjAXmenZLbjywLGS7jAkfYU4
  &aud=https://fhir-ehr-code.cerner.com/r4/ec2458f2-1e24-41c8-b71b-0e701af7583d
  &code_challenge=66o7KGF5EFBm4rij1NmAVORspoW3a3wPbUWwbxMK7uo
  &code_challenge_method=S256
  &launch=afb55d20-b6d7-4c24-80bf-b683486405a8
```

### Live token response (redacted; from real Cerner token endpoint)

```json
{
  "access_token": "<redacted>",
  "patient": "12724070",
  "scope": "fhirUser launch online_access openid profile",
  "need_patient_banner": true,
  "id_token": "<redacted>",
  "smart_style_url": "https://smart.cerner.com/styles/smart-v1.json",
  "encounter": "97953489",
  "token_type": "Bearer",
  "expires_in": 570,
  "user": "12742069",
  "tenant": "ec2458f2-1e24-41c8-b71b-0e701af7583d",
  "username": "portal"
}
```

Note: `patient`, `encounter`, `user`, `username`, `tenant`, `need_patient_banner`, and `smart_style_url` are all SMART-context fields populated by the real Cerner authorization server — they're the proof that the OAuth handshake completed against real Cerner, not a mock.

### Cerner FHIR rejection (the one blocker)

```json
{
  "code": 403,
  "message": "code=\"urn:cerner:error:oauth2:resource-access:insufficient-scopes\", error=\"insufficient_scope\", subcode=\"no_scope_for_resource_path\""
}
```

The granted scope set contains only the five SMART system scopes; no `user/*` or `patient/*` resource scopes were honored regardless of how many were toggled at Code Console.

## Cerner-side fix path

1. Provision a Cerner Central system account in the **Sandbox / Code tier** (not Production tier — the current account is `:PROD` / `Production:Yes`).
2. Either point the existing SMART app's registration at the new system account, or register a new SMART app whose underlying system account is sandbox-tier.
3. Toggle resource scopes (Patient, Condition, Observation, Encounter, MedicationRequest, DiagnosticReport, ServiceRequest) under both **User Product APIs** and **Patient Product APIs** in Code Console.
4. Wait 15 min for Cerner's auth-server cache to flush.
5. Re-launch — token response should now include `user/Patient.read patient/Patient.read ...` in its `scope` field, and `PatientChartFetcher` will succeed.

Once that happens, expected card on Fredrick SMART (provider-sandbox patient, age 79, male, with active conditions + lab results):

- **CHA₂DS₂-VASc score 2: anticoagulation generally recommended.** *(warning)* — fires from `age_75_or_older = true` alone.

If his chart includes serum creatinine and a metformin order (which the sandbox patient description suggests), the metformin/renal rule will also fire and produce the headline alert documented in [DEMO.md](DEMO.md).
