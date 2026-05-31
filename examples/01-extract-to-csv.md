# Example 1 — Extract to CSV (known template)

## Scenario

A PDF document with a header (a date and a reference number) and a repeating list of line items. A `Template` already exists in the local store; the goal is to produce a CSV from this PDF without authoring anything new.

## Template excerpt

The full template shape for this scenario (`PrimitiveFieldDefinition` for `refNumber` + `invoiceDate`, `RecordFieldDefinition` with `isCollection: true` for `lineItems`, three `FieldMapping`/`RepeatingFieldMapping` extraction mappings) is documented in [`../references/template-reference.md` § Complex template](../references/template-reference.md#complex-template-scalar-fields--repeating-collection). The single mapping that drives the repeating CSV rows looks like:

```json
{
  "$kind": "RepeatingFieldMapping",
  "collectionFieldName": "lineItems",
  "source": {
    "$kind": "TextPatternExtractionSource",
    "mode": "AllMatches",
    "regexPattern": "(?<description>[A-Za-z ]+?)\\s+\\$(?<amount>[\\d,.]+)"
  },
  "subFields": [
    { "$kind": "NamedGroupSubFieldMapping", "fieldName": "description", "fieldType": 0, "groupName": "description" },
    { "$kind": "NamedGroupSubFieldMapping", "fieldName": "amount", "fieldType": 1, "groupName": "amount" }
  ]
}
```

Note: `fieldType` is an **integer** (0 = String, 1 = Number, 4 = Date). See the template reference for the canonical enum table.

## Steps

1. `dotnet script scripts/list-templates.csx` — find the template ID that should apply.
2. `dotnet script scripts/load-template.csx -- --id <id>` — confirm the template loaded and inspect its fields. Add `--output <path>` to write the JSON to a file.
3. `dotnet script scripts/dry-run.csx -- --pdf <pdf> --template <template.json>` — confirm a `DryRunSucceeded` outcome. Inspect the extracted fields and (if needed) the `ExtractionDiagnostics` snapshot before publishing.
4. `dotnet script scripts/execute.csx -- --pdf <pdf> --template <template.json> --format csv --output output.csv` — runs the full pipeline through the registered CSV generator. Engine API used: `IDocuoriaEngine.ExecuteTemplateAsync<CsvOutputGenerator, CsvGeneratorOptions>`.

## Expected outcome

`ProcessingResult` is `SucceededResult`; `output.csv` exists with rows matching the document's line items, in document order, with the header columns implied by the template's schema.

## If it fails

Go to [`../references/failure-tree.md`](../references/failure-tree.md). Map the script's stderr `error.code` to a branch via [§ Stderr error.code → Branch routing](../references/failure-tree.md#stderr-errorcode--branch-routing): `rejected` → Branch A (read `RejectionReason` in `detail`); `failed` → Branch B (read `StepIdentifier` in `detail`). If `dry-run.csx` already returned `DryRunSucceeded` but `execute.csx` then returned `rejected` with `RejectionReason.GeneratorRejected`, the generator is rejecting the shape — Branch A's `GeneratorRejected` row covers the remediation.

## See also

- [`../references/template-reference.md` § Complex template](../references/template-reference.md#complex-template-scalar-fields--repeating-collection) — full template JSON shape used in this example.
- [`../references/workflow.md`](../references/workflow.md) Steps 5–7 — this example exercises dry-run, execute, and the store lookup at the start.
- [`../references/scripts.md`](../references/scripts.md) — full flag list and output envelope for every script invoked here.

