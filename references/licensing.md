# Licensing — the agent workflow

Docuoria is licensed software with a **free tier**. Enforcement lives entirely inside the
SDK; the scripts only surface the license journey. Your job is to keep the user moving:
recognize the three deterministic license failures, run the remediation scripts silently
where possible, and ask the user only for what genuinely requires them (their email, or a
pasted key).

## How license failures present

Every license failure is deterministic and machine-parseable. Match on the **message
prefix** (stderr `error.message`) and the **error code**:

| Prefix | `error.code` | Exit | Meaning |
| ------ | ------------ | ---- | ------- |
| `DOCUORIA_LICENSE_REQUIRED:` | `license-required` | **3** | No valid license. Offer the free license flow. |
| `DOCUORIA_FEATURE_DENIED:<key>` | `feature-denied` | 1 | The license excludes feature `<key>`. Upgrade required. |
| `DOCUORIA_RATE_LIMIT:<key>` | `rate-limit` | 1 | Usage window for `<key>` exhausted; it resets automatically. Upgrading raises the limit. |

The skill **enforces by default**: a command run with no valid license fails fast with
`DOCUORIA_LICENSE_REQUIRED:` (exit 3) and drives the free-license journey below. The license
is stored **beside the skill**, at `<skill-scripts-dir>/docuoria.license.json` — written by
`license-set.csx` / `license-acquire.csx` and read by the SDK, so the skill is self-contained
(no separate CLI install required). Resolution precedence: the `DOCUORIA_LICENSE` environment
variable, then the skill-local `docuoria.license.json`. Set `DOCUORIA_ENFORCEMENT=Disabled` to
turn enforcement off for local development, or `DOCUORIA_HOME` / `DOCUORIA_LICENSE_PATH` to
relocate the license file.

## Feature keys and free-tier limits

Access features gate a capability outright (`DOCUORIA_FEATURE_DENIED:<key>` when the
license excludes them); rate-limit features are metered per use inside a short
**60-second window** (`DOCUORIA_RATE_LIMIT:<key>` when exhausted). The windows are
throughput throttles, not monthly quotas — interactive use never notices them, and they
reset on their own within a minute.

**Access features:** `tool-access` (base engine access), `classify-ranked`
(ranked classification — used by `classify.csx`), `advanced-matching` (table,
page-geometry, and composite match rules wherever rules are evaluated), `output-csv`,
`output-json` (output generators), `template-store-api` (API-backed template store),
`authoring-diagnostics` (inspect / test-pattern / test-groups / dry-run /
evaluate-match-rule).

**Rate-limited features (free tier, per 60 seconds):**

| FeatureKey | Metered on | Free limit |
| ---------- | ---------- | ---------- |
| `execute` | Template execution, 1 per PDF | 10 / 60s |
| `classify` | Classification, 1 per call | 30 / 60s |
| `batch-execute` | 1 per `batch-execute.csx` run (plus per-PDF `execute`) | 3 / 60s |
| `ledger-append` | Ledger merge operations | 10 / 60s |
| `http-retrieval` | Executions containing HTTP retrieval steps | 30 / 60s |
| `python-step` | Python transformation step invocations | 10 / 60s |
| `survey` | 1 per `survey.csx` run | 5 / 60s |
| `regression-check` | 1 per `regression-check.csx` run | 5 / 60s |

## What to do on `DOCUORIA_LICENSE_REQUIRED:` (exit 3)

1. Tell the user, in plain language, that Docuoria needs a (free) license — one sentence,
   no licensing jargon.
2. Offer the self-serve path: ask for their email (this is a legitimate user question),
   then run:

   ```powershell
   dotnet script scripts/license-acquire.csx -- --email <their-email>
   ```

   On `{ "status": "ok", "stored": true }` the license is live — retry the original
   command and continue the task.
3. If acquisition reports `not-provisioned` or `checkout-required`, direct the user to the
   marketplace URL from `license-status.csx` (default `https://monaiq.com`). When they
   paste a key back, store it with:

   ```powershell
   dotnet script scripts/license-set.csx -- --key <pasted-key>
   ```

4. Retry the original command once the key is stored.

## On `DOCUORIA_FEATURE_DENIED:<key>` / `DOCUORIA_RATE_LIMIT:<key>`

Tell the user which capability (or usage window) their current license excludes and point
them at the marketplace URL to upgrade. A rate limit resets on its own within the usage
window — for non-urgent work, waiting is a valid option to offer.

## Key-handling rules (hard)

- **NEVER echo, print, log, or quote the license key** — not even partially, not in
  summaries, not in error reports. Pass it straight from the user's message into
  `license-set.csx -- --key <key>` and refer to it only as "your license key".
- **NEVER store the key anywhere except the local store** (`license-set.csx` /
  `license-acquire.csx` do this for you). No files, no environment exports, no notes.
- `license-remove.csx` deletes the stored key when the user asks to sign the machine out.

## Checking state proactively

`license-status.csx` is cheap and side-effect free — run it when the user asks anything
about their license, plan, usage, or "why did this stop working". It reports the licensed
state, feature keys, and per-feature rate-limit windows, and includes ready-made guidance
text when unlicensed.
