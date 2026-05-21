# Fingerprint parity contract

The reasoning engine is implemented in .NET (this repo), Python ([`traceable-cds`](https://github.com/…)), and TypeScript (same repo). All three are required to produce **identical alert fingerprints** from identical inputs. This document is the contract.

## The fingerprint algorithm

For a `Fact`:

```
canonical = "Fact{name=" + name +
            ";value=" + canonical_value(value) +
            ";unit=" + (unit ?? "") +
            ";parents=[" + sorted(parent.Fingerprint).join(",") + "]}"
sha256_bytes = SHA256(UTF-8(canonical))
fingerprint = lowercase_hex(sha256_bytes[0..8])   # 16 hex chars
```

For an `Alert`:

```
canonical = "Alert{rule=" + ruleId +
            ";severity=" + severity.ToString().toLower() +
            ";because=[" + sorted(because.Fingerprint).join(",") + "]}"
sha256_bytes = SHA256(UTF-8(canonical))
fingerprint = lowercase_hex(sha256_bytes[0..8])
```

## Value canonicalization

| Value type | Canonical form |
| --- | --- |
| `null` | `null` |
| `bool` | `true` / `false` lowercase |
| `string` | the string itself, no quoting |
| `double` / `float` | `ToString("R", InvariantCulture)` (round-trippable) |
| `int` / `long` / other `IFormattable` | `ToString(null, InvariantCulture)` |
| anything else | `value.ToString()` |

Floating-point determinism is enforced by `Math.Round(value, 4)` in the rule code before constructing the derived Fact. eGFR, for example, is rounded to 4 decimal places.

## Parent / Because ordering

The sort is `Ordinal` on the fingerprint string. Python uses `sorted()` with default string ordering, TypeScript uses `[...].sort()` with the default comparator. These all agree on ASCII (hex digits).

## Canonical fixtures

The pinned values live in [`tests/Chiron.Cds.Engine.Tests/Fixtures/*.json`](../tests/Chiron.Cds.Engine.Tests/Fixtures). Each fixture's `expect_alert.fingerprint` is the canonical value the other engines must produce too.

Current canonical fingerprints:

| Fixture | Rule | Fingerprint |
| --- | --- | --- |
| `metformin_elderly_male_egfr_27` | `metformin.renal.contraindicated` | `841b53c0be39fc12` |
| `metformin_boundary_egfr_below_30` | `metformin.renal.contraindicated` | `ad728e0a1afdea34` |
| `cha2ds2_high_risk_elderly_female` | `cha2ds2_vasc.high_risk` | `19f10b65f2a59f9f` |
| `warfarin_nsaid_interaction` | `warfarin.nsaid.bleeding_risk` | `4ecd1b38d40696a8` |

Regenerate after a deliberate engine change: in `tests/Chiron.Cds.Engine.Tests/FingerprintCapture.cs`, remove `Skip = "…"`, run, capture the new values, paste them back into the JSON fixtures, and re-skip the helper.

## When parity drifts

If a rule's logic changes in a way that legitimately changes its fingerprint (e.g. CKD-EPI rounding precision), do all three at once:

1. Update the rule in the .NET engine; observe the new fingerprint.
2. Update the rule in the Python engine; the parity tests there should match.
3. Update the rule in the TypeScript engine; same.
4. Update the JSON fixtures in all three repos in lockstep.

If parity is genuinely impossible (different language number representations), the fixture format would grow a `tolerance` field and the test would assert canonical equivalence rather than byte equality. This is not currently needed — `Math.Round(value, 4)` is sufficient.
