# Setup

## Prerequisites

- .NET 9 SDK (`dotnet --version` should report ≥ 9.0.0).
- An account on [Oracle Health Code](https://code.cerner.com) (formerly Cerner Code).

## Register the app at code Console

Already done for this spike. The current registration uses:

| Field | Value |
| --- | --- |
| App name | Chiron CDS Spike |
| App type | Provider |
| Client ID | `5df0b845-a970-4ce9-ad57-dd16e779bbbc` |
| FHIR Spec | R4 |
| Authorized | Yes |
| Redirect URI | `https://localhost:7099/smart/callback` |
| Scopes | `launch fhirUser openid user/Patient.* user/Condition.* user/Observation.* user/Encounter.* user/Practitioner.read user/DiagnosticReport.* user/ServiceRequest.read user/ServiceRequest.search` |

Changes at the Code Console take ~15 minutes to propagate.

## Set the client secret

The client secret is in the Code Console under the app's "Authentication" tab. Set it via user-secrets in development:

```bash
dotnet user-secrets set "Chiron:Tenants:cerner-code-sandbox:ClientSecret" "<paste>" \
  --project src/Chiron.Cds.Web
```

For non-development environments, set the env var `Chiron__Tenants__cerner-code-sandbox__ClientSecret`. Never commit the secret to source.

## Run

```bash
dotnet run --project src/Chiron.Cds.Web
```

The app listens on `https://localhost:7099` (HTTPS) and `http://localhost:5099` (HTTP). HTTPS is required for the OAuth redirect; HTTP is fine for the CDS Hooks endpoints during local development.

## Launch from the EHR

1. Open Oracle Health Code Console.
2. Find your app's tile.
3. Click **Launch**.
4. Choose a synthetic patient (e.g. `Sandbox Smart`). Note the patient id.
5. Authenticate as the test physician.
6. Approve the SMART scopes.
7. The browser lands on `https://localhost:7099/app?session=…` and the alert renders.

## Hit the CDS Hooks endpoints directly

```bash
# Discovery
curl -s http://localhost:5099/cds-services | jq .

# Patient-view with inline prefetch
curl -s -X POST http://localhost:5099/cds-services/chiron-patient-view \
  -H 'Content-Type: application/json' \
  -d @docs/sample-patient-view-request.json | jq .
```

A sample request body is in [`docs/sample-patient-view-request.json`](sample-patient-view-request.json).

## Tests

```bash
dotnet test                           # everything
dotnet test --filter Category=Live    # tests that hit the real Cerner sandbox
```

The integration tests boot the app via `WebApplicationFactory<Program>`. Live tests degrade gracefully (no failure) on network errors so flaky CI doesn't block PRs.
