# Extraction-source decision tree

Pick the extraction source by the *shape* of the data on the page, not by the field name. The subtypes below are mutually exclusive per field; if more than one seems to fit, the lower bullet wins.

## `$kind` reference table

Every extraction source uses the `$kind` JSON discriminator to identify the SDK type. You **must** use these exact values in template JSON — any other value causes silent deserialization failure.

| `$kind` value | Mode / variant | Use case |
|---|---|---|
| `TextPatternExtractionSource` | `mode: "Token"` | Literal token match (one value) |
| `TextPatternExtractionSource` | `mode: "Pattern"` | Regex match (one value, first match) |
| `TextPatternExtractionSource` | `mode: "AllMatches"` | Regex match (all matches → collection) |
| `TextAnchorExtractionSource` | — | Value next to a spatially-anchored label |
| `TableCellExtractionSource` | — | Single cell by row/column coordinate |
| `TableRowsExtractionSource` | — | All data rows from a real PDF table |
| `MetadataFieldExtractionSource` | — | PDF metadata (Title, Author, etc.) |
| `FallbackExtractionSource` | — | Composite: try primary, fall back to fallback |

## The decision questions, in order

1. **Is there exactly one value on the page** (e.g. a reference number, a single date)? → use `TextPatternExtractionSource` with `mode: "Pattern"`. Why: it returns the first regex match against the flattened haystack and projects a single named capture group. In JSON: `{ "$kind": "TextPatternExtractionSource", "mode": "Pattern", "regexPattern": "..." }`.
2. **Is the value a *list of similar values* on one page** (e.g. repeating line items, multiple reference numbers)? → use `TextPatternExtractionSource` with `mode: "AllMatches"`. Why: it returns every regex match, preserving order, as collection rows. In JSON: `{ "$kind": "TextPatternExtractionSource", "mode": "AllMatches", "regexPattern": "..." }`.
3. **Is the value inside a *visually tabular* block** (rows and columns, bordered or unbordered)? → run `inspect.csx` and check `Tables[*].TotalRowCount`. If any table has `TotalRowCount > 1` with correct `RowPreviews`, use `TableRowsExtractionSource` for whole rows or `TableCellExtractionSource` for a single cell coordinate. Why: the engine detects both bordered (lattice) and unbordered (stream/spacing-based) tables via Tabula. Even without visible grid lines, consistent text spacing is often enough for detection.
4. **Does the value live *next to a label* whose position varies** (e.g. `Total: 123.45` floating between header and footer)? → use `TextAnchorExtractionSource`. Why: it anchors on the label text and reads the adjacent run, so layout drift between PDFs is absorbed by the anchor.
5. **Does the value's location vary by layout *version*** (same vendor, two PDF templates over time)? → wrap the primary source in a `FallbackExtractionSource` with `primary` and `fallback` properties. Why: the engine tries `primary` first and falls back to `fallback` if no value is produced.
6. **Is the value in the PDF metadata** (author, title, creation date)? → use `MetadataFieldExtractionSource`. Why: it reads from the PDF's metadata dictionary, not from page text.

## Anti-patterns

- Do not use `TextPatternExtractionSource` (mode `"Pattern"`) for repeating data; it will silently take only the first match. Use mode `"AllMatches"` instead.
- Do not assume `TableRowsExtractionSource` is unusable just because the PDF "looks tabular but isn't bordered." The engine uses both Tabula lattice (bordered) AND stream (unbordered, spacing-based) table detection. **Always run `inspect.csx` and check `Tables[*].TotalRowCount`** — if any table has `TotalRowCount > 1` with meaningful `RowPreviews`, `TableRowsExtractionSource` will work even without visible borders.
- If `inspect.csx` shows tables with only 1 row (header-only) or no tables at all for a visually tabular section, fall back to `TextPatternExtractionSource` with `mode: "AllMatches"`. Before writing the regex, read the actual `FlattenedText` from `inspect.csx` — it may differ significantly from the visual PDF layout due to column-grouped block ordering.
- Do not chain more than three `FallbackExtractionSource` layers; that is a sign the document is fundamentally different and warrants a separate `Template`.
- Do not use `PatternExtractionSource` or `AllMatchesExtractionSource` — these are conceptual names that do **not** exist in the SDK. The actual type is always `TextPatternExtractionSource` with a `mode` property.

## Layout variants and template splitting

If `DryRunSucceeded` returns an empty collection for a field that should have data and `inspect.csx` reveals a different page structure than the PDF used during authoring, you are facing a layout variant. The canonical splitting procedure (choosing discriminators, tightening the original `rootMatchRule`, validating mutual exclusivity with `evaluate-match.csx`) lives in [`classification.md` § Worked example: splitting Microsoft invoices](classification.md#worked-example-splitting-microsoft-invoices). Follow that procedure, then return here to confirm the `ExtractionSource` choice survives the split.

## After picking

Confirm the choice by running `dotnet script scripts/dry-run.csx -- <pdf> <template.json>` and inspecting the field's `ExtractionDiagnostics` trace — if the wrong source was picked, the trace shows which match path was attempted and why it produced no value. Then go to `workflow.md` Step 6.
