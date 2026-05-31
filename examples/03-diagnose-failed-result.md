# Example 3 — Diagnose a FailedResult

## Scenario

`dotnet script scripts/execute.csx -- --pdf <pdf> --template <template.json> --format csv --output out.csv` exits non-zero and emits `{ "error": { "code": "failed", "message": "FieldPath ... could not be coerced to System.Decimal", "detail": "..." } }` on stderr. The underlying `FailedResult` has `Step = StepIdentifier.Transformation` — a field expected to be a `decimal` could not be coerced from the captured text.

## Example: before (broken template)

The template maps a currency amount as `fieldType: 1` (Number), but the regex captures the dollar sign:

```json
{
  "$kind": "FieldMapping",
  "fieldName": "totalAmount",
  "fieldType": 1,
  "source": {
    "$kind": "TextPatternExtractionSource",
    "mode": "Pattern",
    "regexPattern": "Total:?\\s*(?<value>\\$[\\d,.]+)"
  }
}
```

This captures `"$1,234.56"`, which cannot be parsed as a decimal → `FailedResult`.

## Example: after (fixed template)

Exclude the dollar sign from the capture group:

```json
{
  "$kind": "FieldMapping",
  "fieldName": "totalAmount",
  "fieldType": 1,
  "source": {
    "$kind": "TextPatternExtractionSource",
    "mode": "Pattern",
    "regexPattern": "Total:?\\s*\\$?(?<value>[\\d,]+\\.\\d{2})"
  }
}
```

Now captures `"1,234.56"` → coerces to `1234.56` successfully.

## Steps

1. **Map the stderr `error.code` to a branch first.** `execute.csx` emitted `{ "error": { "code": "failed", "message": "...", "detail": "Step: Transformation, FieldPath: ..." } }` on stderr with exit ≥ 1. Look up `failed` in [`../references/failure-tree.md` § Stderr error.code → Branch routing](../references/failure-tree.md#stderr-errorcode--branch-routing) → **Branch B**, then read the `StepIdentifier` in `detail` (`Transformation`) to land on the right remediation row.
2. Read the structured fields on `FailedResult` (parse from stderr `detail` or re-run dry-run with diagnostics):
   - `Step` — here `StepIdentifier.Transformation`.
   - `FieldPath` — e.g. `"DataModel.Fields.Total"`.
   - `SourceText` — the raw captured substring, capped at 256 characters with a trailing ellipsis when truncated.
   - `TargetTypeName` — e.g. `"System.Decimal"` or the engine-friendly name.
   - `InnerDetail` — short description of the inner exception.
3. Confirm with diagnostics: `dotnet script scripts/dry-run.csx -- --pdf <pdf> --template <template.json>`. Dry-run defaults `Diagnostics = true`. Read the same fields off the returned `DryRunFailed` and inspect the per-mapping match trace.
4. Inspect `SourceText`. If it contains a currency symbol, a thousands separator, or surrounding whitespace, the pattern is capturing too much for the target type — tighten the pattern (see [`../references/patterns.md`](../references/patterns.md) patterns 3 vs. 4, or pattern 6 for decimals) or change the target type to `string` with a downstream transform.
5. Re-run `dotnet script scripts/dry-run.csx -- --pdf <pdf> --template <template.json>` until `DryRunSucceeded`.
6. Re-run `dotnet script scripts/execute.csx -- --pdf <pdf> --template <template.json> --format csv --output output.csv` for the real CSV.

## Expected outcome

`ProcessingResult` becomes `SucceededResult`; the previously-failing field carries the coerced value.

## See also

- [`../references/failure-tree.md` § Stderr error.code → Branch routing](../references/failure-tree.md#stderr-errorcode--branch-routing) — always start here when a script exits non-zero.
- [`../references/failure-tree.md`](../references/failure-tree.md) Branch B `Step = Transformation` — the canonical remediation matrix.
- [`../references/patterns.md`](../references/patterns.md) — illustrative patterns, especially 3–7 for numeric coercion.
- [`../references/pattern-authoring.md`](../references/pattern-authoring.md) — the iteration loop for shrinking an over-broad capture.
