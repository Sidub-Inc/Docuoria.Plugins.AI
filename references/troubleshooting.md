# Troubleshooting

The single place to diagnose anything that goes wrong. Find your situation in the **symptom index**, jump to the section, and follow it to a fix. Two ways in:

- **You have a confusing signal** (a pattern won't match, a field is "missing", a `$kind` error) → start at [Symptom interpretation](#symptom-interpretation).
- **You have a script error or a typed outcome** (`error.code`, `RejectedResult`, `FailedResult`, a bad classification, an empty-but-successful run) → start at [Error-code routing](#error-code-routing) and follow the branch.

## Contents

- [Symptom index](#symptom-index) — symptom → section lookup
- [Symptom interpretation](#symptom-interpretation): [pattern non-match](#pattern-non-match-hasmatches-false) · [completeness terminology](#completeness-terminology) · [validator `$kind` error](#validator-discriminator-error-bad-format-on-kind) · [`dotnet` / `dotnet-script` not found](#environment-dotnet-or-dotnet-script-not-found)
- [Error-code routing](#error-code-routing) — `error.code` → branch table
- [Branch A — RejectedResult](#branch-a--rejectedresult) (reasons 0–3)
- [Branch B — FailedResult](#branch-b--failedresult) (steps 0–4)
- [Branch C — Classification failure](#branch-c--classification-failure)
- [Silent or empty results](#silent-or-empty-results) — the success-but-empty failure mode
- [Incomplete success (exit code 2)](#incomplete-success-exit-code-2)
- [Partial-match classification](#partial-match-classification)
- [Quick reference](#quick-reference) — outcome → script → remediation

## Symptom index

| Symptom | Go to |
| --- | --- |
| `test-pattern` returns `hasMatches: false` on a pattern you believe is correct | [Pattern non-match](#pattern-non-match-hasmatches-false) |
| `dry-run`/`execute` reports `completeness.missingRequiredFields` or `emptyDeclaredCollections` | [Completeness terminology](#completeness-terminology) · [Incomplete success](#incomplete-success-exit-code-2) |
| `validate-template` returns `bad-format` mentioning `$kind` | [Discriminator error](#validator-discriminator-error-bad-format-on-kind) |
| A script printed `{ "error": { "code": ... } }` on stderr | [Error-code routing](#error-code-routing) |
| `'dotnet' is not recognized …` or `Could not execute because the specified command or file was not found` | [Environment: `dotnet` / `dotnet-script` not found](#environment-dotnet-or-dotnet-script-not-found) |
| stderr message starts `DOCUORIA_LICENSE_REQUIRED:` / `DOCUORIA_RATE_LIMIT:` / any `DOCUORIA_*:` prefix | [`licensing.md`](licensing.md) |
| `RejectedResult` (engine refused before running a step) | [Branch A — RejectedResult](#branch-a--rejectedresult) |
| `FailedResult` (a step threw) | [Branch B — FailedResult](#branch-b--failedresult) |
| Wrong template ranked first, or nothing matched | [Branch C — Classification failure](#branch-c--classification-failure) |
| `DryRunSucceeded` but a collection is `[]` or a scalar is `null` | [Silent or empty results](#silent-or-empty-results) |
| Exit code 2 — ran but incomplete | [Incomplete success](#incomplete-success-exit-code-2) |
| `failed` with `Data record failed schema validation: … Required field has null value` | [Required field null at publish](#required-field-null-at-publish) |
| Partial / near-tie classification (`ambiguity` block present) | [Partial-match classification](#partial-match-classification) |

---

# Symptom interpretation

Read this when a tool reports a non-obvious result and you need the *cause*, not just the symptom.

## Pattern non-match (`hasMatches: false`)

When a syntactically valid pattern produces no match, `nearMiss` is populated: `partialMatch` is the longest leading portion that still matched, and `breakIndex` is the haystack index where the next token failed.

1. **Read `nearMiss.breakIndex` first.** It points at (or just before) the character the pattern could not consume. Look at that character in `haystack[breakIndex]`.
2. **Punctuation in entity values is the most common cause.** A class like `[A-Za-z\s]` tested against `Sidub Inc.` matches `Sidub Inc` and breaks at the trailing `.`. Include the punctuation the value actually contains: `[A-Za-z\s.&,]`. The same applies to `&` in `Smith & Co` and `,` in `Acme, LLC`.
3. **Confirm against the real haystack.** Run `inspect.csx` and read `pages[*].flattenedText` — the engine's flattened text differs from the visual layout (whitespace, line breaks, encoding). Never adapt a pattern against the rendered PDF; adapt it against the haystack.
4. **Prefer coordinate-free patterns.** A `TextPatternExtractionSource` (Pattern/AllMatches) that matches on surrounding text is more drift-tolerant than a `TextAnchorExtractionSource` pinned to a fixed `region`. Reach for a fixed-region anchor only when no textual pattern is stable across variants.

Full authoring techniques: [`patterns.md`](patterns.md).

## Completeness terminology

`completeness.missingRequiredFields` lists **declared mappings that produced no value** — it is **not** a list of fields you marked "required." In Docuoria every declared mapping is effectively required: if you put a field in `extractionStep.mappings[]`, the engine expects it to extract a value, and a null counts as missing.

- **The fix is to repair the extraction, not to drop the field.** A field appearing here means the pattern/source is not matching on this PDF — go back to `test-pattern.csx` (read `nearMiss`) and fix the source.
- Removing the mapping to silence the warning discards data the user asked for. Only remove a mapping if the field genuinely does not exist in this document type — and if so, it likely belongs to a different sibling template (see [`extraction-sources.md`](extraction-sources.md) and [`classification.md`](classification.md) sibling discrimination).
- `emptyDeclaredCollections` is the collection analogue: a `RepeatingFieldMapping` that matched zero rows. Same remedy — fix the row pattern, do not delete the collection. Routing: [Incomplete success](#incomplete-success-exit-code-2) and [Silent or empty results](#silent-or-empty-results).

## Validator discriminator error (`bad-format` on `$kind`)

When deserialization fails on a missing or unrecognized `$kind`, the engine produces an actionable message that **enumerates the valid `$kind` values for that position** and never leaks internal SDK type names. `validate-template.csx` returns this as the `bad-format` error `detail`.

- **Read the enumerated set in the error `detail`** — it is the authoritative list of accepted discriminators for the failing JSON path (reflected directly from the SDK, so it cannot drift). Pick the correct value from that set; do not guess.
- The `SKILL.md` Discriminator reference table and [`template-reference.md`](template-reference.md) mirror this set for quick lookup, but the engine's error `detail` is the source of truth for the exact position that failed. `schema-info.csx` prints the live set too.

## Environment: `dotnet` or `dotnet-script` not found

Two symptoms, one cause each:

- `'dotnet' is not recognized as an internal or external command` (or `dotnet: command not found`) — the **.NET 10 SDK** is not installed or not on `PATH`. Install it from <https://dotnet.microsoft.com/download>, open a new shell, and confirm with `dotnet --version`.
- `Could not execute because the specified command or file was not found.` after `dotnet script …` — .NET is present but the **`dotnet-script` global tool** is missing. Install it and re-run:

  ```powershell
  dotnet tool install -g dotnet-script
  dotnet script --version
  ```

Both are install-time prerequisites of the skill, not template or PDF problems. The installer (`docuoria init`) runs on Node.js or .NET, but the installed skill always needs both of the above; `docuoria doctor` reports whether they are present.

---

# Error-code routing

Every script emits errors as `{ "error": { "code": "<code>", "message": "...", "detail": "..." } }` on stderr with a non-zero exit. Map the stderr `error.code` to a branch before reading the prose.

| `error.code` | Emitted by | Meaning | Go to |
| --- | --- | --- | --- |
| `pdf-not-found` | every script that takes `--pdf` | The path passed to `--pdf` does not resolve to a file | Fix the path (relative paths resolve from the cwd); re-run. Input error, no branch. |
| `template-not-found` | `dry-run.csx`, `execute.csx`, `evaluate-match.csx`, `regression-check.csx` | The `--template` (or `--modified` / `--baseline`) path does not resolve to a file | Correct the path (relative paths resolve from the cwd). No branch. |
| `not-found` | `load-template.csx`, `regression-check.csx` (`--baseline-id`) | Template ID does not exist in the store | Run `list-templates.csx -- --store-path <abs-dir>` to confirm the ID and that you pointed at the right store. No branch. |
| `missing-arg` | every script, for a required flag (exit 2) | A required `--flag` was not supplied | Add the flag; `--help` lists them. No branch. |
| `unknown-arg` | every script (exit 2) | A `--flag` the script does not declare, or a stray positional token; the message lists the valid flags | Fix the invocation — the `--` separator must be present and every flag spelled as `--help` shows. No branch. |
| `no-store` | `batch-execute.csx` only | Neither `--store-path` nor `--store-url` was given (this script has no `./templates` default) | Pass `--store-path <abs-dir>`. Other store scripts never emit this: a local store is always registered, and a missing directory simply yields `matches: []` / `templates: []`. No branch. |
| `bad-format` | `validate-template.csx`, `dry-run.csx`, `execute.csx`, `save-template.csx` | Template JSON is malformed or fails schema validation | [Branch A](#branch-a--rejectedresult) — `MalformedTemplate`. Run `validate-template.csx` for the error list. |
| `rejected` (stderr, exit 1) — or `kind: "DryRunRejected"` on **stdout, exit 0** | `execute.csx` and `dry-run.csx --preview-as` use the stderr code; plain `dry-run.csx` reports it on stdout, so route on `kind` | Engine returned `RejectedResult` — read `reason` | [Branch A](#branch-a--rejectedresult) — match on the `RejectionReason`. |
| `failed` (stderr, exit 1) — or `kind: "DryRunFailed"` on **stdout, exit 0** | `execute.csx` and `dry-run.csx --preview-as` use the stderr code; plain `dry-run.csx` reports it on stdout, so route on `kind` | Engine returned `FailedResult` — read `step` | [Branch B](#branch-b--failedresult) — match on the `StepIdentifier`. |
| `pattern-timeout` | `test-pattern.csx`, `test-groups.csx` | Regex matching exceeded the 5s match timeout (catastrophic backtracking) | [`patterns.md` § Backtracking complexity](patterns.md#backtracking-complexity) — make the pattern deterministic; `--timeout-ms` overrides the limit. |
| (stdout) `DryRunSucceeded` with empty collections | `dry-run.csx`, `execute.csx` | Succeeded but a `RepeatingFieldMapping` returned `[]` or a scalar returned `null` | [Silent or empty results](#silent-or-empty-results) — no stderr; detect from stdout. |
| Wrong template ranked first, or nothing ranked | `classify.csx` | Unexpected ordering — diagnose via the ranked matches | [Branch C](#branch-c--classification-failure). |
| `already-exists` | `save-template.csx` | Template with the same ID already exists | Pass `--overwrite` if intentional, or pick a different ID. No branch. |
| `unhandled` | any script | Unexpected exception inside the script (`detail` has the stack) | SDK/script defect, not a template/PDF problem. File a bug. No branch. |
| `license-required` / `license-inactive` / `license-invalid` (exit 3) · `rate-limit` / `feature-denied` / `license-unavailable` (exit 1) · `offering-unavailable` (exit 2) · `checkout-unavailable` (exit 1, `license-acquire.csx` only) | any script | Licence enforcement; the message starts with a `DOCUORIA_*:` prefix | [`licensing.md`](licensing.md). In `batch-execute.csx` a licence failure stops the batch at that PDF: completed outputs are written, `detail` lists processed and remaining PDFs, and re-running with `--append` after the window resets completes it. |

## Branch A — RejectedResult

A `RejectedResult` means the engine refused before completing. The stdout/stderr `reason` is the `RejectionReason` integer (0 InvalidPdf, 1 MalformedTemplate, 2 UnknownOutputGenerator, 3 GeneratorRejected).

### reason 0 — InvalidPdf

- **Meaning:** the PDF stream could not be parsed.
- **Diagnose:** run `inspect.csx -- --pdf <pdf>`. If `pageCount` is `0`, the file is unparseable.
- **Remediation:** confirm the file is a real PDF (magic bytes `%PDF-`), not a renamed image or HTML. If the source is a scan, the engine cannot extract from rasterised content; OCR upstream first.

### reason 1 — MalformedTemplate

- **Meaning:** the template JSON is structurally invalid (schema violations, missing required fields, unknown discriminators).
- **Diagnose:** run `validate-template.csx -- --template <template.json>`.
- **Remediation:** fix every error (`severity: 2`). Never bypass validation — the runtime check is the same check.

### reason 2 — UnknownOutputGenerator

- **Meaning:** the generic `TGenerator` passed to `ExecuteTemplateAsync<TGenerator, TOptions>` is not registered in DI.
- **Diagnose:** search host startup for `AddOutputGenerator<TGenerator, TOptions>` (or a helper such as `AddCsvOutputGenerator`).
- **Remediation:** register the generator before calling execute, or pick an already-registered generator. (For the CLI scripts this is wired already; this surfaces only in custom hosts.)

### reason 3 — GeneratorRejected

- **Meaning:** the generator refused the extracted data (e.g. two or more collections handed to a CSV generator).
- **Diagnose:** run `dry-run.csx -- --pdf <pdf> --template <template.json>` — the projection shows what the generator would have received.
- **Remediation:** reshape the template (split into separate templates, or use JSON which accepts multiple collections).

## Branch B — FailedResult

A `FailedResult`/`DryRunFailed` means a step threw. Read the `step` integer first (0 Unknown, 1 Retrieval, 2 Extraction, 3 Transformation, 4 Publish); everything else is diagnostic context.

### step 1 — Retrieval

- **Meaning:** a retrieval provider threw.
- **Diagnose:** read `innerDetail`. For HTTP retrieval check connectivity and 4xx/5xx status.
- **Remediation:** fix the retrieval source; rerun.

### step 2 — Extraction

- **Meaning:** a pattern, table, or anchor extraction step threw or could not produce a value.
- **Diagnose:** rerun via `dry-run.csx` (diagnostics on by default). Read `fieldPath` to identify the field, then `test-pattern.csx` (for `TextPatternExtractionSource`) or `inspect.csx` (for `TableRowsExtractionSource` / `TableCellExtractionSource`).
- **Remediation:** see [`patterns.md`](patterns.md) (Authoring techniques) or revisit [`extraction-sources.md`](extraction-sources.md).

### step 3 — Transformation (coercion)

- **Meaning:** a field coercion failed (string → Date, Number, etc.).
- **Diagnose:** read the structured fields: `fieldPath` (which field), `sourceText` (the raw captured substring, ≤ 256 chars), `targetTypeName` (the destination type), `innerDetail` (the coercion exception detail).
- **Remediation:** either tighten the regex to capture a coercible substring (see [`patterns.md`](patterns.md) pattern 4 vs 3 — strip the currency symbol), change the field type, add a `parseFormat`, or add a transform step.

### step 4 — Publish

- **Meaning:** the output generator threw mid-write (vs cleanly rejecting, which yields `GeneratorRejected`).
- **Diagnose:** read `innerDetail`.
- **Remediation:** typical causes are I/O (path not writable, file locked) — fix infrastructure first, then file a generator issue.

### step 0 — Unknown

- **Meaning:** legacy/default value. Observed when no `step` was set.
- **Diagnose:** treat as Extraction until proven otherwise; read `innerDetail`.
- **Remediation:** proceed as for the inferred step.

## Branch C — Classification failure

Classification issues come in three forms: no template matched, the wrong template matched, or a template's requirements are not satisfied. Start with a ranked view:

```
dotnet script scripts/classify.csx -- --pdf <pdf> --store-path <templates-dir>
```

This returns the top-N templates sorted by `classificationScore` (descending), each with a `recommendation`. Templates with `requirementsSatisfied: false` are excluded. For the recommendation-to-action table, see [`classification.md` § Interpreting the recommendation](classification.md#interpreting-the-recommendation). An empty `matches` array means either no templates are stored or all candidates have `requirementsSatisfied: false`.

### Requirements not satisfied — template excluded

- **Meaning:** the template's extraction requirements were not met. The engine set `classificationScore = 0` and excluded it — expected behaviour when siblings use requirements to discriminate.
- **Diagnose:**
  1. `evaluate-match.csx` — find `requirements[*].satisfied: false` and read `requirements[*].detail`.
  2. `dry-run.csx` — verify whether the expected collection is present and whether the pattern matches.
  3. `MinRows` unsatisfied + empty collection → the pattern may not match this variant → [Silent or empty results](#silent-or-empty-results).
  4. `MustBeAbsent` unexpectedly violated → the pattern is too broad and matching incidental text → tighten it.
- **Remediation:** if this IS the correct template, fix the pattern so the required mapping populates. If it is a sibling that should be excluded, the requirements are working correctly. If the requirement itself is wrong, see [`classification.md` § When NOT to use requirements](classification.md#when-not-to-use-requirements).

### Wrong template matched

- **Meaning:** a high-scoring template produces empty/incorrect data because the PDF is a different document type from the same vendor.
- **Diagnose:**
  1. `classify.csx` — is the correct template lower in the list? A narrow `classificationScore` gap means weak discrimination.
  2. `inspect.csx` — compare the page structure against the PDF used to author the matched template.
- **Remediation:** add `requirements` to the matched template to disqualify it (e.g. `MustBeAbsent` for a mapping unique to the correct template); and/or strengthen the correct template's discriminators. Validate with `classify.csx` — the correct template must rank #1 with a clear gap. See [`classification.md` § Declaring requirements](classification.md#declaring-requirements).

### No match at all

- **Meaning:** all templates score near zero, or all have `requirementsSatisfied: false`.
- **Diagnose:** `evaluate-match.csx` against each stored template for the per-requirement breakdown.
- **Remediation:** if all have unsatisfied requirements, the PDF is a new variant — author a new template or adjust requirements. If nothing matches, author a new template (back to [`workflow.md`](workflow.md) Step 2); if any template scores moderately (0.3+), `load-template.csx` it as a structural starting point.

### Root-cause patterns

| Pattern | Symptom | Fix |
|---|---|---|
| Vendor-only tokens | Multiple document types from same vendor score similarly | Add document-type-specific discriminators (see [`classification.md`](classification.md)) |
| AnyToken with low threshold | Template matches on generic tokens alone | Switch to AllTokens for critical gates, or raise threshold |
| No structural rules | Same tokens, different layouts | Add `TableMatchRule`, `PageGeometryMatchRule`, or `TextAnchorMatchRule` |
| Single rule (no composite) | No layered defence | Use `CompositeMatchRule` with weighted discriminators |
| Never tested negatives | Fine against target but matches siblings | Validate against same-vendor PDFs that should NOT match |
| Narrow score gap | Top two within 0.1 | Strengthen discriminators or add `requirements` to widen the gap |
| No requirements declared | Both siblings match the wrong PDF | Add `MustBeAbsent` or `MinRows` to enforce structural exclusivity |

## Silent or empty results

The *silent failure* mode — the pipeline reports success but a field (typically a `RepeatingFieldMapping`) returned `null` or `[]`. The dry-run does not flag this as an error because the engine has no expectation of how many rows should exist.

### A collection returns `[]`

- **Diagnose:**
  1. `dry-run.csx` — confirm the collection field is present but empty.
  2. `inspect.csx -- --pdf <pdf> --page <N>` — examine `flattenedText` and `tables` for the page with the expected data.
  3. `tables` with `totalRowCount > 1` and meaningful `rowPreviews` → the data IS detectable → switch to `TableRowsExtractionSource`.
  4. `tables` with only header-like entries (1 row) or none → read `flattenedText` carefully; block-flattening order may differ from the visual layout.
  5. `test-pattern.csx` against the ACTUAL flattened text (not what you see in the PDF viewer).

- **Root causes (in likelihood order):**

| Cause | How to confirm | Remediation |
|---|---|---|
| **Layout variant** — built for a different layout from the same vendor | Compare page 2+ structure against the authoring PDF (section headers, per-product vs single table) | **Split into separate templates** with narrower match rules — [`classification.md` § Partial-match decision](classification.md#partial-match-decision-using-the-ambiguity-block) |
| **Block order differs from visual order** — column-grouped blocks (labels in one block, values in another) | `inspect.csx` → read `blocks` with coordinates; if labels and values are in separate column-aligned blocks, the flattened haystack loses row associations | If `tables` detection finds the structure → `TableRowsExtractionSource`; otherwise document as a known limitation |
| **Pattern mismatch** — AllMatches regex written for a different text structure | `test-pattern.csx` — if `hasMatches: false`, the pattern doesn't match the haystack | Rewrite against the actual `flattenedText` — [`patterns.md`](patterns.md) |
| **Page targeting** — source targets the wrong page | Compare `pageNumber` against where the data appears | Correct or remove `pageNumber` |

### A scalar returns `null` unexpectedly

- **Diagnose:** `test-pattern.csx` with the field's regex. If no match, inspect the haystack for the actual text around the expected value.
- **Remediation:** adjust the regex. If the field genuinely doesn't exist in this variant, set `"isRequired": false` or split templates.

## Incomplete success (exit code 2)

`dry-run.csx` and `execute.csx` exit with code 2 (not 0 or 1) when the pipeline ran but `completeness.isComplete` is `false`. **This is a failure to fix, not a success.** The template matched and ran, but not all declared fields populated.

**Step 1 — Read the completeness block from stdout:**

```json
{
  "completeness": {
    "isComplete": false,
    "missingRequiredFields": ["documentDate"],
    "emptyDeclaredCollections": ["lineItems"],
    "unsatisfiedRequirements": []
  }
}
```

**Step 2 — Diagnose each missing field:** for each name in `missingRequiredFields`, run `test-pattern.csx` with the field's regex.
- Pattern has no match → fix the regex or the block separator.
- Pattern matches but field is still null → coercion may be failing → see [Branch B step 3](#step-3--transformation-coercion); try `fieldType` `0` (String) first to isolate.

**Step 3 — Diagnose empty collections:** for each name in `emptyDeclaredCollections`, run `test-pattern.csx` with the collection's regex.
- Zero matches → the collection anchor or pattern is wrong; use `inspect.csx` to locate the table or list in the haystack → [Silent or empty results](#silent-or-empty-results).
- Matches on the wrong page → add `pageNumber` to the extraction source.

Exit code 2 with JSON on stdout always means a template correction, never a pipeline fix. (On `execute.csx`, exit 1 with `failed`/`rejected` on stderr means the pipeline itself failed; plain `dry-run.csx` reports the same verdict as `kind: "DryRunFailed"`/`"DryRunRejected"` on stdout with exit 0 — see [Branch B](#branch-b--failedresult). Exit 2 with JSON on **stderr** is an argument error such as `missing-arg` or `unknown-arg`.)

## Required field null at publish

`execute.csx` exits 1 with `code: "failed"` and a message like:

```
Data record failed schema validation:
  - amounts[0].taxRatePct: Required field has null value.
```

`dry-run.csx` surfaces the **same verdict** as `kind: "DryRunFailed"` with `step: 4`
(Publish) and identical message text — a dry-run that succeeds will not fail this
validation on execute.

The pipeline extracted the record but the publish step rejected it because a field
declared (or defaulted — `isRequired` defaults to `true`) as required produced no
value for one or more rows.

- **The document legitimately omits the value** (e.g. a tax column printing `---` or
  blank for exempt rows): mark the field `"isRequired": false` in its
  `PrimitiveFieldDefinition`. Blank cells in the output are then *faithful to the
  document* — say so when reporting to the user.
- **The value exists in the PDF but the pattern missed it for some rows:** run
  `test-pattern.csx` on the collection pattern and compare match count to the
  expected row count; fix the pattern (optional groups around the missing segment
  are the usual cure: `(?:GST/HST\n(?<rate>\d+\.\d{2})%\n)?`).

Decide from the haystack, not the schema: if `inspect.csx` shows no value printed for
that row, the field is optional in reality — never invent a value and never delete
the row.

## Partial-match classification

When `classify.csx` or `evaluate-match.csx` returns `recommendation: "partial"`, or a near-tie (top two scores within ~0.1) with an `ambiguity` block, the classification is uncertain. The **four-action decision (extend / extend-with-tolerance / split / new) and the stop-and-ask rule are owned by [`classification.md` § Partial-match decision](classification.md#partial-match-decision-using-the-ambiguity-block)** — go there to decide. Quick read of the block:

| Field | What to check |
| --- | --- |
| `discriminatorTokensAbsent` | Tokens the template expected but the PDF lacks. 3+ significant tokens → split/new; 1 cosmetic token → extend-with-tolerance. |
| `rootRuleChildren[*]` | Which sub-rules fired. A `false` child identifies the discriminator that isn't working for this PDF. |
| `extractionMappings[*]` | Whether each field would extract data. `0` matches → the field won't populate; fix extraction before lowering any threshold. |

---

## Quick reference

| Outcome | Value | Script | Remediation pointer |
| --- | --- | --- | --- |
| `RejectedResult` | reason 0 InvalidPdf | `inspect.csx` | check magic bytes / OCR upstream |
| `RejectedResult` | reason 1 MalformedTemplate | `validate-template.csx` | fix schema errors |
| `RejectedResult` | reason 2 UnknownOutputGenerator | (host startup) | register generator in DI |
| `RejectedResult` | reason 3 GeneratorRejected | `dry-run.csx` | reshape template or use JSON |
| `FailedResult` | step 1 Retrieval | (inspect retrieval) | fix retrieval source |
| `FailedResult` | step 2 Extraction | `dry-run.csx`, `test-pattern.csx`, `inspect.csx` | [`patterns.md`](patterns.md), [`extraction-sources.md`](extraction-sources.md) |
| `FailedResult` | step 3 Transformation | `dry-run.csx` | tighten pattern, add `parseFormat`, or change field type |
| `FailedResult` | step 4 Publish | (inspect I/O) | fix infrastructure |
| `FailedResult` | step 0 Unknown | `dry-run.csx` | treat as Extraction |
| `DryRunSucceeded` | empty collection | `dry-run.csx`, `inspect.csx`, `test-pattern.csx` | [Silent or empty results](#silent-or-empty-results) |
| Incomplete | exit code 2 | `dry-run.csx`, `execute.csx` | [Incomplete success](#incomplete-success-exit-code-2) |
| Classification | no match / requirements unsatisfied | `classify.csx`, `evaluate-match.csx` | [Branch C](#branch-c--classification-failure) |
| Classification | wrong match | `classify.csx`, `inspect.csx` | [Branch C](#branch-c--classification-failure) |
| Classification | partial / near-tie | `classify.csx`, `evaluate-match.csx` | [Partial-match classification](#partial-match-classification) |
