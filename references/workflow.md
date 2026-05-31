# Workflow

The pipeline has seven steps. **Step 1 (Classify) always runs first** and determines whether steps 2â€“4 are needed. Never skip step 1 â€” classification is cheap and avoids redundant exploration.

```
1 Classify â”€â”€â–º strong match (â‰¥ 0.8) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â–º 5 Dry-run â”€â”€â–º 6 Execute â”€â”€â–º done
     â”‚
     â”œâ”€â”€â–º partial match (0.4â€“0.8) â”€â”€â–º 3 Test pattern â”€â”€â–º 4 Build template â”€â”€â–º 5 Dry-run â”€â”€â–º 6 Execute â”€â”€â–º 7 Store
     â”‚         (load existing template, fix broken fields only)
     â”‚
     â””â”€â”€â–º no match / error â”€â”€â–º 2 Inspect â”€â”€â–º 3 Test pattern â”€â”€â–º 4 Build template â”€â”€â–º 5 Dry-run â”€â”€â–º 6 Execute â”€â”€â–º 7 Store
```

---

## Step 1 â€” Classify

Determine whether an existing template already handles this PDF.

- **Script:** `dotnet script scripts/classify.csx -- --pdf <pdf>`
- **API:** `IDocuoriaEngine.ClassifyAsync` â€” evaluates every stored template's `rootMatchRule` and returns them ranked by `confidence` (`ruleConfidence Ã— extractionProbeScore`).
- **Output:** `{ "matches": [ { "templateId": "...", "confidence": 0.92 }, ... ] }` â€” descending by confidence.

