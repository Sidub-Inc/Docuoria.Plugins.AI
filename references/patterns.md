# Patterns

The patterns below are *illustrative*. They demonstrate authoring techniques, not copy-paste solutions. Real PDFs have layout quirks (broken words, unicode whitespace, OCR drift) that mean a pattern that matches the visible text will often miss the engine's flattened haystack. Before using any pattern below, run `dotnet script scripts/test-pattern.csx -- <pdf> '<regex>'` and adapt to what `PatternTestResult.Matches` and `PatternTestResult.Gaps` actually report. See `pattern-authoring.md` for techniques.

### 1. ISO 8601 date

- regex: `\b\d{4}-(0[1-9]|1[0-2])-(0[1-9]|[12]\d|3[01])\b`
- Matches: `2024-01-15`, `1999-12-31`.
- Non-matches: `2024-13-01`, `24-01-15`, `2024/01/15`.
- Field type: `DateOnly`.
- Teaches: alternation inside character classes for valid month/day ranges.

### 2. US ZIP / CA postal code (combined)

- regex: `\b(?:\d{5}(?:-\d{4})?|[A-Z]\d[A-Z] ?\d[A-Z]\d)\b`
- Matches: `90210`, `90210-1234`, `K1A 0B1`, `K1A0B1`.
- Non-matches: `1234`, `9021O` (letter O instead of zero).
- Field type: `string`.
- Teaches: non-capturing groups and optional whitespace with `?`.

### 3. Currency with symbol

- regex: `(?<currency>[$€£¥])\s?(?<amount>\d{1,3}(?:,\d{3})*(?:\.\d{2})?)`
- Matches: `$1,234.56`, `€ 99.00`, `£10`.
- Non-matches: `1234.56` (no symbol), `$1.234,56` (European grouping).
- Field type: `decimal`.
- Teaches: named groups (consumed downstream by `PatternExtractionSource.PrimaryGroup`).

### 4. Currency, symbol-stripped numeric only

- regex: `(?<![\$€£¥\d])-?\d{1,3}(?:,\d{3})*\.\d{2}(?![\d])`
- Matches: `1,234.56`, `-99.00`.
- Non-matches: `1234` (no decimal), `1,234.5` (one fraction digit).
- Field type: `decimal`.
- Teaches: lookbehind/lookahead to reject embedded matches.

### 5. Integer (standalone)

- regex: `(?<![\d.])\d+(?![\d.])`
- Matches: `42`, `1000`.
- Non-matches: `3.14`, `12345.67`.
- Field type: `int`.
- Teaches: negative lookarounds for "not part of a bigger token".

### 6. Decimal (any precision)

- regex: `(?<![\d.])-?\d+\.\d+(?![\d.])`
- Matches: `3.14`, `-0.001`.
- Non-matches: `3.`, `.5`, `3.14.15`.
- Field type: `decimal`.
- Teaches: balanced lookarounds preventing partial overlap.

### 7. Percentage

- regex: `(?<value>-?\d+(?:\.\d+)?)\s?%`
- Matches: `50%`, `12.5 %`, `-3%`.
- Non-matches: `% 50`, `fifty percent`.
- Field type: `decimal`.
- Teaches: optional whitespace plus capture before a literal suffix.

### 8. Email (RFC-pragmatic)

- regex: `\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b`
- Matches: `a@b.co`, `first.last+tag@example.com`.
- Non-matches: `a@b`, `@example.com`.
- Field type: `string`.
- Teaches: word-boundary anchoring and the pragmatic-vs-RFC-strict tradeoff.

### 9. Phone E.164

- regex: `\+[1-9]\d{1,14}\b`
- Matches: `+14165551212`, `+442071838750`.
- Non-matches: `416-555-1212`, `+0123` (leading zero after `+`).
- Field type: `string`.
- Teaches: format normalisation upstream of extraction (the engine expects already-cleaned input).

### 10. URL (http/https)

- regex: `https?://[^\s<>"']+`
- Matches: `https://example.com/path?q=1`, `http://a.b`.
- Non-matches: `ftp://x`, `example.com`.
- Field type: `string`.
- Teaches: negated character class for "until whitespace or quote".

### 11. UUID v1–v5

- regex: `\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[1-5][0-9a-fA-F]{3}-[89abAB][0-9a-fA-F]{3}-[0-9a-fA-F]{12}\b`
- Matches: `550e8400-e29b-41d4-a716-446655440000`.
- Non-matches: `not-a-uuid`, all-zeros (fails the variant nibble).
- Field type: `Guid`.
- Teaches: position-specific character classes to validate format.

## How to verify a pattern

- Run `dotnet script scripts/test-pattern.csx -- <pdf> '<regex>'`. If `PatternTestResult.HasMatches` is `false`, the pattern does not match this PDF's haystack — go to `pattern-authoring.md`.
- If matches are partial, run `dotnet script scripts/test-groups.csx -- <pdf> '<regex>'` and read `PatternGroupTestResult.Groups[*].MatchesIndependently` to find the failing group.
- If matches are too many, tighten with lookarounds (patterns 4–6 demonstrate the technique).
