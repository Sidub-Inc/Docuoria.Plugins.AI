# Licensing — the agent workflow

Docuoria is licensed software with a **free tier**. Enforcement lives entirely inside the
SDK; the scripts only surface the license journey. Your job is to keep the user moving:
recognize the deterministic license failures, run the remediation scripts silently
where possible, and ask the user only for what genuinely requires them (signing in, or a
pasted key).

## How license failures present

Every license failure is deterministic and machine-parseable. Match on the **message
prefix** (stderr `error.message`) and the **error code**:

| Prefix | `error.code` | Exit | Meaning |
| ------ | ------------ | ---- | ------- |
| `DOCUORIA_LICENSE_REQUIRED:` | `license-required` | **3** | No valid license. Offer the free license flow. |
| `DOCUORIA_LICENSE_INACTIVE:<reason>` | `license-inactive` | **3** | A license exists but is not active — `Expired`, `PlanSuspended`, or `PlanCancelled`. A suspended plan is reversible by resuming billing; a cancelled one needs a new purchase. |
| `DOCUORIA_LICENSE_INVALID:` | `license-invalid` | **3** | The stored credential could not be validated. Remove it and acquire a new one — retrying will not help. |
| `DOCUORIA_LICENSE_UNAVAILABLE:` | `license-unavailable` | 1 | The licensing service is unreachable **and** the offline grace period has elapsed. Not the user's fault and not a license problem: tell them to reconnect and retry. |
| `DOCUORIA_OFFERING_UNAVAILABLE:` | `offering-unavailable` | 2 | The offering or seller named does not exist in the catalog — a mistyped id, or ids from one deployment used against another. A configuration mistake, not a licence problem. |
| `DOCUORIA_FEATURE_DENIED:<key>` | `feature-denied` | 1 | The license excludes feature `<key>`. Upgrade required. |
| `DOCUORIA_RATE_LIMIT:<key>` | `rate-limit` | 1 | Usage window for `<key>` exhausted; it resets automatically. Upgrading raises the limit. |
| — (`license-acquire.csx` only) | `checkout-unavailable` | 1 | Buyer sign-in is not available from the scripts, so self-serve acquisition cannot complete here. `detail` carries the purchase URL — send the user there and store the pasted key with `license-set.csx`, or use `docuoria license acquire` in the .NET CLI. |

**The exit code is the routing signal.** Exit **3** means the license itself needs attention
and nothing will succeed until it does — route the user into the acquisition or renewal
journey. Exit **1** means this one call failed while the license is fine — the feature is not
in their plan, the window is exhausted, or the service is briefly unreachable. Do not push a
user toward buying anything on an exit 1.

Offline use depends on the host. Long-running SDK hosts and the CLI hold the last verified
authorization in memory and keep working for a bounded grace period when the licensing service
cannot be reached. The scripts start a fresh process per command, so that cache is empty every
time: a script run with no network fails with `DOCUORIA_LICENSE_UNAVAILABLE:` (exit 1). Tell
the user to reconnect and retry — it is not a signal to re-acquire a licence, and it is not a
problem with their licence.

The skill **enforces by default**: a command run with no valid license fails fast with
`DOCUORIA_LICENSE_REQUIRED:` (exit 3) and drives the free-license journey below. The scripts
resolve a licence in this order: the `DOCUORIA_LICENSE` environment variable; the skill-local
file `<skill-scripts-dir>/docuoria.license.json` (or the path in `DOCUORIA_LICENSE_PATH`);
the user-home file `~/.docuoria/license.json` — setting `DOCUORIA_HOME` replaces both file
locations with `%DOCUORIA_HOME%/license.json`. So a licence acquired with the
.NET CLI (`docuoria license acquire`, which writes `~/.docuoria/license.json`) is picked up by
the skill, and a skill install with no CLI is still self-contained: `license-set.csx` writes
the skill-local file. `license-remove.csx` removes the skill-local file only — use
`docuoria license remove` for the user-home one. Set `DOCUORIA_ENFORCEMENT=Disabled` to turn
enforcement off for local development.

## Feature keys and free-tier limits

Access features gate a capability outright (`DOCUORIA_FEATURE_DENIED:<key>` when the
license excludes them); rate-limit features are metered per use inside a short
**60-second window** (`DOCUORIA_RATE_LIMIT:<key>` when exhausted). The windows are
throughput throttles, not monthly quotas — interactive use never notices them, and they
reset on their own within a minute.

**Access features included on the free tier:** `tool-access` (base engine access),
`classify-ranked` (ranked classification — used by `classify.csx`), `advanced-matching`
(table, page-geometry, and composite match rules wherever rules are evaluated),
`output-csv`, `output-json` (output generators), `authoring-diagnostics` (inspect /
test-pattern / test-groups / dry-run / evaluate-match-rule).

