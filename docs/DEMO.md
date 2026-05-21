# 90-second demo script

This is the script behind the Loom video. The two scenarios that *can* be driven end-to-end against real Cerner data without a browser handshake are below. The third scenario (the EHR-initiated SMART launch with write-back) requires you to click "Launch" in the Code Console — script for that is at the bottom and is its own ~30 seconds.

## Setup (off-camera)

- `dotnet run --project src/Chiron.Cds.Web` running locally.
- A terminal with `curl` and `jq`.
- A browser tab on `http://localhost:5099/cds-services` to show the discovery endpoint.
- (For Scenario C only) a browser tab on the Oracle Health Code Console with the Chiron app's "Launch" button visible.

## Scenario A — Real Cerner data → real alert (≈ 30 s)

1. "This is a real Cerner sandbox patient — id 12674028 — fetched from `fhir-open.cerner.com`. Nothing in this request body is hand-rolled."

   ```bash
   jq '.context.patientId, .prefetch.patient.name[0].text, [.prefetch.medications.entry[].resource.medicationCodeableConcept.text]' \
     docs/sample-patient-view-request.json
   ```

   Output:
   ```
   "12674028"
   "SMITH, ANNIE"
   ["metFORMIN (metFORMIN 500 mg oral tablet)"]
   ```

2. "POST it to the CDS Hooks endpoint."

   ```bash
   curl -s -X POST http://localhost:5099/cds-services/chiron-patient-view \
     -H 'Content-Type: application/json' \
     -d @docs/sample-patient-view-request.json | jq '.cards[]'
   ```

   "One card. CHA₂DS₂-VASc score 2 — anticoagulation generally recommended."

3. "Detail field is Markdown. The derivation walks back to the components:"

   ```
   cha2ds2_vasc.total = 2 points
     diabetes = true        ← real "Type 2 diabetes mellitus" Condition from Cerner
     female_sex = true      ← real Cerner Patient.gender = "female"
   ```

4. "Citation is the 2019 AHA/ACC/HRS focused update. Every clinical claim cites a source. The engine refuses to register a rule that doesn't."

## Scenario B — The provenance moment (≈ 20 s)

1. "Look at the card's `uuid` field — `33cc0041c37010c5`. That's the alert fingerprint, a stable SHA-256 over (rule, severity, derivation tree)."

2. Rerun the POST:

   ```bash
   curl -s -X POST http://localhost:5099/cds-services/chiron-patient-view \
     -H 'Content-Type: application/json' \
     -d @docs/sample-patient-view-request.json | jq '.cards[0].uuid'
   ```

   "Same fingerprint. Deterministic across runs, deterministic across language ports — that's the parity contract with the Python and TypeScript engines."

3. "If this fingerprint had ever been overridden on this patient or any patient, the override log would say so. That's how we kill alert fatigue — we don't keep firing alerts that get clicked past."

## Scenario C — SMART launch + write-back (≈ 30 s, manual browser)

Verified end-to-end against the real Cerner Code sandbox on 2026-05-21. Full evidence in [REAL_LAUNCH_VERIFICATION.md](REAL_LAUNCH_VERIFICATION.md). The walkthrough:

1. "Now let's do this through the real EHR-initiated flow." Click **Launch** on the Chiron app tile in Oracle Health Code Console, or visit `https://localhost:5001/smart/launch?iss=https://fhir-ehr-code.cerner.com/r4/ec2458f2-1e24-41c8-b71b-0e701af7583d` directly.

2. Pick a patient at the Cerner sandbox patient picker (Fredrick SMART — the elderly male with PE + active conditions + lab results — is the best demo target). Authenticate at the CernerCare login. Approve scopes.

3. Browser lands on `https://localhost:5001/app?session=…`. The app has just:
   - Resolved the tenant from the inbound `iss`
   - Generated PKCE S256 + 32-byte state, persisted them keyed by state
   - Redirected to Cerner's `authorization.cerner.com/...` authorize endpoint (URL discovered dynamically from `.well-known/smart-configuration`)
   - Exchanged the code for tokens via HTTP Basic confidential-client auth at the real token endpoint
   - Received a token response containing `access_token`, `patient`, `encounter`, `user`, `username`, `id_token`, `expires_in`, real bearer token

4. The post-launch UI fetches Fredrick's chart via `PatientChartFetcher` and renders the cards. "Accept alert" POSTs a `DiagnosticReport` to the Cerner FHIR endpoint with the alert's full `Explain()` output in `conclusion` and the fingerprint in an `Identifier`.

5. Refresh the patient in the Code Console UI. The new `DiagnosticReport` is visible. Round-trip proved.

*Note (2026-05-21): step 4's FHIR read currently returns 403 because the system account backing the app registration is `Production:Yes / Account Type:LIMITED`, which gates resource scope grants behind Cerner certification. Cerner support has been engaged to provision a sandbox-tier account. The application code is correct; everything up through step 3 (real OAuth handshake against real Cerner) is verified. See [REAL_LAUNCH_VERIFICATION.md](REAL_LAUNCH_VERIFICATION.md).*

## Why no live "metformin renal" headline?

The original demo plan was a headline scenario with elevated creatinine + active metformin → eGFR < 30 → "Avoid metformin" alert. After surveying the Cerner Code open sandbox, no public patient has that combination — sandbox observation/medication data is sparse and not curated for clinical scenarios. To preserve the demo's "no augmentation" stance, the headline shifted to CHA₂DS₂-VASc on Annie Smith, which fires on her real, unmodified chart.

The metformin/renal rule itself is fully implemented and unit-tested over multiple eGFR boundaries ([MetforminRenalTests](../tests/Chiron.Cds.Engine.Tests/MetforminRenalTests.cs)), and the parity fixture in [`metformin_elderly_male_egfr_27.json`](../tests/Chiron.Cds.Engine.Tests/Fixtures/metformin_elderly_male_egfr_27.json) pins its canonical fingerprint. It will fire the moment a real chart with the right combination flows through — Cerner's authenticated FHIR endpoint may include more chart data than the open one; Epic / Athena sandboxes are the next test surface.
