# Failure-mode decision tree

Every PDF run produces one of three outcomes: `SucceededResult` (done), `RejectedResult` (the engine refused before completing — see `RejectionReason`), or `FailedResult` (the engine ran a step and it threw — see `StepIdentifier`). Classification can additionally produce "no template matched". Use the matching subsection below.

## Stderr `error.code` → Branch routing

Every script emits errors as `{ "error": { "code": "<code>", "message": "...", "detail": "..." } }` on stderr with non-zero exit. Map the stderr `error.code` to a branch below before reading the prose narrative:

| `error.code` | Emitted by | Meaning | Go to |
| --- | --- | --- | --- |
| `pdf-not-found` | every script that takes `--pdf` | The path passed to `--pdf` does not resolve to a file | Fix the path (relative paths resolve from the cwd); re-run. No branch — this is an input error, not an SDK outcome. |
| `template-not-found` | `load-template.csx`, `evaluate-match.csx`, `dry-run.csx`, `execute.csx` | Template ID does not exist in the store, or the `--template` path does not resolve | Run `list-templates.csx` to confirm the ID, or correct the path. No branch. |
| `no-store` | `classify.csx`, `list-templates.csx`, `load-template.csx`, `save-template.csx` | `DOCUORIA_STORE_LOCAL_PATH` is unset or points to a missing directory | Set the env var; see `references/scripts.md` § Environment Variables. No branch. |
| `bad-format` | `validate-template.csx`, `dry-run.csx`, `execute.csx`, `save-template.csx` | Template JSON is malformed or fails schema validation (parse error, missing required property, invalid enum value) | **Branch A** — `RejectionReason.MalformedTemplate`. Run `validate-template.csx` for the schema error list. |
| `rejected` | `dry-run.csx`, `execute.csx` | Engine returned `RejectedResult` — see `RejectionReason` in stderr `detail` | **Branch A** — match on the `RejectionReason` enum value. |
| `failed` | `dry-run.csx`, `execute.csx` | Engine returned `FailedResult` — see `StepIdentifier` in stderr `detail` | **Branch B** — match on the `StepIdentifier` enum value. |
| `empty-result` / silent `DryRunSucceeded` with empty collections | `dry-run.csx`, `execute.csx` | The engine succeeded but a `RepeatingFieldMapping` returned `[]` or a scalar returned `null` unexpectedly | **Branch D** — silent extraction failure (no stderr; detect by inspecting stdout). |
| Wrong template ranked first, or no template ranked | `classify.csx` | Classification produced unexpected ordering — diagnose via the ranked confidence gradient | **Branch C** — see also [`classification.md` § Interpreting the gradient](classification.md#interpreting-the-gradient). |
| `already-exists` | `save-template.csx` | Template with the same ID already exists in the store | Pass `--overwrite` if intentional, or pick a different ID. No branch. |
| `unhandled` | any script | Unexpected exception inside the script (not an SDK outcome) — the `detail` field contains the stack | File a bug; this is an SDK or script defect, not a template/PDF problem. No branch. |

## Branch A — RejectedResult

### RejectionReason.InvalidPdf

- **Meaning:** the PDF stream could not be parsed.
- **Diagnose:** run `dotnet script scripts/inspect.csx -- <pdf>`. If `PdfInspection.PageCount` is `0`, the file is unparseable.
- **Remediation:** confirm the file is a real PDF (magic bytes `%PDF-`), not a renamed image or HTML. If the source is a scan, the engine cannot extract from rasterised content; OCR upstream first.

### RejectionReason.MalformedTemplate

- **Meaning:** the `Template` JSON is structurally invalid (schema violations, missing required fields, unknown discriminators).
- **Diagnose:** run `dotnet script scripts/validate-template.csx -- <template.json>`.
- **Remediation:** fix every validation error reported. Never bypass validation by editing past it — the runtime check is the same check.

### RejectionReason.UnknownOutputGenerator

- **Meaning:** the generic `TGenerator` passed to `IDocuoriaEngine.ExecuteTemplateAsync<TGenerator, TOptions>` is not registered in DI.
- **Diagnose:** search host startup for `AddOutputGenerator<TGenerator, TOptions>` (or convenience helpers such as `AddCsvOutputGenerator`).
- **Remediation:** register the generator before calling execute, or pick an already-registered generator.

### RejectionReason.GeneratorRejected

- **Meaning:** the generator refused the extracted data (e.g. multiple collections handed to a CSV generator).
- **Diagnose:** run `dotnet script scripts/dry-run.csx -- <pdf> <template.json>` — `DryRunSucceeded` shows what the generator would have received.
- **Remediation:** reshape the template (split into multiple templates, or pick a richer generator that accepts the shape).

## Branch B — FailedResult

Read `FailedResult.Step` (enum-typed `StepIdentifier`) first; everything else is diagnostic context.

### Step = Retrieval

- **Meaning:** a retrieval provider threw.
- **Diagnose:** read `FailedResult.ErrorMessage` and `FailedResult.Exception`. For HTTP retrieval check connectivity and 4xx/5xx status.
- **Remediation:** fix the retrieval source; rerun.

### Step = Extraction

- **Meaning:** a pattern, table, or anchor extraction step threw or could not produce a value.
- **Diagnose:** rerun via `dotnet script scripts/dry-run.csx -- <pdf> <template.json>` (diagnostics on by default). Read `FailedResult.FieldPath` to identify the field, then `dotnet script scripts/test-pattern.csx` (for `TextPatternExtractionSource` mode `"Pattern"` / `"AllMatches"`) or `dotnet script scripts/inspect.csx` (for `TableRowsExtractionSource` / `TableCellExtractionSource`).
- **Remediation:** see `pattern-authoring.md` or revisit `decision-tree.md`.

### Step = Transformation

- **Meaning:** a field coercion failed (string → `DateOnly`, `decimal`, etc.).
- **Diagnose:** read the structured fields on `FailedResult`: `FieldPath` (which field), `SourceText` (the raw captured substring, capped at 256 chars with a trailing ellipsis), `TargetTypeName` (the destination type), `InnerDetail` (the coercion exception detail).
- **Remediation:** either tighten the regex to capture a coercible substring (see `patterns.md` patterns 4 vs. 3), or change the field type, or add a transform step.

### Step = Publish

- **Meaning:** the output generator threw mid-write (vs. cleanly rejecting, which would yield `RejectionReason.GeneratorRejected`).
- **Diagnose:** read `FailedResult.Exception`.
- **Remediation:** typical causes are I/O (path not writable, file locked) or generator bugs — fix infrastructure first, then file a generator issue.

### Step = Unknown

- **Meaning:** legacy/default value (assigned `0`). Observed when the legacy three-arg `FailedResult` constructor was used and no `Step` was set.
- **Diagnose:** treat as `Extraction` until proven otherwise; read `FailedResult.ErrorMessage` and `FailedResult.Exception`.
- **Remediation:** proceed as for the inferred step.

## Branch C — Classification failure

Classification issues come in three forms: no template matched, the wrong template matched, or no template is confident enough. Use `classify.csx` to get a ranked view of all templates with their `confidence` scores — this replaces the binary match/no-match model with a confidence gradient.

### First step: get the ranked classification

```
dotnet script scripts/classify.csx -- --pdf <pdf>
```

This returns the top-N templates sorted by `confidence` (descending). For the canonical confidence-to-action table, see [`classification.md` § Interpreting the gradient](classification.md#interpreting-the-gradient). If the matches array is empty, no templates are stored at all — author from scratch (`workflow.md` Step 3).

### Scenario: Wrong template matched (misclassification)

- **Meaning:** the top-ranked template with high `confidence` produces empty or incorrect data because the PDF belongs to a different document type from the same vendor.
- **How to detect:** `DryRunSucceeded` with empty collections or null scalars where data should exist (→ Branch D), OR extracted values are nonsensical (wrong field mapped to wrong content).
- **Diagnose:**
  1. Run `classify.csx` — check if the correct template appears lower in the ranked list. If so, the correct template's rules are weaker than expected.
  2. Run `dotnet script scripts/inspect.csx -- --pdf <pdf>` — compare the page structure against the PDF used when authoring the matched template.
  3. Check the `confidence` gap between the wrong match and the correct template — a narrow gap means the rules lack discrimination.
- **Remediation:**
  1. The matched template's `rootMatchRule` is too broad — it matches documents it cannot extract from. See `classification.md` for how to tighten.
  2. Strengthen the correct template's discriminating rules to boost its `confidence` above the wrong match.
  3. Validate with `classify.csx` — the correct template must rank #1 with a clear gap over sibling templates.

### Scenario: No match at all

- **Meaning:** all templates score near zero `confidence`.
- **Remediation:** author a new template (back to `workflow.md` Step 3). If any template scores moderately (0.3+), use `load-template.csx` to retrieve it as a structural starting point — its field layout and extraction sources may transfer even if its match rules don't fit.

### Root cause patterns for classification failures

| Pattern | Symptom | Fix |
|---|---|---|
| Vendor-only tokens | Multiple document types from same vendor score similarly | Add document-type-specific discriminators (see `classification.md`) |
| AnyToken with low threshold | Template matches when only generic tokens are present | Switch to AllTokens for critical gates, or raise threshold |
| No structural rules | Two documents share text tokens but have different layouts | Add `TableMatchRule`, `PageGeometryMatchRule`, or `TextAnchorMatchRule` |
| Single rule (no composite) | No layered defense | Use `CompositeMatchRule` with weighted discriminators |
| Never tested negative examples | Rule seems fine against target but matches siblings too | Always validate against same-vendor PDFs that should NOT match |
| Narrow confidence gap | Top two templates score within 0.1 of each other | Strengthen discriminators on both to widen the gap |

## Branch D — DryRunSucceeded but data is empty or incomplete

This is the *silent failure* mode — the pipeline reports success but one or more fields (typically a `RepeatingFieldMapping`) returned `null` or an empty collection. The dry-run does not flag this as an error because the engine has no expectation about how many rows should be extracted.

### Symptom: RepeatingFieldMapping returns `[]` (empty collection)

- **Diagnose:**
  1. Run `dotnet script scripts/dry-run.csx -- <pdf> <template.json>` — confirm the collection field is present but empty.
  2. Run `dotnet script scripts/inspect.csx -- <pdf> --page <N>` — examine `FlattenedText` and `Tables` for the page containing the expected data.
  3. If `Tables` shows entries with `TotalRowCount > 1` and meaningful `RowPreviews`, the data IS detectable as a table → switch to `TableRowsExtractionSource`.
  4. If `Tables` shows only header-like entries (1 row) or no entries, read `FlattenedText` carefully. The block flattening order may differ from the visual PDF layout.
  5. Run `dotnet script scripts/test-pattern.csx -- <pdf> "<your-regex>"` against the ACTUAL flattened text (not the text you see when viewing the PDF).

- **Root causes (in likelihood order):**

| Cause | How to confirm | Remediation |
|---|---|---|
| **Layout variant** — The template was designed for a different document layout from the same vendor | Compare the PDF's page 2+ structure against the one used during authoring. Look for differences in section headers, column layout, or whether data appears per-product vs. in a single table. | **Split into separate templates** with narrower match rules. See [`classification.md` § Worked example: splitting Microsoft invoices](classification.md#worked-example-splitting-microsoft-invoices) for the canonical procedure. |
| **Block order differs from visual order** — The PDF's internal text blocks are column-grouped (all labels in one block, all values in another) rather than row-grouped | Run `inspect.csx` and read the `Blocks` array with coordinates. If all row labels share one block and values are in separate column-aligned blocks, the flattened haystack destroys row associations. | If `Tables` detection finds the structure → use `TableRowsExtractionSource`. Otherwise, this layout may not be extractable with the current SDK; document as a known limitation. |
| **Pattern mismatch** — The AllMatches regex was written for a different text structure | Run `test-pattern.csx` — if `HasMatches` is `false`, the pattern doesn't match the actual haystack. | Rewrite the regex against the actual `FlattenedText` from `inspect.csx`. See `pattern-authoring.md`. |
| **Page targeting** — The extraction source targets the wrong page | Check `pageNumber` in the extraction source config vs. where the data actually appears. | Correct the page number or remove it to search all pages. |

### Layout variant splitting

When a single template classifies multiple document layouts correctly (same vendor, same match tokens) but extraction works for only one layout, follow [`classification.md` § Worked example: splitting Microsoft invoices](classification.md#worked-example-splitting-microsoft-invoices) — the canonical step-by-step procedure for splitting templates, choosing discriminators, and validating mutual exclusivity.

### Symptom: Scalar field returns `null` unexpectedly

- **Diagnose:** run `dotnet script scripts/test-pattern.csx -- <pdf> "<regex>"` with the field's regex. If no match, inspect the haystack for the actual text surrounding the expected value.
- **Remediation:** adjust the regex. If the field genuinely doesn't exist in this document variant, consider making it non-required (`"isRequired": false`) or splitting templates.

## Quick reference

| Outcome | Enum value | Script | Remediation pointer |
| --- | --- | --- | --- |
| `RejectedResult` | `RejectionReason.InvalidPdf` | `inspect.csx` | check magic bytes / OCR upstream |
| `RejectedResult` | `RejectionReason.MalformedTemplate` | `validate-template.csx` | fix schema errors |
| `RejectedResult` | `RejectionReason.UnknownOutputGenerator` | (host startup) | register generator in DI |
| `RejectedResult` | `RejectionReason.GeneratorRejected` | `dry-run.csx` | reshape template or change generator |
| `FailedResult` | `Step = Retrieval` | (inspect retrieval) | fix retrieval source |
| `FailedResult` | `Step = Extraction` | `dry-run.csx`, `test-pattern.csx`, `inspect.csx` | `pattern-authoring.md`, `decision-tree.md` |
| `FailedResult` | `Step = Transformation` | `dry-run.csx` | tighten pattern or change field type |
| `FailedResult` | `Step = Publish` | (inspect I/O) | fix infrastructure |
| `FailedResult` | `Step = Unknown` | `dry-run.csx` | treat as Extraction |
| `DryRunSucceeded` | empty collection | `dry-run.csx`, `inspect.csx`, `test-pattern.csx` | Branch D above — layout variant or block-order mismatch |
| Classification | no match | `classify.csx`, `evaluate-match.csx` | Branch C — examine ranked results, author or refine template |
| Classification | wrong match | `classify.csx`, `inspect.csx` | Branch C — tighten rules, strengthen discriminators (see `classification.md`) |