**Access feature that is Pro only:** `template-store-api` (API-backed shared template
store). On the free tier this returns `DOCUORIA_FEATURE_DENIED:template-store-api` — the
local filesystem template store is unaffected and remains the default.

**Rate-limited features (per 60 seconds):**

| FeatureKey | Metered on | Free | Pro |
| ---------- | ---------- | ---- | --- |
| `execute` | Template execution, 1 per PDF | 10 / 60s | 120 / 60s |
| `classify` | Classification, 1 per call | 30 / 60s | 300 / 60s |
| `batch-execute` | 1 per `batch-execute.csx` run (plus per-PDF `execute`) | 3 / 60s | 30 / 60s |
| `ledger-append` | Ledger merge operations | 10 / 60s | 120 / 60s |
| `survey` | 1 per `survey.csx` run | 5 / 60s | 30 / 60s |
| `regression-check` | 1 per `regression-check.csx` run | 5 / 60s | 30 / 60s |

Two further rate-limited keys, `http-retrieval` and `python-step`, are metered only for SDK
hosts that register those steps; neither can be expressed in template JSON nor reached from
any script, so a skill user never sees them.

## What to do on `DOCUORIA_LICENSE_REQUIRED:` (exit 3)

1. Tell the user, in plain language, that Docuoria needs a (free) licence — one sentence,
   no licensing jargon.
2. Pick the path the machine supports. Both end with the same retry; neither collects an
   email — the buyer is identified by the account they sign in with.

   **The .NET CLI is installed (preferred — no key passes through the chat).** Have the
   user run:

   ```powershell
   docuoria license acquire
   ```

   It signs them in with a device code in their browser and stores the licence in
   `~/.docuoria/license.json`, which the skill reads. Retry the original command.

   **Scripts only.** Buyer sign-in is not available from the scripts: `license-acquire.csx`
   exits 1 with `checkout-unavailable` and the purchase URL in `detail`. Send the user to
   that URL (also `purchaseUrl` in `license-status.csx` output; default
   `https://monaiq.com/marketplace`, product "Docuoria"). When they paste the key back,
   store it with:

   ```powershell
   dotnet script scripts/license-set.csx -- --key <pasted-key>
   ```

3. Retry the original command once the licence is stored.

A `checkout-required` status from acquisition applies only to **paid** offerings (browser
checkout, then `license-set.csx`); it is not part of the free path.

## On `DOCUORIA_FEATURE_DENIED:<key>` / `DOCUORIA_RATE_LIMIT:<key>`

Tell the user which capability (or usage window) their current license excludes and point
them at the marketplace URL to upgrade. A rate limit resets on its own within the usage
window — for non-urgent work, waiting is a valid option to offer.

## Licence failures mid-batch (`batch-execute.csx`)

A licence failure part-way through a batch — rate limit, feature denied, or licence
required/inactive/invalid/unavailable — is **not** swallowed as a per-PDF `status: "failed"`.
Processing stops at the PDF that hit it; outputs for PDFs already completed **are** written;
then the standard licence envelope goes to stderr with its usual code and exit (3 for
`license-required` / `license-inactive` / `license-invalid`, 1 for `rate-limit` /
`feature-denied` / `license-unavailable`, 2 for `offering-unavailable`). `detail` states how
many PDFs were processed, which outputs were written, which PDFs remain, and that re-running
the same command with `--append` after the window resets completes the batch. stdout stays
empty. For a rate limit, waiting out the 60-second window and re-running with `--append` is
the whole remedy.

## Key-handling rules (hard)

- **NEVER echo, print, log, or quote the license key** — not even partially, not in
  summaries, not in error reports. Pass it straight from the user's message into
  `license-set.csx -- --key <key>` and refer to it only as "your license key".
- **NEVER store the key anywhere except the local store** (`license-set.csx` writes the
  skill-local file; `docuoria license acquire` writes `~/.docuoria/license.json`). No other
  files, no environment exports, no notes.
- `license-remove.csx` deletes the skill-local key when the user asks to sign the machine out;
  a licence stored by the CLI is removed with `docuoria license remove`.

## Checking state proactively

`license-status.csx` is cheap and side-effect free — run it when the user asks anything
about their license, plan, usage, or "why did this stop working". It reports the licensed
state, feature keys, per-feature rate-limit windows (`currentUsage`, `limit`, `unlimited`,
`percentage`, `windowSeconds`), the `marketplaceUrl` and `purchaseUrl`, and includes
ready-made guidance text when unlicensed.
