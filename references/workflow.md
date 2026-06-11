# Workflow

The procedure you MUST follow to turn a PDF into structured output. The pipeline runs through a **template store** — a directory (or API endpoint) of JSON template files that define how to classify and extract a document type.

**Store location:** store-aware scripts (`classify`, `evaluate-match`, `list-templates`, `load-template`, `save-template`, `survey`, `regression-check`, `batch-execute`) read templates from a store directory. The store MUST live in the **user's working directory** (e.g. `<project>/templates`) — NEVER inside the skill install directory (`.claude/skills/docuoria/...`). Scripts are often invoked with the skill directory as cwd, so the bare default `./templates` would silently create the store inside the skill folder, where a skill update or reinstall wipes it. Always pass an **absolute** `--store-path` anchored at the user's project root.

## Contents

- [Two rules that override everything below](#two-rules-that-override-everything-below)
- [Settle scope first: single-PDF or batch](#settle-scope-first-single-pdf-or-batch)
- [Step map](#step-map)
- [Progress checklist](#progress-checklist)
- [Step 0 — Survey (batch only)](#step-0--survey-batch-only)
- [Step 1 — Classify](#step-1--classify-always-first-never-skip)
- [Step 2 — Inspect](#step-2--inspect-when-no-template-matches)
- [Step 3 — Test patterns](#step-3--test-patterns)
- [Step 4 — Build the template](#step-4--build-the-template)
- [Step 5 — Dry-run](#step-5--dry-run)
- [Step 5b — Regression check](#step-5b--regression-check-blocking-gate-when-modifying-a-stored-template)
- [Step 6 — Execute](#step-6--execute)
- [Step 7 — Store](#step-7--store)
- [Quick reference](#quick-reference)
- [CSV output behaviour](#csv-output-behaviour)

---

## Two rules that override everything below

These are the two failures that break real runs. Obey them before any step.

### Rule 1 — Understand the WHOLE document before you plan, ask, or author

The unit of work is the **document**, never "page 1". Before you form a plan, ask the user anything, or write a single line of template JSON:

1. Run `inspect.csx` (no `--page`) so the engine reads **every** page.
2. State to yourself, page by page from 1 to `pageCount`, what each page contains.
3. Account for `pages[*].tables[*].totalRowCount` on every page — a count above 1 means line-level rows exist on that page.

Multi-page business documents routinely place summary totals on the first page and the actual line items, charges, or terms on later pages. **If you have only looked at one page, you do not understand the document and you are not allowed to proceed.** Treating "analyze the PDF" as "analyze page 1" is the single most damaging mistake in this skill.

### Rule 2 — Ask the user at most ONE plain-language question, and only when you must

You are working for an everyday business user who does not know — and does not want to know — anything technical. Therefore:

- **Never** ask about internal mechanics: do not ask whether to validate, run checks, classify, test patterns, dry-run, execute, or which extraction source to use. You decide and do all of that silently.
- Ask a question **only** when the document genuinely supports more than one reasonable result **and** the user has not already told you what they want. If their request already answers it, ask nothing.
- When you must ask, ask **one** question, in plain language, about the **outcome** (what ends up in their output) — not the method.
- Offer 2–4 concrete choices. Mark the most complete useful option **(Recommended)** and pre-select it.

**Good (after fully reading the document):**
> Your file is a 4-page invoice. Page 1 has the summary totals; pages 2–3 list 2 individual charges. What should I put in your output?
> - Everything — the totals plus every charge *(Recommended)*
> - Just the summary totals
> - Just the list of charges

**Bad (never do this):** "Should I run a dry-run?" · "Do you want me to validate the template?" · "CSV or JSON?" (default to a spreadsheet/CSV unless the user said otherwise) · a list of 5+ questions.

---

## Settle scope first: single-PDF or batch

Before Step 1, decide the scope — it changes where you start:

- **Single-PDF** — one file, or the user pointed at one document → start at **Step 1 — Classify**.
- **Batch** — a folder, a list, or language like "all invoices", "every statement", "these files" → start at **Step 0 — Survey**. The survey groups the corpus by structure and tells you how many templates you need. Authoring one template per PDF when the batch shares one structure, or building for one PDF and missing a variant, is exactly what Step 0 prevents.

If scope is genuinely unclear, this is a valid moment for your one Rule 2 question.

---

## Step map

| Entry condition | Start at |
| --- | --- |
| Batch (folder/list/"all …") | **Step 0 — Survey** first |
| Single PDF | **Step 1 — Classify** (always first) |
| Strong match (`recommendation: "strong"`) | Skip to **Step 5 — Dry-run**, then Execute |
| Partial match (`recommendation: "partial"`, or top two scores within ~0.1) | Resolve with [`classification.md`](classification.md) § Partial-match decision, then Test/Build |
| No match (`recommendation: "no-match"` on every candidate) / empty store | **Step 2 — Inspect**, author from scratch |

```
(batch) Survey -> per group:
Classify -> strong ------------------------------------> Dry-run -> Execute -> done
         -> partial ---------> resolve -> Test -> Build -> Dry-run -> Execute -> Store
         -> no-match / error -> Inspect -> Test -> Build -> Dry-run -> Execute -> Store
```

---

## Progress checklist

For any non-trivial run, copy this checklist into your working notes and check items off as you go. Skip the steps classification lets you skip (a strong match jumps straight to dry-run), but never skip a **blocking gate**.

```
Docuoria run:
- [ ] Scope settled (single-PDF vs batch)
- [ ] Whole document understood — every page 1..pageCount accounted for (Rule 1)
- [ ] Classified against the store (Step 1)
- [ ] Patterns proven against the flattened text (Step 3)        — authoring only
- [ ] Template authored from the loaded specs (Step 4a)          — authoring only
- [ ] BLOCKING GATE: negative validation is "no-match" (Step 4b)  — authoring only
- [ ] Schema validates with zero errors (Step 4c)                — authoring only
- [ ] Dry-run completeness.isComplete == true (Step 5)
- [ ] BLOCKING GATE: regression-check exits 0 (Step 5b)          — modified template only
- [ ] Executed; exit code 0 and completeness.isComplete (Step 6)
- [ ] Stored and ranking re-verified (Step 7)                    — new/modified template only
```

---

## Step 0 — Survey (batch only)

Run only when more than one PDF is in scope (a directory, a list, or "all PDFs in…").

- **Script:** `dotnet script scripts/survey.csx -- --corpus <dir> --store-path <abs-store-dir> [--strict]`
- **Returns:** `{ pdfCount, matchedGroups: [ { template, pdfs[], representative } ], unmatched: [ { pdf, pageCount, structuralTokens[] } ], guidance }`
- **Exit codes:** `0` normal · `2` (`--strict`) at least one PDF is unmatched · `1` fewer than 2 PDFs.

**Survey reports facts, not a verdict.** Like `inspect`, it does not decide how many templates you need — you do, by reasoning over what it returns:

- **`matchedGroups`** — these PDFs already classify to a stored template. Reuse it: confirm on the `representative` with `classify.csx`, then dry-run the rest of the group.
- **`unmatched`** — these need authoring, and **you MUST decide the grouping by reasoning**, not by any single field:
  1. `structuralTokens` overlap is a *hint*, not proof. PDFs whose tokens largely overlap **and** whose page layout matches likely need ONE template.
  2. A different `pageCount` often means the same template with extra line-item pages — **not** a new document type. Confirm by inspecting the candidates across every page (Rule 1).
  3. Before authoring a split, prove mutual exclusivity with `evaluate-match.csx` negative validation (Step 4b): a sibling PDF must return `recommendation: "no-match"`.
- **Never author one template per PDF.** Over-fragmenting a batch that shares one structure is the failure this step exists to prevent.

**Action:** for each structure you conclude exists, author one template from a representative PDF, then run Steps 1–7. The members of a structural group are your cross-validation set — positive examples for their own template, **negative** examples for the others (Step 4b).

**Batch execute:** once every structural group has a stored template, run `dotnet script scripts/batch-execute.csx -- --corpus <dir> --store-path <abs-store-dir> --output <merged.csv>` instead of per-PDF execute calls. It classify-routes each PDF on `recommendation`, merges all rows into one CSV with leading `sourceFile`/`templateId` columns, and reports per-PDF status. Resolve any `"partial-match"`/`"skipped"` entries per [`classification.md` § Partial-match decision](classification.md#partial-match-decision-using-the-ambiguity-block) before re-running. `--format json` produces `{ sourceFile, templateId, data }` envelopes instead; `--output` may contain `{templateId}` / `{sourceFile}` tokens when the user wants one file per structure or per input.

**Canonical field vocabulary (multi-template ledgers):** when several templates feed ONE merged output, name same-meaning fields **identically** across them (`vendor`, `invoiceNumber`, `invoiceDate`, `currency`, `subtotal`, `tax`, `total`, `description`, …). The merged header is the union of template headers — `tax` in one template and `hst` in another become two half-empty columns and break the user's pivots. Align names at authoring time; do not rename fields on a stored template without a regression check.

**Recurring exports (ledgers):** when the user will run this again — "add this month's invoices", "keep one running spreadsheet", any append/update phrasing — use `--append`:

- `batch-execute.csx -- --corpus <dir> --store-path <abs-store-dir> --output <ledger.csv> --append` works in month 1 (creates) and month N (extends). PDFs already recorded in the ledger are **skipped before classification** (`status: "duplicate"`), so sweeping the whole folder every month is correct and cheap — only new files are processed. Re-running the exact same command is a safe no-op.
- Idempotency is per source file name; `--on-duplicate replace` refreshes a recorded PDF's rows in place (e.g. after a template fix), `fail` aborts if any duplicate exists.
- The existing ledger defines the output **grain and column shape** — do not re-author granularity for an established ledger. A new structural variant may add columns (reported via `columnsAdded`); disclose that to the user.
- Safety contract (also why you should prefer `--append` for anything long-lived): append refuses to touch files that aren't recognizable ledgers (`not-a-ledger`); a plain rebuild refuses when the ledger records sources missing from the corpus (`would-drop-sources`) — never work around these with manual file edits, and never pass `--overwrite` unless the user explicitly accepts losing the recorded rows.
- Single-PDF monthly flow: `execute.csx -- --pdf <new.pdf> --template <t.json> --format csv --output <ledger.csv> --append` (same semantics, one document). Always report what happened from the `ledger` envelope: appended / replaced / already present.

---

## Step 1 — Classify (always first; never skip)

Classification is cheap and is your first discrimination gate. Run it before inspecting or authoring. A directory listing of the store is **not** a substitute — file names tell you nothing about which template matches this PDF's content.

- **Script:** `dotnet script scripts/classify.csx -- --pdf <pdf> --store-path <abs-store-dir> [--top N]`
- **API:** `IDocuoriaEngine.ClassifyRankedAsync` — opens the PDF once, scores every stored template's `rootMatchRule`, and returns matches ranked by `classificationScore` (descending). Templates whose requirements are unsatisfied are excluded.
- **Output:**

  ```json
  { "matches": [
    { "templateId": "...", "classificationScore": 0.62, "recommendation": "strong",
      "requirementsSatisfied": true,
      "specificityScore": 1.0, "matchQuantityScore": 0.8, "coverageScore": 0.6,
      "ruleConfidence": 1.0, "ambiguity": null }
  ] }
  ```

- **Score:** `classificationScore = requirementsSatisfied ? ruleConfidence × (0.5·specificity + 0.3·quantity + 0.2·coverage) : 0`. The score orders candidates and flags near-ties (~0.1 delta) — never route on its absolute value; a perfect template commonly self-scores 0.6–0.7. Full interpretation: [`classification.md` § Interpreting the recommendation](classification.md#interpreting-the-recommendation).
- **Routing — on `recommendation`, never on absolute score:**
  - **`"strong"`** → rule matched, all requirements satisfied, every declared mapping produced ≥ 1 match. Skip to **Step 5 — Dry-run**, then Execute.
  - **`"partial"`**, or the top two scores within ~0.1 (an `ambiguity` block is present) → the rule matched but at least one mapping found nothing. Resolve with [`classification.md` § Partial-match decision](classification.md#partial-match-decision-using-the-ambiguity-block) before editing anything.
  - **`"no-match"` on every candidate**, or no matches → author a new template. Go to **Step 2**.
- **`error: no-store`** (empty/missing store) is expected when authoring the first template — go to Step 2. Running classify is still how you confirm there is nothing to reuse.

---

## Step 2 — Inspect (when no template matches)

Read what the engine actually extracts. Its flattened text often differs from the visual layout (whitespace, line breaks, encoding). This step is where you satisfy **Rule 1**.

- **Script:** `dotnet script scripts/inspect.csx -- --pdf <pdf>` (no `--page` → reads the whole document)
- **API:** `IDocuoriaEngine.InspectAsync`
- **Returns:** `PdfInspection` — `pageCount`, `metadata`, and `pages[*]` with `pageNumber`, `flattenedText`, `blocks[*]` (`bounds`, `content`), and `tables[*]` (`headerPreview`, `rowPreviews`, `totalRowCount`).
- **Required:** enumerate every page (1…`pageCount`) and note its contents and every `tables[*].totalRowCount`. Do not summarize the document from one page.
- **Stop condition:** if `pageCount` is 0 or every page has empty `blocks`, the PDF is scanned/image-only — stop and OCR upstream first.

After inspecting, apply **Rule 2**: if the desired output is ambiguous, ask the one plain-language question now. Then continue to Step 3.

---

## Step 3 — Test patterns

Prove every regex against the engine's flattened haystack — never against the visible text.

- **Scripts:**
  - `dotnet script scripts/test-pattern.csx -- --pdf <pdf> --pattern "<regex>"` — one pattern.
  - `dotnet script scripts/test-groups.csx -- --pdf <pdf> --pattern "<regex>"` — each capture group independently, when a multi-group regex partially fails.
- **API:** `IDocuoriaEngine.TestPatternAsync` / `TestGroupsAsync`
- **Returns:** `PatternTestResult` — `hasMatches`, `matches`, `gaps`, an `error` (on invalid syntax/timeout), and `nearMiss` (on a valid zero-match probe: the longest partial match plus the haystack `breakIndex` — read it to see exactly where the pattern broke).
- **Iterate** with [`patterns.md`](patterns.md). On a non-match, read `nearMiss.breakIndex` and consult [`troubleshooting.md`](troubleshooting.md). Repeat until `hasMatches` is `true` and the match count meets expectation.

---

## Step 4 — Build the template

### 4a — Author the JSON

**You MUST have loaded both of these before writing any template JSON:**

- [`template-reference.md`](template-reference.md) — the exact JSON shape, `$kind` discriminators, and enum values.
- [`extraction-sources.md`](extraction-sources.md) — which `ExtractionSource` subtype each field needs.

Authoring before reading these is the most common authoring failure (inventing a non-existent template shape). The tables in `SKILL.md` are a menu; these references are the authoritative spec. Run `schema-info.csx` for the live discriminator set.

Then:

- Map each confirmed pattern to the right `ExtractionSource` subtype (per `extraction-sources.md`).
- **Row-granularity contract (collections):** the repeating unit of every
  `RepeatingFieldMapping` MUST equal the unit the user wants as a spreadsheet row.
  "Each charge as its own line" means each product / usage category / line entry —
  not the section summary that totals them. Before authoring, count the unit's
  occurrences in the haystack (e.g. how many product names or category rows each
  page shows); your collection pattern must match exactly that many times in
  `test-pattern.csx`. Fewer matches than visible units = wrong pattern — iterate
  using `nearMiss`, do NOT fall back to extracting a coarser summary row. Rolling
  detail up into a summary is permitted only when the haystack provably lacks
  per-unit values (cite the blocks when you report it) — never because a pattern
  attempt failed.
- Design a `rootMatchRule` that identifies the **document type**, not just the vendor (per [`classification.md`](classification.md)). Use a `CompositeMatchRule` (And) with discriminator children weighted ≥ 2.0, built from **structural tokens** unique to this document type — section headers, product identifiers, column names siblings do not share. Never build a discriminator from an extracted field value; `validate-template.csx` warns when a discriminator token overlaps an extraction pattern.
- Declare `requirements` when a sibling template could also match this PDF (see [`classification.md` § Declaring requirements](classification.md#declaring-requirements)). Requirements are a **hard gate**: any unsatisfied requirement drops `classificationScore` to 0 and hides the template from `classify.csx`.

### 4b — Verify classification quality (negative validation is a blocking gate)

- **Script:** `dotnet script scripts/evaluate-match.csx -- --pdf <pdf> --template <template.json>`
- **Positive:** the target PDF must return `recommendation: "strong"`.
- **Negative (required):** run it against at least one PDF that should NOT match (a sibling or different document type). It must return `recommendation: "no-match"` (`isMatch: false`). A `"partial"` on a sibling means the rules lack discrimination — strengthen the discriminator and re-test.
- If an `ambiguity` block is present, read `discriminatorTokensAbsent`; see [`classification.md` § Partial-match decision](classification.md#partial-match-decision-using-the-ambiguity-block).
- **Do NOT proceed to Step 5 until negative validation passes.**

### 4c — Validate the schema

- **Script:** `dotnet script scripts/validate-template.csx -- --template <template.json>`
- Fix every reported error (`severity: 2`). A schema failure equals `RejectionReason.MalformedTemplate` at runtime. Address warnings (`severity: 1`) too — `TPL_DISCRIMINATOR_FROM_VALUE` means a discriminator overlaps an extracted value.

---

## Step 5 — Dry-run

Extract and transform without producing output, to confirm the data is correct.

**Warm-store shortcut:** when `classify.csx` returned `recommendation: "strong"` for
an **unmodified stored template**, you may skip the separate dry-run and go straight
to Step 6 — `execute.csx` performs the same completeness verification and exits `2`
on incomplete extraction. The separate dry-run remains mandatory for any template
you authored or edited in this session.

- **Script:** `dotnet script scripts/dry-run.csx -- --pdf <pdf> --template <template.json>`
- **API:** `IDocuoriaEngine.DryRunAsync`
- **Returns** (discriminated by `kind`):
  - `DryRunSucceeded` — `jsonProjection`, `diagnostics`, `rawHaystack`, and `completeness`.
  - `DryRunFailed` — `step` (0 Unknown, 1 Retrieval, 2 Extraction, 3 Transformation, 4 Publish), `fieldPath`, `sourceText` (≤ 256 chars), `targetTypeName`, `innerDetail`. A dry-run also runs the publish step's record schema validation, so `step: 4` with `Required field has null value` is the same verdict execute would give — fix it now (see [`troubleshooting.md` § Required field null at publish](troubleshooting.md#required-field-null-at-publish)); do not expect execute to behave differently.
  - `DryRunRejected` — `reason` (0 InvalidPdf, 1 MalformedTemplate, 2 UnknownOutputGenerator, 3 GeneratorRejected), `detail`.
- **`DryRunSucceeded` is not "complete".** Read `completeness`:
  - `isComplete: true` → proceed.
  - `isComplete: false` (exit code 2) → the run is **incomplete, which is a failure to fix, not a success**. Read `missingRequiredFields`, `emptyDeclaredCollections`, and `unsatisfiedRequirements`, then repair the template (tighten or add a mapping, fix a pattern, or split a variant). Recovery routing: [`troubleshooting.md` § Incomplete success](troubleshooting.md#incomplete-success-exit-code-2).
- **On failure:** go to [`troubleshooting.md`](troubleshooting.md), indexed by `step` or `reason`.
- **Unit-count check (collections):** a non-empty collection is not automatically a
  correct one. The row count in `jsonProjection` must equal the number of repeating
  units visible in the haystack (count the per-unit anchor — product names, category
  rows, line entries). One row where the document shows five means the pattern
  matched a summary, not the units — go back to Step 4a's row-granularity contract.
- **Arithmetic cross-check (financial documents):** where the document prints the
  identity, verify it on the extracted values — `subtotal + tax == total`, and line
  amounts sum to the printed subtotal. This is the only check that catches "the
  pattern matched a plausible but WRONG value" (e.g. a label-substring collision,
  `patterns.md` § Anchoring) — completeness and match counts cannot. A failed
  identity on any variant is a pattern bug; do not ship it.
- **Variant check:** repeat the dry-run with every available PDF of the same type. Empty collections or null scalars where data should exist signal over-fitting — see [`troubleshooting.md` § Silent or empty results](troubleshooting.md#silent-or-empty-results).

If you are modifying an already-stored template, do Step 5b next; otherwise go to Step 6.

---

## Step 5b — Regression check (blocking gate when modifying a stored template)

Modifying a stored template can silently break the PDFs it already handled. Before you overwrite one, prove it still works.

- **Script:** `dotnet script scripts/regression-check.csx -- --modified <updated.json> --baseline <stored.json> --corpus <pdfs-dir>` (or `--baseline-id <id>` to pull the baseline from the store).
- **Corpus:** the PDFs that previously classified to this template; assemble them in a working directory first.
- **Returns:** per-PDF `scalarDiffs`, `collectionDiffs`, `isRegression`, `isImprovement`, plus `summary { regressionsDetected, improvementsDetected, unchanged }`.
- **Exit codes:** `0` clean · `2` regressions (stop and fix) · `1` invalid input.
- **Do NOT proceed to Step 7 if any PDF regresses** (a field that used to extract now returns null, or a collection shrinks to zero rows).

**Prefer splitting over mutating.** When a new PDF is a genuine structural variant, author a *sibling* template and tighten the original's discriminator rather than loosening the original to cover both. Silent mutation of a shared template is the highest-risk action in this skill. See [`classification.md` § Partial-match decision](classification.md#partial-match-decision-using-the-ambiguity-block).

---

## Step 6 — Execute

Full run, including output generation.

- **Script:** `dotnet script scripts/execute.csx -- --pdf <pdf> --template <template.json> --format csv|json` (optionally `--output <path>`)
- **API:** `IDocuoriaEngine.ExecuteTemplateAsync<TGenerator, TOptions>`
- **Returns:** to stdout, `{ status: "ok", format, output, completeness }` (or `{ status: "ok", path, completeness }` with `--output`). Internally a `SucceededResult`, `FailedResult`, or `RejectedResult` (`reason` ∈ {InvalidPdf, MalformedTemplate, UnknownOutputGenerator, GeneratorRejected}).
- **Exit codes:** `0` complete · `2` succeeded but incomplete (same `completeness` gate as dry-run) · `1` failed/rejected.
- **On failure:** go to [`troubleshooting.md`](troubleshooting.md).
- **Batch scope:** when every structural group already has a stored template, use `batch-execute.csx` for the whole corpus instead of per-PDF execute calls (see Step 0 — Batch execute).
- **Recurring scope:** when the output should accumulate across runs (monthly invoices into one spreadsheet), add `--append` — see Step 0 — Recurring exports (ledgers). A plain `--output` aimed at an existing ledger is refused (`existing-ledger`); that error means you wanted `--append`.
- If Step 1 routed here directly via a strong match, you are done. If this is a new or modified template, go to Step 7.

---

## Step 7 — Store

Persist the template and confirm it ranks correctly. This prevents it from stealing classifications from siblings or ranking too low for its own target.

**Do not save until all of these hold:**

- Negative `evaluate-match` returns `"no-match"` on a sibling/different document (4b). A `"partial"` on a sibling is insufficient discrimination — fix it first.
- Positive `evaluate-match` returns `"strong"` on the target PDF.
- `validate-template.csx` reports no errors.
- Dry-run `completeness.isComplete` is `true` for the confirmed scope.
- If modifying an existing template, regression-check (5b) exited `0`.

Then:

- **Save:** `dotnet script scripts/save-template.csx -- --template <template.json> --store-path <abs-store-dir>` (add `--overwrite` to replace an existing id). Output: `{ status: "ok", identifier }`.
  Author the working template file **outside the store directory** (project root or a
  drafts folder) — a draft written straight into the store is "stored" without ever
  passing the gates above, and `save-template` will then refuse with `already-exists`.
- **Verify ranking:**
  - `classify.csx` on the target PDF → the new template ranks **#1** with `recommendation: "strong"`.
  - `classify.csx` on a sibling PDF → the new template returns `"no-match"`; existing siblings still rank #1 for their own PDFs.
- **Other store scripts:** `list-templates.csx` (enumerate), `load-template.csx --id <id>` (fetch one).

---

## Quick reference

| Step | Script(s) | Engine API | Result |
| --- | --- | --- | --- |
| 0 Survey | `survey.csx` | `PdfCorpusSurvey` | Structural facts (matched groups + unmatched profiles) + reasoning guidance |
| 1 Classify | `classify.csx` | `ClassifyRankedAsync` | Ranked `matches` with `classificationScore` |
| 2 Inspect | `inspect.csx` | `InspectAsync` | `PdfInspection` (all pages) |
| 3 Test | `test-pattern.csx`, `test-groups.csx` | `TestPatternAsync`, `TestGroupsAsync` | `PatternTestResult` |
| 4 Build | (editor), `evaluate-match.csx`, `validate-template.csx` | `EvaluateMatchAsync` | Template JSON + validation |
| 5 Dry-run | `dry-run.csx` | `DryRunAsync` | `DryRunResult` + completeness |
| 5b Regression | `regression-check.csx` | `TemplateRegressionDiff` | Regression summary |
| 6 Execute | `execute.csx` | `ExecuteTemplateAsync` | `ProcessingResult` + completeness |
| 7 Store | `save-template.csx`, `classify.csx` | `ClassifyRankedAsync` | Ranking verification |

---

## CSV output behaviour

`--format csv` flattens the hierarchical record into tabular CSV.

| Template shape | Behaviour |
| --- | --- |
| Scalar fields only | One row, one column per field |
| One `RepeatingFieldMapping` | Denormalised: scalars repeat on every row, each collection element gets one row; headers use dot notation (`lineItems.description`) |
| Two+ `RepeatingFieldMapping` | Rejected (`RejectionReason.GeneratorRejected`) — use JSON, or split into separate templates |
| Nested `RecordFieldDefinition` | Flattened with dot notation (`address.city`) |

### CsvGeneratorOptions

| Property | Type | Default | Description |
| --- | --- | --- | --- |
| `Delimiter` | `char` | `,` | Field separator |
| `Encoding` | `Encoding` | UTF-8 (no BOM) | Output encoding |
| `NewlineReplacement` | `string?` | `" "` | Embedded newlines in string values collapse to a single space (wrapped PDF text reads naturally in Excel); `"\n"` emits literal escape text preserving newline positions; `null` preserves raw newlines in RFC 4180 quoted cells |
| `IncludeHeaderRow` | `bool` | `true` | Whether to emit a header row |
| `DateFormat` | `string?` | `null` (ISO 8601) | .NET date format string for `Date` fields |
| `NumberFormat` | `string?` | `null` (general `G`) | .NET format string for `Number` fields |

