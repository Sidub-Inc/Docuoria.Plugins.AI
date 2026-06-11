# Patterns

The patterns below are *illustrative*. They demonstrate authoring techniques, not copy-paste solutions. Real PDFs have layout quirks (broken words, unicode whitespace, OCR drift) that mean a pattern matching the visible text will often miss the engine's flattened text (the **haystack**). Before using any pattern below, you **MUST** run `dotnet script scripts/test-pattern.csx -- --pdf <pdf> --pattern '<regex>'` and adapt to what `PatternTestResult.Matches` and `PatternTestResult.Gaps` actually report. The **Authoring techniques** section at the end covers what to do when a pattern does not match.

## Contents

- [Pattern library](#1-iso-8601-date) — 11 illustrative patterns (date, postal code, currency, integer, decimal, percentage, email, phone, URL, UUID)
- [How to verify a pattern](#how-to-verify-a-pattern)
- [Authoring techniques](#authoring-techniques) — what to do when a pattern does not match:
  - [Anchoring](#anchoring) · [Named groups](#named-groups) · [Escaping](#escaping) · [Greediness](#greediness) · [Multi-line haystack pitfalls](#multi-line-haystack-pitfalls) · [Backtracking complexity](#backtracking-complexity) · [Iteration loop](#iteration-loop) · [RegexOptions](#regexoptions)
  - [Never infer a format from a file name](#never-infer-a-format-from-a-file-name)
  - [Never derive discriminators from extracted values](#never-derive-discriminators-from-extracted-values)

## Pattern library

### 1. ISO 8601 date

- regex: `\b\d{4}-(0[1-9]|1[0-2])-(0[1-9]|[12]\d|3[01])\b`
- Matches: `2024-01-15`, `1999-12-31`.
- Non-matches: `2024-13-01`, `24-01-15`, `2024/01/15`.
- Field type: `Date` (4).
- Teaches: alternation inside character classes for valid month/day ranges.

### 2. US ZIP / CA postal code (combined)

- regex: `\b(?:\d{5}(?:-\d{4})?|[A-Z]\d[A-Z] ?\d[A-Z]\d)\b`
- Matches: `90210`, `90210-1234`, `K1A 0B1`, `K1A0B1`.
- Non-matches: `1234`, `9021O` (letter O instead of zero).
- Field type: `String` (0).
- Teaches: non-capturing groups and optional whitespace with `?`.

### 3. Currency with symbol

- regex: `(?<currency>[$€£¥])\s?(?<amount>\d{1,3}(?:,\d{3})*(?:\.\d{2})?)`
- Matches: `$1,234.56`, `€ 99.00`, `£10`.
- Non-matches: `1234.56` (no symbol), `$1.234,56` (European grouping).
- Field type: `Number` (1).
- Teaches: named groups (projected downstream by a `NamedGroupSubFieldMapping.groupName`).

### 4. Currency, symbol-stripped numeric only

- regex: `(?<![\$€£¥\d])-?\d{1,3}(?:,\d{3})*\.\d{2}(?![\d])`
- Matches: `1,234.56`, `-99.00`.
- Non-matches: `1234` (no decimal), `1,234.5` (one fraction digit).
- Field type: `Number` (1).
- Teaches: lookbehind/lookahead to reject embedded matches.

### 5. Integer (standalone)

- regex: `(?<![\d.])\d+(?![\d.])`
- Matches: `42`, `1000`.
- Non-matches: `3.14`, `12345.67`.
- Field type: `Integer` (2).
- Teaches: negative lookarounds for "not part of a bigger token".

### 6. Decimal (any precision)

- regex: `(?<![\d.])-?\d+\.\d+(?![\d.])`
- Matches: `3.14`, `-0.001`.
- Non-matches: `3.`, `.5`, `3.14.15`.
- Field type: `Number` (1).
- Teaches: balanced lookarounds preventing partial overlap.

### 7. Percentage

- regex: `(?<value>-?\d+(?:\.\d+)?)\s?%`
- Matches: `50%`, `12.5 %`, `-3%`.
- Non-matches: `% 50`, `fifty percent`.
- Field type: `Number` (1).
- Teaches: optional whitespace plus capture before a literal suffix.

### 8. Email (RFC-pragmatic)

- regex: `\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b`
- Matches: `a@b.co`, `first.last+tag@example.com`.
- Non-matches: `a@b`, `@example.com`.
- Field type: `String` (0).
- Teaches: word-boundary anchoring and the pragmatic-vs-RFC-strict tradeoff.

### 9. Phone E.164

- regex: `\+[1-9]\d{1,14}\b`
- Matches: `+14165551212`, `+442071838750`.
- Non-matches: `416-555-1212`, `+0123` (leading zero after `+`).
- Field type: `String` (0).
- Teaches: format normalisation upstream of extraction (the engine expects already-cleaned input).

### 10. URL (http/https)

- regex: `https?://[^\s<>"']+`
- Matches: `https://example.com/path?q=1`, `http://a.b`.
- Non-matches: `ftp://x`, `example.com`.
- Field type: `String` (0).
- Teaches: negated character class for "until whitespace or quote".

### 11. UUID v1–v5

- regex: `\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[1-5][0-9a-fA-F]{3}-[89abAB][0-9a-fA-F]{3}-[0-9a-fA-F]{12}\b`
- Matches: `550e8400-e29b-41d4-a716-446655440000`.
- Non-matches: `not-a-uuid`, all-zeros (fails the variant nibble).
- Field type: `String` (0).
- Teaches: position-specific character classes to validate format.

## How to verify a pattern

- Run `dotnet script scripts/test-pattern.csx -- --pdf <pdf> --pattern "<regex>"`. If `PatternTestResult.HasMatches` is `false`, the pattern does not match this PDF's haystack — use the techniques below.
- If matches are partial, run `dotnet script scripts/test-groups.csx -- --pdf <pdf> --pattern "<regex>"` and read `PatternGroupTestResult.Groups[*].MatchesIndependently` to find the failing group.
- If matches are too many, tighten with lookarounds (patterns 4–6 demonstrate the technique).

---

# Authoring techniques

Use this section when a pattern above does not match the engine's flattened haystack. It is organised by failure mode, not by regex feature.

## Anchoring

The haystack joins text blocks with a configurable separator (`PatternTestOptions.BlockSeparator`, default newline). `^` and `$` may or may not be per-line depending on `RegexOptions.Multiline`. Prefer `\b` (word boundary) and lookarounds over `^`/`$` unless the haystack guarantees one match per line. To anchor on the start of a logical block, key off a stable adjacent literal instead of `^`.

**Label-substring collisions (case-insensitive default):** extraction matching is
case-INSENSITIVE unless the source sets `caseSensitive: true`. A label anchor that is
a substring of a sibling label *modulo case* silently matches the wrong field —
e.g. `Total:` matches inside `SOUS-TOTAL/SUB-TOTAL:` on bilingual invoices, and the
error is invisible whenever the two values coincide (tax = 0 → subtotal == total).
Before trusting a label anchor, search the haystack for the label as a substring of
other labels; if it collides, set `caseSensitive: true` and/or anchor on the
preceding newline (`(?<=\n)Total:`). The arithmetic cross-check in `workflow.md`
Step 5 is the safety net that exposes this class of error.

## Named groups

The engine projects capture groups into element fields via sub-field mappings (`NamedGroupSubFieldMapping.groupName` and `RegexGroupSubFieldMapping.groupIndex`). Prefer named groups — `(?<name>…)` — over numbered groups; they stay stable when the regex is edited. An unused group still costs match overhead, so make every group either named-and-projected or non-capturing `(?:…)`.

## Escaping

Characters that bite in PDF text: `.`, `$`, `(`, `)`, `[`, `]`, `*`, `+`, `?`, `|`, `\`. Currency symbols (`$`, `€`, `£`, `¥`) are literal in regex but may arrive as composed unicode; to match any currency symbol use `\p{Sc}`. To match any whitespace including unicode breaks, use `\s`, not a literal space.

## Greediness

`.*` will swallow across line breaks when the haystack uses `\n` as a separator. Prefer `.*?` (lazy) or a negated class like `[^\n]*`. The classic failure is a greedy regex that matches "everything up to and including the next field's value" — the symptom is a `Matches[0].Value` that runs across multiple logical fields.

## Multi-line haystack pitfalls

The engine flattens the PDF before regex evaluation, and words can break across lines because PDFs lay out by position, not reading order.

- Use `\s+` instead of a literal space between words that may break across blocks.
- Use `[\s\S]` instead of `.` when you intentionally want a multi-line span.
- If a word splits across lines, write the regex against the haystack from `inspect.csx`, not the visual PDF.

## Column-grouped layouts (zipping parallel runs)

Some visually tabular sections flatten **column-major**: the haystack lists all the
row labels first (names, dates), then all the numeric tuples — instead of
label-value-label-value. A plain contiguous regex cannot capture one row, because a
row's fields are not adjacent in the haystack.

Recognize it: `inspect.csx` shows a block containing N labels in a run, followed by
a block of N (or N×k) values; your per-row pattern matches once (or zero times)
where the page visibly shows N rows.

Options, in preference order:

1. **`TableRowsExtractionSource`** — check `pages[*].tables[*]` first; Tabula's
   stream detection often reassembles such sections into proper rows even without
   borders. If `totalRowCount` ≈ N with sane `rowPreviews`, use it and skip regex.
2. **Lookahead zip (regex)** — match the unit = the Nth label, and zip in the Nth
   value tuple with a counting lookahead using .NET balancing groups: for each
   *later* label push `(?<R>)`, then in the lookahead skip that many value tuples
   (`(?<-R>…)*`), require `(?(R)(?!))`, and capture the next tuple. This pairs
   label i with tuple i positionally. Verify the match count equals the visible row
   count (row-granularity contract) and cross-check one row's values by eye —
   positional zips silently misalign when a value tuple is missing.
3. **Roll up + disclose** — only when the haystack provably lacks per-row values
   (cite the blocks), per the row-granularity contract in `workflow.md`.

## Backtracking complexity

A pattern that nests ambiguous quantifiers — two or more quantified subexpressions
that can consume the **same** characters, e.g. `(?:[^\n]*X[^\n]*)*` or `(a+)+` —
backtracks exponentially when the overall match fails: the engine retries every way
of splitting the text between the overlapping quantifiers before giving up.
`test-pattern.csx` / `test-groups.csx` bound this with a **5-second match timeout**
(override with `--timeout-ms`); on timeout they exit `1` with the
`pattern-timeout` error code. A `pattern-timeout` is a pattern bug, not a
load problem — do not just raise the limit. Make the pattern deterministic:

- **Avoid ambiguous quantifier nesting.** `(?:[^\n]*X[^\n]*)*` lets the two `[^\n]*`
  runs trade characters; prefer a per-line deterministic form such as
  `(?=[^\n]*X)[^\n]+` (assert the line contains `X`, then consume the line once).
- **Bound repetition.** Replace open-ended `*`/`+` on broad classes with explicit
  counts (`{1,80}`) sized to the field.
- **Anchor match starts.** Key the pattern off a stable literal or `\n` boundary so
  the engine does not retry the match from every character position.

## Iteration loop

1. Start with the rough pattern from the library above.
2. Run `test-pattern.csx` and read `Matches` and `Gaps`.
3. If `HasMatches` is `false`: relax greediness or escape less, OR confirm via `inspect.csx` that the haystack actually contains the expected text. On a valid zero-match probe, read `nearMiss.breakIndex` to see exactly where the pattern broke (see [`troubleshooting.md`](troubleshooting.md)).
4. If matches are too many: tighten with lookarounds or `\b`.
5. If one of N groups fails: run `test-groups.csx` and inspect `groups[*].subPattern` and `groups[*].matchesIndependently`.
6. Commit only when `HasMatches` is `true` and the match count meets expectation.

## RegexOptions

`PatternTestOptions.Options` controls `IgnoreCase`, `Multiline`, `CultureInvariant`, etc.; the default is `RegexOptions.None`. Use `IgnoreCase` for case-noisy PDFs; enable `Multiline` only when the regex deliberately relies on per-line `^`/`$`.

## Never infer a format from a file name

If a file is named `MICROSOFT_2025-05-09.pdf`, the date `2025-05-09` is in the **file name**, not necessarily in the document body in that format. Run `inspect.csx`, then `test-pattern.csx` against the haystack, to confirm the real format before writing a date or number pattern. File names are metadata; a pattern built from a file-name date may silently fail on the next variant.

## Never derive discriminators from extracted values

A discriminator token in `rootMatchRule` must identify the **document format** (section headers, product names, structural markers), not a value the template extracts (dates, invoice numbers, amounts). If your discriminator is a character class like `[0-9]{2}/[0-9]{2}` because the date field has a two-digit day, then every document with a date in that format matches, and changing the incoming date format breaks classification silently.

`validate-template.csx` emits a `TPL_DISCRIMINATOR_FROM_VALUE` warning (Severity: Warning) when a `rootMatchRule` token has high character-trigram similarity (Jaccard ≥ 0.5) to a `TextPatternExtractionSource` pattern on the same template. It does not block loading, but it signals a design problem.

**Fix:** replace value-derived tokens with structural tokens — section headers (`"Invoice Summary"`), product names (`"Microsoft 365"`), or column names (`"Unit Price"`, `"License Qty"`). These are stable across date formats and billing cycles.
