# Example 3 — Diagnose a FailedResult

## Scenario

`dotnet script scripts/execute.csx -- --pdf <pdf> --template <template.json> --format csv --output out.csv` exits non-zero and emits `{ "error": { "code": "failed", "message": "FieldPath ... could not be coerced to System.Decimal", "detail": "..." } }` on stderr. The underlying `FailedResult` has `step` 3 (Transformation) — a field expected to be a `Number` could not be coerced from the captured text.

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

1. **Map the stderr `error.code` to a branch first.** `execute.csx` emitted `{ "error": { "code": "failed", "message": "...", "detail": "..." } }` on stderr with exit ≥ 1. Look up `failed` in [`../references/troubleshooting.md` § Error-code routing](../references/troubleshooting.md#error-code-routing) → [Branch B](../references/troubleshooting.md#branch-b--failedresult), then read the `step` integer in `detail` (`3` — Transformation) to land on the right remediation row.
2. Read the structured fields on `FailedResult` (parse from stderr `detail` or re-run dry-run with diagnostics):
   - `step` — here `3` (Transformation).
   - `fieldPath` — e.g. `"totalAmount"`.
   - `sourceText` — the raw captured substring, capped at 256 characters with a trailing ellipsis when truncated.
   - `targetTypeName` — e.g. `"Number"` / the engine-friendly name.
   - `innerDetail` — short description of the inner exception.
3. Confirm with diagnostics: `dotnet script scripts/dry-run.csx -- --pdf <pdf> --template <template.json>`. Dry-run defaults diagnostics on. Read the same fields off the returned `DryRunFailed` and inspect the per-mapping match trace.
4. Inspect `sourceText`. If it contains a currency symbol, a thousands separator, or surrounding whitespace, the pattern is capturing too much for the target type — tighten the pattern (see [`../references/patterns.md`](../references/patterns.md) patterns 3 vs 4, or pattern 6 for decimals) or change the target type to `String` (0) with a downstream transform.
5. Re-run `dotnet script scripts/dry-run.csx -- --pdf <pdf> --template <template.json>` until `DryRunSucceeded`.
6. Re-run `dotnet script scripts/execute.csx -- --pdf <pdf> --template <template.json> --format csv --output output.csv` for the real CSV.

## Expected outcome

`ProcessingResult` becomes `SucceededResult`; the previously-failing field carries the coerced value.

## See also

- [`../references/troubleshooting.md` § Error-code routing](../references/troubleshooting.md#error-code-routing) — always start here when a script exits non-zero.
- [`../references/troubleshooting.md` § Branch B — FailedResult](../references/troubleshooting.md#branch-b--failedresult) — the canonical step-by-step remediation, including step 3 (Transformation/coercion).
- [`../references/patterns.md`](../references/patterns.md) — illustrative patterns, especially 3–7 for numeric coercion, and the Authoring techniques section for shrinking an over-broad capture.