**Routing:** see [`classification.md` Â§ Interpreting the gradient](classification.md#interpreting-the-gradient) for the canonical confidence-to-action table (â‰¥ 0.8 strong, 0.4â€“0.8 partial, < 0.4 author new). On `error: no-store` (no template store configured), proceed to **Step 2**.

---

## Step 2 â€” Inspect

See what the engine actually reads from the PDF before writing any patterns. The engine's text output often differs from the visual layout.

- **Script:** `dotnet script scripts/inspect.csx -- --pdf <pdf>` (optionally `--page N` for a single page)
- **API:** `IDocuoriaEngine.InspectAsync`
- **Returns:** `PdfInspection` â€” `PageCount`, `Pages[*].FlattenedText`, `Pages[*].TextBlocks`, `Pages[*].Tables` snapshots.
- **Gate:** if `PageCount` is 0 or every page has empty `TextBlocks`, the PDF is scanned/image-only â€” **STOP**. OCR upstream first.
- **Next:** Step 3.

---

## Step 3 â€” Test pattern

Prove each regex matches the engine's flattened haystack â€” not the visible text.

- **Scripts:**
  - `dotnet script scripts/test-pattern.csx -- --pdf <pdf> --pattern "<regex>"` â€” test a single pattern.
  - `dotnet script scripts/test-groups.csx -- --pdf <pdf> --pattern "<regex>"` â€” test each capture group independently when a multi-group regex partially fails.
- **API:** `IDocuoriaEngine.TestPatternAsync` / `IDocuoriaEngine.TestGroupsAsync`
- **Returns:** `PatternTestResult` â€” `HasMatches`, `Matches`, `Gaps`. For groups: `PatternGroupTestResult.Groups[*].MatchesIndependently`.
- **Iterate:** use `patterns.md` and `pattern-authoring.md` as reference. Repeat until `HasMatches` is `true` and match count matches expectation.
- **Next:** Step 4.

---

## Step 4 â€” Build template

Assemble the template JSON, design its classification rules, and validate the schema. This step has three sub-tasks that must all pass before proceeding.

### 4a â€” Author template JSON

- Combine confirmed patterns with the appropriate `ExtractionSource` subtype (consult `decision-tree.md`).
- Design a discriminating `rootMatchRule` that identifies this **document type**, not just the vendor (consult `classification.md`).
  - Use `CompositeMatchRule` (And) with discriminator children weighted â‰¥ 2.0.
  - Identify tokens unique to this document type â€” section headers, product identifiers, column names that siblings do not share.

### 4b â€” Validate classification rules

- **Script:** `dotnet script scripts/evaluate-match.csx -- --pdf <pdf> --template <template.json>`
- **API:** `IDocuoriaEngine.EvaluateMatchAsync` â€” returns `confidence` (`ruleConfidence Ã— extractionProbeScore`).
- **Positive test:** target PDF â†’ confidence â‰¥ 0.8.
- **Negative test:** same-vendor PDFs that should NOT match â†’ confidence near zero. If 0.4â€“0.7, the rules lack discrimination â€” strengthen the discriminator.
- See `classification.md` for the full design guide.

### 4c â€” Validate schema

- **Script:** `dotnet script scripts/validate-template.csx -- --template <template.json>`
- Fix every reported error before proceeding. A schema failure corresponds to `RejectionReason.MalformedTemplate` at runtime.

**Next:** Step 5.

---

## Step 5 â€” Dry-run

Extract and transform without generating output. Confirms the template produces correct data before committing to a full run.

- **Script:** `dotnet script scripts/dry-run.csx -- --pdf <pdf> --template <template.json>`
- **API:** `IDocuoriaEngine.DryRunAsync`
- **Returns:** `DryRunSucceeded` (extracted fields + diagnostics), `DryRunFailed` (`Step`, `FieldPath`, `SourceText`, `TargetTypeName`, `InnerDetail`), or `DryRunRejected` (`RejectionReason`).

**On success:** verify all collection fields have the expected element count. An empty `[]` for a `RepeatingFieldMapping` means the pattern didn't match â€” see `failure-tree.md` Branch D.

**On failure:** go to `failure-tree.md` indexed by `Step` (for `DryRunFailed`) or `RejectionReason` (for `DryRunRejected`).

**Multi-variant check:** repeat dry-run with every available PDF from the same vendor/template category. If any variant produces empty collections or null scalars where data should exist, the template may be over-fitted â€” see `failure-tree.md` Branch D (layout variant splitting).

**Next:** Step 6.

---

## Step 6 â€” Execute

Full pipeline run including the output generator.

- **Script:** `dotnet script scripts/execute.csx -- --pdf <pdf> --template <template.json> --format csv|json` (optionally `--output <path>`)
- **API:** `IDocuoriaEngine.ExecuteTemplateAsync<TGenerator, TOptions>`
- **Returns:** `SucceededResult`, `FailedResult`, or `RejectedResult` with `RejectionReason` in {`InvalidPdf`, `MalformedTemplate`, `UnknownOutputGenerator`, `GeneratorRejected`}.
- **On failure:** go to `failure-tree.md`.
- **Next:** if this was a strong classify match (step 1 routed here directly) â†’ done. If this is a new or modified template â†’ Step 7.

---

## Step 7 â€” Store

Persist the template and verify it ranks correctly in the store. This prevents the new template from stealing classifications from existing templates or ranking too low for its own target.

- **Save:** `dotnet script scripts/save-template.csx -- --template <template.json>`
- **Verify ranking:**
  - `dotnet script scripts/classify.csx -- --pdf <target.pdf>` â†’ new template must rank **#1**, confidence â‰¥ 0.8.
  - `dotnet script scripts/classify.csx -- --pdf <sibling.pdf>` â†’ new template should score < 0.4; existing sibling templates should still rank #1 for their own PDFs.
- **API:** `IDocuoriaEngine.ClassifyAsync`, `IDocuoriaEngine.EvaluateMatchAsync`
- **Other store scripts:** `list-templates.csx` (list all), `load-template.csx --id <id>` (fetch one).

**Done.** The template is stored and will be found by Step 1 on future PDFs of this type.

---

## Quick reference

| Step | Script(s) | Engine API | Result |
|---|---|---|---|
| 1 Classify | `classify.csx` | `ClassifyAsync` | Ranked matches with `confidence` |
| 2 Inspect | `inspect.csx` | `InspectAsync` | `PdfInspection` |
| 3 Test pattern | `test-pattern.csx`, `test-groups.csx` | `TestPatternAsync`, `TestGroupsAsync` | `PatternTestResult` |
| 4 Build | (editor), `evaluate-match.csx`, `validate-template.csx` | `EvaluateMatchAsync` | Template JSON + validation |
| 5 Dry-run | `dry-run.csx` | `DryRunAsync` | `DryRunResult` |
| 6 Execute | `execute.csx` | `ExecuteTemplateAsync` | `ProcessingResult` |
| 7 Store | `save-template.csx`, `classify.csx` | `ClassifyAsync` | Ranking verification |

---

## CSV output behaviour

When `--format csv` is used with `execute.csx`, the `CsvOutputGenerator` flattens the hierarchical `DataRecord` into tabular CSV.

| Template shape | Behaviour |
|---|---|
| Scalar fields only | One data row, one column per field |
| One `RepeatingFieldMapping` | Denormalised: scalars repeat on every row, collection elements get one row each. Column headers use dot notation (`lineItems.description`) |
| Two+ `RepeatingFieldMapping` | **Rejected** (`RejectionReason.GeneratorRejected`). Use JSON output or split into separate templates |
| Nested `RecordFieldDefinition` | Flattened with dot notation (`address.city`) |

### CsvGeneratorOptions

| Property | Type | Default | Description |
|---|---|---|---|
| `Delimiter` | `char` | `,` | Field separator |
| `Encoding` | `Encoding` | UTF-8 (no BOM) | Output encoding |
| `NewlineReplacement` | `string?` | `"\n"` | Replace embedded newlines with literal escape text; `" "` collapses to spaces; `null` preserves raw newlines in RFC 4180 quoted cells |
| `IncludeHeaderRow` | `bool` | `true` | Whether to emit a header row |
| `DateFormat` | `string?` | `null` (ISO 8601) | .NET date format string for `Date` fields |
| `NumberFormat` | `string?` | `null` (general `G`) | .NET format string for `Number` fields |
