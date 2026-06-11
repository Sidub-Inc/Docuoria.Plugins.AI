# Choosing an extraction source

Pick the extraction source by the *shape* of the data on the page, not by the field name. The subtypes below are mutually exclusive per field; if more than one seems to fit, the lower bullet wins.

**Serialization note:** an extraction source `mode` is a **string** (`"mode": "Pattern"`), unlike match-rule `mode` and `fieldType`, which are **integers**. Use the exact string values shown below. `schema-info.csx` prints the live set.

## Contents

- [`$kind` reference table](#kind-reference-table)
- [The decision questions, in order](#the-decision-questions-in-order) — pick the source by data shape
- [Anti-patterns](#anti-patterns)
- [Layout variants and template splitting](#layout-variants-and-template-splitting)
- [Declaring requirements](#declaring-requirements) — the kind selector (rationale lives in classification.md)
- [After picking](#after-picking)

## `$kind` reference table

Every extraction source uses the `$kind` JSON discriminator to identify the SDK type. You **MUST** use these exact values in template JSON — any other value causes silent deserialization failure.

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
3. **Is the value inside a *visually tabular* block** (rows and columns, bordered or unbordered)? → run `inspect.csx` and check `pages[*].tables[*].totalRowCount`. If any table has `totalRowCount > 1` with correct `rowPreviews`, use `TableRowsExtractionSource` for whole rows or `TableCellExtractionSource` for a single cell coordinate. Why: the engine detects both bordered (lattice) and unbordered (stream/spacing-based) tables via Tabula. Even without visible grid lines, consistent text spacing is often enough for detection.
4. **Does the value live *next to a label* whose position varies** (e.g. `Total: 123.45` floating between header and footer)? → use `TextAnchorExtractionSource`. Why: it anchors on the label text and reads the adjacent run, so layout drift between PDFs is absorbed by the anchor.
5. **Does the value's location vary by layout *version*** (same vendor, two PDF templates over time)? → wrap the primary source in a `FallbackExtractionSource` with `primary` and `fallback` properties. Why: the engine tries `primary` first and falls back to `fallback` if no value is produced.
6. **Is the value in the PDF metadata** (author, title, creation date)? → use `MetadataFieldExtractionSource`. Why: it reads from the PDF's metadata dictionary, not from page text.

## Anti-patterns

- Do not use `TextPatternExtractionSource` (mode `"Pattern"`) for repeating data; it will silently take only the first match. Use mode `"AllMatches"` instead.
- Do not assume `TableRowsExtractionSource` is unusable just because the PDF "looks tabular but isn't bordered." The engine uses both Tabula lattice (bordered) AND stream (unbordered, spacing-based) table detection. **Always run `inspect.csx` and check `pages[*].tables[*].totalRowCount`** — if any table has `totalRowCount > 1` with meaningful `rowPreviews`, `TableRowsExtractionSource` will work even without visible borders.
- If `inspect.csx` shows tables with only 1 row (header-only) or no tables at all for a visually tabular section, fall back to `TextPatternExtractionSource` with `mode: "AllMatches"`. Before writing the regex, read the actual `flattenedText` from `inspect.csx` — it may differ significantly from the visual PDF layout due to column-grouped block ordering.
- Do not chain more than three `FallbackExtractionSource` layers; that is a sign the document is fundamentally different and warrants a separate `Template`.
- Do not use `PatternExtractionSource` or `AllMatchesExtractionSource` — these are conceptual names that do **not** exist in the SDK. The actual type is always `TextPatternExtractionSource` with a `mode` property.
- Do not split one repeating row shape into **two parallel collections** that must be
  zipped back together (e.g. `categories` + `amounts` captured by separate patterns).
  The CSV generator rejects multi-collection templates
  (`MULTIPLE_COLLECTIONS_UNSUPPORTED`), `batch-execute.csx` cannot merge them, and
  index-zipping silently misaligns when one pattern misses a row. Write ONE
  `RepeatingFieldMapping` whose pattern spans the full repeating unit — use optional
  non-capturing groups (`(?:…)?`) for segments that some rows omit. Reserve multiple
  collections for genuinely separate lists destined for JSON output.

## Layout variants and template splitting

If `DryRunSucceeded` returns an empty collection for a field that should have data and `inspect.csx` reveals a different page structure than the PDF used during authoring, you are facing a layout variant. The canonical splitting procedure (choosing discriminators, tightening the original `rootMatchRule`, validating mutual exclusivity with `evaluate-match.csx`) lives in [`classification.md` § Worked example: splitting Microsoft invoices](classification.md#worked-example-splitting-microsoft-invoices-with-requirements). Follow that procedure, then return here to confirm the `ExtractionSource` choice survives the split.

## Declaring requirements

After picking your extraction source, consider whether the template should declare **requirements** — structural constraints the engine evaluates after extraction to enforce template exclusivity. Use them only when two sibling templates would otherwise both match the same PDF. This section is the *kind selector*; for the rationale, the worked example, and when NOT to use requirements, [`classification.md` § Declaring requirements](classification.md#declaring-requirements) is the canonical owner.

### Decision: do I need requirements?

1. **Does exactly one template from my store match this document type?**
   - Yes → no requirements needed; match rules are sufficient.
   - No → read on.

2. **Can I find tokens in the PDF that are unique to this document type?**
   - Yes → add those tokens to the `rootMatchRule` and retest with `evaluate-match.csx`. If `isMatch` is now correct for both templates → done.
   - No (both templates share all tokens) → proceed to requirements.

3. **Does the document type always contain a specific collection?**
   - Yes (e.g. detailed invoices always have line items) → use `MinRows` on that collection.
   - No (e.g. header-only invoices must NOT have that collection) → use `MustBeAbsent` on the mapping.

4. **Does the document type require specific scalar fields to be present?**
   - Yes → use `RequiredFields` listing the mapping names that must each return ≥ 1 match.

5. **Does the document type require a minimum number of pattern matches in a mapping?**
   - Yes → use `MinMatches` with the expected minimum count.

### Requirement kind selector

| Scenario | Use |
|---|---|
| Sibling A has a repeating collection; sibling B must not have it | `MustBeAbsent` on the collection mapping in template B |
| Sibling A requires ≥ 1 collection row | `MinRows` with `count: 1` in template A |
| Both templates have the collection but template A requires ≥ 5 rows | `MinRows` with `count: 5` in template A |
| A scalar field that must match for this template to be valid | `RequiredFields` listing the mapping name |
| A pattern that must match ≥ N times (not a collection) | `MinMatches` with the mapping name and count |

### Placement in template JSON

Requirements live inside `extractionStep.requirements[]`:

```json
{
  "extractionStep": {
    "mappings": [ ... ],
    "requirements": [
      { "$kind": "MinRows", "collection": "lineItems", "count": 1 },
      { "$kind": "MustBeAbsent", "mapping": "creditNoteRef" }
    ]
  }
}
```

Each entry uses the `$kind` discriminator: `"MinMatches"`, `"MinRows"`, `"RequiredFields"`, `"MustBeAbsent"`. The `mapping` / `collection` / `fields` values must match declared mapping names in the same `extractionStep.mappings[]` — `validate-template.csx` enforces this.

After declaring requirements, verify with `evaluate-match.csx`:
```powershell
dotnet script scripts/evaluate-match.csx -- --pdf <pdf> --template <template.json>
# check: requirementsSatisfied, requirements[*].satisfied, requirements[*].detail
```

## After picking

Confirm the choice by running `dotnet script scripts/dry-run.csx -- --pdf <pdf> --template <template.json>` and inspecting the field's diagnostics trace — if the wrong source was picked, the trace shows which match path was attempted and why it produced no value. Then go to [`workflow.md`](workflow.md) Step 5.
