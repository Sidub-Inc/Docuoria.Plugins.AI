# Agent Scripts

This directory hosts the **agent-facing CLI surface** for `Docuoria`. Each script
is a [`dotnet-script`](https://github.com/dotnet-script/dotnet-script) `.csx` file that
binds a single SDK verb to a deterministic JSON contract. The scripts are designed for
non-interactive automation (LLM agents, CI jobs, shell pipelines) and uphold a strict
output contract:

- **Successful runs** emit a single line of UTF-8 JSON to **stdout**, exit code `0`.
- **Errors** emit a single `{"error":{"code","message","detail"}}` line to **stderr**,
  non-zero exit code.
- All payloads serialize via `DocuoriaJsonOptions.Default` (camelCase, `$kind`
  discriminator for polymorphic results, `WhenWritingNull` ignore policy — see Classify
  for the explicit-null exception). Enums serialize as **integers** unless a type has a
  custom converter (extraction-source `mode`, `standardField`, and `$kind` are strings).

Every script `#load "_common.csx"` to share host bootstrap, argument parsing
(`Cli.Require` / `Cli.Get` / `Cli.Has`), template-store registration, PDF stream
loading, and JSON writers.

> Classification returns a composite `classificationScore`
> (`requirementsSatisfied ? ruleConfidence × (0.5·specificity + 0.3·quantity + 0.2·coverage) : 0`).
> `ClassifyAsync` returns the single best match (`null` when none), `ClassifyRankedAsync`
> returns the ranked list; both throw `InvalidOperationException` when no store is registered.
> `EvaluateMatchAsync` rule confidence is binary in v1.4 (`1.0` / `0.0`).

> **Distribution:** this directory is the **source** for the AI plugin's `scripts/`
> folder. `skills/build.ps1` copies these `.csx` files into `dist/docuoria/scripts/`
> and rewrites the SDK `#r` line in `_common.csx` to point at the bundled
> `assets/lib/Docuoria.dll`. In-repo development uses the relative
> `bin/Release/...dll` path; downstream consumers receive the bundled DLL.

## Contents

- [Installation](#installation)
- [Common Store Parameters](#common-store-parameters) — `--store-path` / `--store-url` / `--store-key`
- [Error JSON Shape](#error-json-shape) — the stderr envelope and common `code` values
- **Discovery & authoring:** [`inspect`](#inspectcsx) · [`test-pattern`](#test-patterncsx) · [`test-groups`](#test-groupscsx) · [`validate-template`](#validate-templatecsx) · [`schema-info`](#schema-infocsx)
- **Run the pipeline:** [`dry-run`](#dry-runcsx) · [`execute`](#executecsx) · [`batch-execute`](#batch-executecsx)
- **Classification:** [`classify`](#classifycsx) · [`evaluate-match`](#evaluate-matchcsx)
- **Template store:** [`list-templates`](#list-templatescsx) · [`load-template`](#load-templatecsx) · [`save-template`](#save-templatecsx)
- **Batch & change safety:** [`survey`](#surveycsx) · [`regression-check`](#regression-checkcsx)
- [Internals — `_common.csx`](#internals--_commoncsx)

## Installation

```powershell
dotnet tool install -g dotnet-script
# build the SDK once so _common.csx can reference the local DLL
dotnet build src/libs/Docuoria/Docuoria.csproj -c Debug
```

Run any script with:

```powershell
dotnet script scripts/<name>.csx -- --pdf path\to\file.pdf
```

The `--` separator forwards subsequent tokens as script arguments (exposed as the
`Args` global, an `IList<string>`).

## Common Store Parameters

Scripts that access the template store (`classify`, `evaluate-match`,
`list-templates`, `load-template`, `save-template`, `survey`, `regression-check`,
`batch-execute`) accept these shared flags to configure the store backend. When neither
`--store-path` nor `--store-url` is provided, the local store defaults to `./templates`
(exception: `batch-execute` has no default and **requires** one of the two). Because the
process working directory varies by environment, **pass `--store-path <dir>`
explicitly** whenever the templates live outside the default `./templates`
location.

| Flag           | Default        | Description                                                              |
| -------------- | -------------- | ------------------------------------------------------------------------ |
| `--store-path` | `./templates`  | Local file-system template store directory.                              |
| `--store-url`  | _(none)_       | API template store URL (mutually exclusive with `--store-path`).         |
| `--store-key`  | _(none)_       | Function key for API store authentication (used with `--store-url`).     |

## Error JSON Shape

```json
{
  "error": {
    "code": "kebab-case-identifier",
    "message": "Human-readable summary.",
    "detail": "Optional stack trace or full exception text."
  }
}
```

Common `code` values: `pdf-not-found`, `parse-error`, `already-exists`, `no-store`,
`unhandled`, `bad-format`, `pattern-timeout` (regex match exceeded the timeout on
`test-pattern`/`test-groups` — see those scripts).

---

## inspect.csx

**Synopsis.** Report low-level PDF structure (page count, text blocks, candidate
patterns) for a PDF — primary discovery step before authoring a template.

| Arg         | Required | Description                                  |
| ----------- | -------- | -------------------------------------------- |
| `--pdf`     | yes      | Path to the source PDF.                      |
| `--page`    | no       | 1-based page index (default: all pages).     |
| `--summary` | no       | Flag. Emit page/table *shape* only — no flattened text or block contents. ~1 KB instead of ~15 KB per invoice; use when surveying many PDFs for structural grouping, then run a full inspect on one representative per group. |

**Output schema.** `PdfInspection`: `{ pageCount, metadata: { title, author, subject, keywords, creator, producer, creationDate, modifiedDate, rawProperties }, pages: [{ pageNumber, flattenedText, blocks: [{ bounds: { x, y, width, height }, content }], tables: [{ headerPreview, rowPreviews, totalRowCount }] }] }`.

**Output schema (`--summary`).** `{ pageCount, metadata, pages: [{ pageNumber, textLength, blockCount, tables: [{ totalRowCount, headerPreview }] }] }`.

**Exit codes.** `0` success · `2` bad `--page` argument · `1` unhandled · `pdf-not-found` on missing input.

**Example.**

```powershell
dotnet script scripts/inspect.csx -- --pdf invoice.pdf --page 1
dotnet script scripts/inspect.csx -- --pdf invoice.pdf --summary
```

---

## test-pattern.csx

**Synopsis.** Evaluate a single extraction pattern against a PDF and report whether
it matched, with the captured value(s).

| Arg                  | Required | Description                                                |
| -------------------- | -------- | ---------------------------------------------------------- |
| `--pattern`          | yes      | Inline pattern source (regex or DSL block).                |
| `--pdf`              | yes      | Path to the source PDF.                                    |
| `--page`             | no       | 1-based page index (default: all pages).                   |
| `--block-separator`  | no       | Override the block-separator regex used during extraction.  |
| `--timeout-ms`       | no       | Regex match timeout in milliseconds (default: `5000`).      |

**Output schema.** `{ hasMatches, haystack, matches: [{ index, length, value, groups: [{ name, index, length, value, success }] }], gaps: [{ start, length, text }], error: { kind, message, position } | null, nearMiss: { partialMatch, breakIndex } | null }`. `error` is populated on invalid regex syntax; `nearMiss` only on a valid zero-match probe.

**Timeout.** Matching is bounded by a **5-second default match timeout** so a
catastrophically backtracking pattern cannot hang the script. On timeout the script
exits `1` with the standard error envelope, `code: "pattern-timeout"`, naming the
elapsed limit — make the pattern more deterministic (see `skills` patterns reference
§ Backtracking complexity) or raise `--timeout-ms`.

**Exit codes.** `0` success · `2` bad argument · `1` `pattern-timeout` / unhandled · `pdf-not-found`.

**Example.**

```powershell
dotnet script scripts/test-pattern.csx -- --pattern 'Invoice #(\d+)' --pdf invoice.pdf
```

---

## test-groups.csx

**Synopsis.** Evaluate a multi-group pattern and emit each named capture group's
match set — used when authoring repeating-row extractions.

| Arg            | Required | Description                                            |
| -------------- | -------- | ------------------------------------------------------ |
| `--pattern`    | yes      | Multi-group pattern source.                            |
| `--pdf`        | yes      | Path to the source PDF.                                |
| `--page`       | no       | 1-based page index (default: all pages).               |
| `--timeout-ms` | no       | Regex match timeout in milliseconds (default: `5000`). |

**Output schema.** `{ hasMatches, haystack, groups: [{ groupName, subPattern, matchesIndependently, matchCount, firstMatchText }], error: { kind, message, position } | null }`.

**Timeout.** Same contract as `test-pattern.csx`: a **5-second default match timeout**
applies; on timeout the script exits `1` with `code: "pattern-timeout"` naming the
elapsed limit. Override with `--timeout-ms`. (A per-group sub-pattern that times out
independently degrades to a non-matching row instead of erroring.)

**Exit codes.** `0` success · `2` bad argument · `1` `pattern-timeout` / unhandled.

**Example.**

```powershell
dotnet script scripts/test-groups.csx -- --pattern @rows.txt --pdf invoice.pdf
```

---

## validate-template.csx

**Synopsis.** Parse a `Template` JSON file and report schema / semantic validation
results without executing it.

| Arg          | Required | Description                          |
| ------------ | -------- | ------------------------------------ |
| `--template` | yes      | Path to the template JSON file.      |

**Output schema.** `{ valid: bool, hasWarnings: bool, errors: [{ code, fieldPath, message, severity }] }`. `severity` is an integer (0 Info, 1 Warning, 2 Error).

**Exit codes.** `0` parse succeeded (check `valid` and the `errors` list) · `1` `parse-error` / `bad-format` / unhandled · `2` bad argument.

**Example.**

```powershell
dotnet script scripts/validate-template.csx -- --template templates/invoice.json
```

---

## dry-run.csx

**Synopsis.** Execute extraction + publish steps against a PDF **without** producing a
serialized output payload — useful for end-to-end pipeline validation. Optionally
preview formatted output with `--preview-as`.

| Arg            | Required | Description                                                |
| -------------- | -------- | ---------------------------------------------------------- |
| `--pdf`        | yes      | Path to the source PDF.                                    |
| `--template`   | yes      | Path to the template JSON file.                            |
| `--preview-as` | no       | Preview formatted output: `csv` or `json` (no file written). |

**Output schema.** `{ kind: "DryRunSucceeded"|"DryRunFailed"|"DryRunRejected", result, completeness }`. `DryRunSucceeded.result` carries `jsonProjection`, `diagnostics`, `rawHaystack`; `DryRunFailed.result` carries `step` (int), `fieldPath`, `sourceText`, `targetTypeName`, `innerDetail`; `DryRunRejected.result` carries `reason` (int), `detail`. With `--preview-as`: `{ kind, format, preview }`.

**Publish-validation parity.** A record that would fail the publish step's schema
validation on execute (e.g. a required field with a null value in any row) returns
`kind: "DryRunFailed"` with `step: 4` (Publish) and the same message text execute
would produce — a dry-run verdict is the execute verdict. Output generation is still
bypassed.

**Exit codes.** `0` complete · `2` succeeded but `completeness.isComplete` is `false` · `1` failed/rejected/unhandled.

**Example.**

```powershell
dotnet script scripts/dry-run.csx -- --pdf invoice.pdf --template templates/invoice.json
```

---

## execute.csx

**Synopsis.** Full pipeline run with output generation. Writes a CSV or JSON payload
either to stdout, to a plain `--output` file, or — with `--append` — merges it into a
durable **ledger** (the provenance-tracking output shape described under
[`batch-execute.csx`](#batch-executecsx)). Append mode is create-or-extend and
**idempotent per source PDF**: the same command works in month 1 and month N, and a PDF
already recorded in the ledger is skipped (default), replaced in place, or fails per
`--on-duplicate`. A skipped duplicate never runs the engine. Ledger writes are atomic
(temp file + replace), so a crash never corrupts the existing file.

Data safety: `--append` refuses to touch a file that is not a recognizable ledger
(`not-a-ledger`); a plain (non-append) `--output` refuses to flatten a recognized ledger
unless `--overwrite` is passed (`existing-ledger`). Plain snapshot files that are not
ledgers keep the original overwrite semantics.

| Arg               | Required | Description                                                                                |
| ----------------- | -------- | ------------------------------------------------------------------------------------------ |
| `--pdf`           | yes      | Path to the source PDF.                                                                    |
| `--template`      | yes      | Path to the template JSON file.                                                            |
| `--format`        | yes      | `csv` or `json`.                                                                           |
| `--output`        | no       | Write payload to this path. If omitted, stdout text (then `--append` is invalid).          |
| `--append`        | no       | Merge into the ledger at `--output` (created when missing); idempotent per source PDF.     |
| `--on-duplicate`  | no       | With `--append`: `skip` (default) / `replace` / `fail` when this PDF is already recorded.  |
| `--strict-header` | no       | With `--append --format csv`: fail instead of adding new columns to the ledger header.     |
| `--overwrite`     | no       | Allow a plain (non-append) `--output` to replace an existing Docuoria ledger.              |

**Output schema.** Success (no `--output`): `{ status: "ok", format, output, completeness }`. Success (plain `--output`): `{ status: "ok", path, completeness }`. Success (`--append`): `{ status: "ok", path, completeness?, ledger: { action: "appended"|"replaced"|"skipped-duplicate", sourceFile, rowsAdded, rowsRemoved, columnsAdded?, totalRows, totalSources } }` — `completeness` is absent on a skipped duplicate because the engine never ran; `columnsAdded` is present only when the merge evolved the CSV header. Failure: `{ status: "rejected"|"failed", result }`.

**Exit codes.** `0` complete (an idempotent duplicate skip is success) · `2` succeeded but `completeness.isComplete` is `false` · `1` rejected / failed / refused (`not-a-ledger`, `existing-ledger`, `duplicate-source` under `--on-duplicate fail`).

**Examples.**

```powershell
dotnet script scripts/execute.csx -- --pdf invoice.pdf --template templates/invoice.json --format json --output out.json
# Monthly ledger: add this month's invoice; re-running the exact same command is a safe no-op.
dotnet script scripts/execute.csx -- --pdf jan-invoice.pdf --template templates/invoice.json --format csv --output ledger.csv --append
```

---

## batch-execute.csx

**Synopsis.** Classify-route every PDF in a corpus and merge the extracted rows into
consolidated **ledger** output. For each PDF (ordinal-sorted by file name) the top-ranked
classification decides the route: `"strong"` executes the matched template;
`"partial"` and `"no-match"` are recorded as **skipped** — the batch never guesses, the caller
resolves partials per the partial-match decision and re-runs. A failed/rejected execution is
recorded per-PDF and the batch continues.

**The ledger shape.** CSV (default) leads with `sourceFile` (file name only) and
`templateId` provenance columns, followed by the union of all per-template headers in
first-seen order; cells a template does not produce are left empty. Output is RFC 4180,
CRLF, UTF-8 without BOM (matching `CsvGeneratorOptions` defaults). `--format json`
produces an indented JSON array of `{ sourceFile, templateId, data }` envelopes, one per
PDF, where `data` is the JSON generator's typed object — the right shape for system
imports. The provenance columns make the file self-describing: the ledger itself records
which documents it already contains.

**Recurring runs (`--append`).** Extends an existing ledger instead of rebuilding:
missing file → created; recorded PDFs → handled per `--on-duplicate` (`skip` default,
detected **before** classification so a monthly whole-folder sweep only pays for new
files; `replace` refreshes a source's rows in place; `fail` aborts upfront). Re-running
the same command twice is a safe no-op. New templates may evolve the CSV header (new
columns appended, old rows empty there, reported via `columnsAdded`); `--strict-header`
fails such PDFs instead. All writes are atomic (temp file + replace).

**Data safety.** `--append` refuses files that are not recognizable ledgers
(`not-a-ledger`). A plain (non-append) run refuses to rebuild a ledger that records
sources **not present in the corpus** — those rows could not be regenerated
(`would-drop-sources`) — and refuses to overwrite an existing non-ledger file
(`existing-output`); `--overwrite` forces either. Re-running a plain snapshot over the
same corpus (every recorded source present) proceeds without flags, as before.

**Split outputs.** `--output` may contain `{templateId}` and/or `{sourceFile}` tokens to
route one ledger per structure (`out/{templateId}.csv`) or per input file
(`out/{sourceFile}.csv`); every ledger behavior — append, duplicate policy, safety,
atomic writes — applies independently per resolved path.

Unlike the other store scripts there is **no** `./templates` default — classification
routes every PDF, so the store is required.

| Arg               | Required | Description                                                                            |
| ----------------- | -------- | -------------------------------------------------------------------------------------- |
| `--corpus`        | yes      | Directory containing the PDFs to process.                                              |
| `--output`        | yes      | Ledger path to write; may contain `{templateId}` / `{sourceFile}` tokens.              |
| `--store-path`    | one of   | Local template store directory.                                                        |
| `--store-url`     | one of   | API template store URL (mutually exclusive with `--store-path`).                       |
| `--store-key`     | no       | Function key for API store authentication.                                             |
| `--format`        | no       | Ledger format: `csv` (default) or `json`.                                              |
| `--append`        | no       | Extend the existing ledger(s) idempotently instead of rebuilding.                      |
| `--on-duplicate`  | no       | With `--append`: `skip` (default) / `replace` / `fail` for PDFs already recorded.      |
| `--strict-header` | no       | With `--append --format csv`: fail a PDF instead of adding new columns to the header.  |
| `--overwrite`     | no       | Allow a plain run to replace an unregenerable ledger or a non-ledger output file.      |

**Output schema.** `{ pdfs: [{ pdf, templateId, recommendation, status: "ok"|"incomplete"|"skipped"|"duplicate"|"failed", rows, action?, reason?, error?, completeness? }], summary: { pdfCount, succeeded, incomplete, skipped, duplicates, failed, rowsWritten, totalRows, outputPath?, outputs: [{ path, rowsWritten, totalRows, totalSources, columnsAdded? }] } }`. `reason` is `"no-template-match"` / `"partial-match"` on skipped entries and `"already-in-output"` on duplicate entries; `action` (`"appended"`/`"replaced"`) appears on executed entries in append mode; `rows` counts CSV rows (or 1 JSON envelope) the PDF contributed; `rowsWritten` is what this run added, `totalRows` what the ledger now holds; `outputPath` is omitted when `--output` is tokenized (read `outputs` instead); `completeness` is present when a succeeded execution was incomplete (`"incomplete"` rows are still merged).

**Exit codes.** `0` every PDF `"ok"` or `"duplicate"` (duplicate skips are the append steady state, not a problem) · `2` ran, but at least one PDF skipped/incomplete/failed (ledgers still written for the ok rows) · `1` bad args / missing corpus / no store / zero PDFs / refused output target (`not-a-ledger`, `would-drop-sources`, `existing-output`, `duplicate-source` under `fail`).

**Examples.**

```powershell
# One-shot: a year of statements, one merged CSV ledger.
dotnet script scripts/batch-execute.csx -- --corpus ./invoices --store-path ./templates --output ./merged.csv
# Monthly steady state: sweep the whole folder; only files not yet in the ledger are processed.
dotnet script scripts/batch-execute.csx -- --corpus ./invoices --store-path ./templates --output ./ledger.csv --append
# Mixed structures, one file per template; JSON envelopes for a bookkeeping import.
dotnet script scripts/batch-execute.csx -- --corpus ./inbox --store-path ./templates --output "./out/{templateId}.json" --format json
```

---

## evaluate-match.csx

**Synopsis.** Evaluate a single template's root match rule against a PDF and project the
agent-facing classification. Returns the composite `classificationScore` plus its
components, the per-requirement breakdown, and a per-rule summary. The template
argument may be a file path **or** a template identifier resolved through the store.

| Arg            | Required | Description                                                                       |
| -------------- | -------- | --------------------------------------------------------------------------------- |
| `--pdf`        | yes      | Path to the source PDF.                                                           |
| `--template`   | yes      | File path (`.json` / contains path separator) **or** template ID for store lookup. |
| `--store-path` | no       | Local template store directory (default: `./templates`).                          |
| `--store-url`  | no       | API template store URL.                                                           |
| `--store-key`  | no       | Function key for API store authentication.                                        |

**Output schema.** `TemplateMatchEvaluation`: `{ isMatch, classificationScore, recommendation, requirementsSatisfied, specificityScore, matchQuantityScore, coverageScore, ruleConfidence, requirements: [{ kind, satisfied, detail }], matchedRules: [{ ruleType, matched, confidence, detail }], ambiguity }`. `recommendation` is the engine's verdict: `"strong"` (rule matched, all requirements satisfied, every declared mapping produced ≥ 1 match — safe to dry-run), `"partial"` (rule matched but ≥ 1 mapping found nothing — diagnose via `ambiguity`), or `"no-match"` (rule failed or a requirement unsatisfied). Route on `recommendation`; `classificationScore` is for ranking only.

**Exit codes.** `0` success · `1` template not found / unhandled.

**Example.**

```powershell
dotnet script scripts/evaluate-match.csx -- --pdf invoice.pdf --template invoice
```

---

## classify.csx

**Synopsis.** Run ranked classification across **all** registered templates and return
the top matches sorted by confidence (descending).

| Arg            | Required | Description                                       |
| -------------- | -------- | ------------------------------------------------- |
| `--pdf`        | yes      | Path to the source PDF.                           |
| `--top`        | no       | Maximum number of results to return (default: 5). |
| `--store-path` | no       | Local template store directory (default: `./templates`). |
| `--store-url`  | no       | API template store URL.                           |
| `--store-key`  | no       | Function key for API store authentication.        |

**Output schema.** `{ matches: [{ templateId, classificationScore, recommendation, requirementsSatisfied, specificityScore, matchQuantityScore, coverageScore, ruleConfidence, ambiguity }, ...] }`, sorted by `classificationScore` descending. `recommendation` per match: `"strong"` (rule matched, all requirements satisfied, every declared mapping produced ≥ 1 match), `"partial"` (rule matched but ≥ 1 mapping found nothing), `"no-match"` (rule failed or a requirement unsatisfied). Route on `recommendation`; use `classificationScore` only to order candidates and spot near-ties. Templates with `requirementsSatisfied: false` are excluded. `ambiguity` is non-null only for partial or near-tie matches.

**Exit codes.** `0` success (including an empty `matches` array) · `1` `no-store` if no template store is registered · `1` unhandled.

**Example.**

```powershell
dotnet script scripts/classify.csx -- --pdf invoice.pdf
```

---

## list-templates.csx

**Synopsis.** Enumerate template identifiers from the configured store.

| Arg            | Required | Description                                              |
| -------------- | -------- | -------------------------------------------------------- |
| `--store-path` | no       | Local template store directory (default: `./templates`). |
| `--store-url`  | no       | API template store URL.                                  |
| `--store-key`  | no       | Function key for API store authentication.               |

**Output schema.** `{ templates: [id, ...] }`.

**Exit codes.** `0` success · `1` unhandled.

**Example.**

```powershell
dotnet script scripts/list-templates.csx -- --store-path ./templates
```

---

## load-template.csx

**Synopsis.** Resolve a template by identifier and emit its JSON representation.

| Arg            | Required | Description                                              |
| -------------- | -------- | -------------------------------------------------------- |
| `--id`         | yes      | Template identifier.                                     |
| `--output`     | no       | Write JSON to this file path instead of stdout.          |
| `--store-path` | no       | Local template store directory (default: `./templates`). |
| `--store-url`  | no       | API template store URL.                                  |
| `--store-key`  | no       | Function key for API store authentication.               |

**Output schema.** Without `--output`: full template JSON. With `--output`:
`{ status: "ok", path }`.

**Exit codes.** `0` success · `1` not-found / unhandled.

**Example.**

```powershell
dotnet script scripts/load-template.csx -- --id invoice --output templates/invoice.json
```

---

## save-template.csx

**Synopsis.** Persist a template JSON file to the configured store. Fails with
`already-exists` unless `--overwrite` is supplied.

| Arg            | Required | Description                                                               |
| -------------- | -------- | ------------------------------------------------------------------------- |
| `--template`   | yes      | Path to the template JSON file to persist.                                |
| `--overwrite`  | no       | Boolean switch — overwrite an existing template with the same identifier. |
| `--store-path` | no       | Local template store directory (default: `./templates`).                  |
| `--store-url`  | no       | API template store URL.                                                   |
| `--store-key`  | no       | Function key for API store authentication.                                |

**Output schema.** `{ status: "ok", identifier }`.

**Exit codes.** `0` success · `1` `already-exists` / parse-error / unhandled.

**Example.**

```powershell
dotnet script scripts/save-template.csx -- --template templates/invoice.json --overwrite
```

---

## survey.csx

**Synopsis.** Report structural facts about a directory of PDFs (run before authoring when more
than one PDF is in scope). It does **not** decide how many templates you need — that is a reasoning
task, like `inspect.csx`. Instead it returns the reliable signals and tells you how to reason:
- `matchedGroups` — PDFs that already classify to a stored template. **Reuse it.**
- `unmatched` — PDFs with no template, each with its `pageCount` and `structuralTokens` (recurring,
  value-free first-page tokens; instance values like IDs, dates and amounts are excluded). Token
  overlap between two PDFs is a *hint*, not proof, that they share a structure.
- `guidance` — explicit instructions for deciding the grouping (inspect candidates, compare all
  pages, confirm splits with negative validation). **Do not author one template per PDF.**

| Arg            | Required | Description                                                              |
| -------------- | -------- | ------------------------------------------------------------------------ |
| `--corpus`     | yes      | Directory containing the PDFs to survey.                                 |
| `--strict`     | no       | Exit `2` when any PDF is unmatched (a grouping/authoring decision is required). |
| `--store-path` | no       | Local template store directory (default: `./templates`).                 |
| `--store-url`  | no       | API template store URL.                                                  |
| `--store-key`  | no       | Function key for API store authentication.                               |

**Output schema.** `{ pdfCount, matchedGroups: [{ template, pdfs: [path, ...], representative }], unmatched: [{ pdf, pageCount, structuralTokens: [token, ...] }], guidance }`.

**Exit codes.** `0` success · `2` `--strict` and at least one PDF is unmatched · `1` fewer than 2 PDFs / invalid input.

**Example.**

```powershell
dotnet script scripts/survey.csx -- --corpus ./invoices --store-path ./templates
```

---

## regression-check.csx

**Synopsis.** Compare a baseline template against a modified template across a PDF corpus,
reporting per-PDF scalar and collection diffs. Use before overwriting a stored template to
prove the change does not break the PDFs it already handled. Provide the baseline as a file
(`--baseline`) **or** a store id (`--baseline-id`).

| Arg             | Required | Description                                                              |
| --------------- | -------- | ------------------------------------------------------------------------ |
| `--modified`    | yes      | Path to the updated template JSON.                                       |
| `--corpus`      | yes      | Directory of PDFs that previously classified to the baseline template.   |
| `--baseline`    | one of   | Path to the baseline template JSON file.                                 |
| `--baseline-id` | one of   | Baseline template identifier resolved through the store.                 |
| `--store-path`  | no       | Local template store directory (default: `./templates`).                 |
| `--store-url`   | no       | API template store URL.                                                  |
| `--store-key`   | no       | Function key for API store authentication.                               |

**Output schema.** `{ pdfs: [{ pdfPath, baselineClassificationScore, modifiedClassificationScore, baselineRequirementsSatisfied, modifiedRequirementsSatisfied, scalarDiffs: [{ fieldName, baseline, modified }], collectionDiffs: [{ collectionName, baselineRowCount, modifiedRowCount }], isRegression, isImprovement }], summary: { regressionsDetected, improvementsDetected, unchanged } }`.

**Exit codes.** `0` no regressions · `2` regressions detected · `1` invalid input.

**Example.**

```powershell
dotnet script scripts/regression-check.csx -- --modified templates/invoice.json --baseline-id invoice --corpus ./invoices
```

---

## schema-info.csx

**Synopsis.** Dump every SDK discriminator, mode, and enum — the live source of truth for
template authoring. Takes no arguments.

| Arg | Required | Description |
| --- | -------- | ----------- |
| _(none)_ | — | This script accepts no flags. |

**Output schema.** `{ fieldTypes, extractionSources, modes, matchRules, subFieldMappings, metadataFields, notes }`.

**Exit codes.** `0` success · `1` unhandled.

**Example.**

```powershell
dotnet script scripts/schema-info.csx
```

---

## Internals — `_common.csx`

`_common.csx` is the **single bootstrap** used by every script. It:

1. References the locally-built `Docuoria.dll` and required NuGet packages
   (`PdfPig`, `Tabula`, `CsvHelper`, `pythonnet`, `Microsoft.Extensions.Hosting` /
   `DependencyInjection` / `Http`).
2. Exposes `Cli.Require / Cli.Get / Cli.Has` for argument parsing (renamed from
   `Args` to avoid shadowing the `dotnet-script` global of the same name).
3. Builds a Generic Host via `ScriptHost.CreateHost(args, includeStore: bool)` which
   wires `AddDocuoriaEngine`, `AddBuiltInMatchRules`, the CSV/JSON output
   generators, and (optionally) the template store selected by the `--store-path` /
   `--store-url` / `--store-key` flags.
4. Provides `JsonOut.Write` / `JsonOut.Error` writers backed by
   `DocuoriaJsonOptions.Default` and a `LoadPdf(path)` helper that exits with
   `pdf-not-found` when the input is missing.

Scripts must declare `#nullable enable` after `#load "_common.csx"` because the
nullable context does not propagate across `#load` boundaries.
