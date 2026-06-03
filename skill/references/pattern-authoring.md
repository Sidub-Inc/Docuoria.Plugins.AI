# Pattern authoring techniques

Organised by failure mode, not by regex feature. Use this file when a pattern from `patterns.md` does not match the PDF's flattened haystack.

## Anchoring

The PDF haystack joins text blocks with a configurable separator (`PatternTestOptions.BlockSeparator`, default newline). `^` and `$` may or may not be per-line depending on `RegexOptions.Multiline`. Prefer `\b` (word boundary) and lookarounds over `^`/`$` unless the haystack guarantees one match per line. When you want the start of a logical block, anchor on a stable adjacent literal instead of relying on `^`.

## Named groups

The engine consumes capture groups via `PatternExtractionSource.PrimaryGroup` and `ProjectionGroups`. Prefer named groups — `(?<name>…)` — over numbered groups; they remain stable when the regex is edited. An unused group still costs match overhead, so make every group either named-and-projected or non-capturing `(?:…)`.

## Escaping

Characters that bite in PDF text: `.`, `$`, `(`, `)`, `[`, `]`, `*`, `+`, `?`, `|`, `\`. Currency symbols (`$`, `€`, `£`, `¥`) are literal in regex but may arrive as composed unicode; when you want any currency symbol, use `\p{Sc}` instead of enumerating them. When you want any whitespace including unicode breaks, use `\s` not the literal space character.

## Greediness

`.*` will swallow across line breaks if the haystack uses `\n` as a separator. Prefer `.*?` (lazy) or a negated class like `[^\n]*`. The classic failure is a greedy regex that "matches everything up to and including the next field's value" — the symptom is `PatternTestResult.Matches[0]` whose `Value` runs across multiple logical fields.

## Multi-line haystack pitfalls

The engine flattens the PDF using `PatternTestOptions.BlockSeparator` before regex evaluation. Words can break across lines because PDFs lay out by position, not by reading order. Techniques:

- Use `\s+` instead of a literal space between words that may break across blocks.
- Use `[\s\S]` instead of `.` when you intentionally want a multi-line span.
- If a known word splits across lines, write the regex against the haystack produced by `dotnet script scripts/inspect.csx`, not the visual PDF — what you see in a viewer is not what the engine matches against.

## Iteration loop

1. Start with the rough pattern from `patterns.md`.
2. Run `dotnet script scripts/test-pattern.csx -- <pdf> '<regex>'`. Read `PatternTestResult.Matches` and `PatternTestResult.Gaps`.
3. If `PatternTestResult.HasMatches` is `false`: relax greediness or escape less, OR confirm the haystack actually contains the expected text via `dotnet script scripts/inspect.csx`.
4. If matches are too many: tighten with lookarounds or `\b`.
5. If one of N groups fails: run `dotnet script scripts/test-groups.csx -- <pdf> '<regex>'` and inspect `PatternGroupTestResult.Groups[*].Pattern` and `MatchesIndependently`.
6. Commit only when `HasMatches` is `true` *and* the match count matches expectation.

## RegexOptions

`PatternTestOptions.Options` controls `IgnoreCase`, `Multiline`, `CultureInvariant`, etc.; the default is `RegexOptions.None`. Recommend `IgnoreCase` for case-noisy PDFs (e.g. labels that vary in capitalisation between layouts) and only enable `Multiline` if the regex deliberately relies on per-line `^`/`$` semantics.
