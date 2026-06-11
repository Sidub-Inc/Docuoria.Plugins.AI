---
name: docuoria
description: Extract structured data (CSV or JSON) from PDF documents with Docuoria, a template-driven extraction engine that runs entirely on the local machine. Use for any work with the Docuoria CLI scripts, template JSON, or the IDocuoriaEngine API â€” including authoring or validating a template, designing classification match rules, choosing an ExtractionSource, writing or debugging an extraction regex, diagnosing a FailedResult or RejectedResult, surveying a batch of PDFs, or confirming that PDF processing stays on-machine. Applies even when the user does not say "Docuoria".
license: MIT
---

# Docuoria

Docuoria turns PDFs into structured data (CSV or JSON). It classifies each document against a store of reusable **templates**, then runs a deterministic extraction pipeline. You drive it through `dotnet-script` CLI scripts in `scripts/`. The same PDF, template, and options always produce the same bytes â€” nothing is inferred by a model at runtime, and no PDF ever leaves the machine.

You work on behalf of an everyday business user who does not know â€” and does not want to know â€” anything about regex, templates, or the engine. Do the technical work silently and return the data they asked for.

## Requirements

- The **.NET 10 SDK** and the **`dotnet-script`** global tool (`dotnet tool install -g dotnet-script`).
- The SDK assembly (`Docuoria.dll`) is bundled under `assets/lib/`; `dotnet-script` resolves the remaining NuGet dependencies on first run.

## The two cardinal rules

Almost every failed run violates one of these. They override every other instinct in this skill.

1. **You MUST understand the WHOLE document before you plan, ask, or author.** Run `inspect.csx` with no `--page` and account for every page from 1 to `pageCount`. Business documents put totals on page 1 and the real line items, charges, or terms on later pages. If you have read only one page, you do not understand the document â€” **do not proceed.**

2. **You MUST ask the user at most one plain-language question, and only when you genuinely must.** Do every technical step (survey, classify, inspect, test, validate, dry-run, execute) silently â€” never ask the user about mechanics. Ask only when the document supports more than one reasonable result *and* the user has not already said what they want. When you ask, ask one outcome-focused question with 2â€“4 choices and a pre-selected **(Recommended)** default. The phrasing model is in [references/workflow.md](references/workflow.md).

## First, settle scope: one PDF or many?

Single-PDF and batch runs start differently. Decide before anything else:

- **Single-PDF** â€” one file, or the user pointed at one document. Start at **Classify** (Step 1).
- **Batch** â€” a folder, a list, or language like "all invoices" / "every statement". Start at **Survey** (Step 0): it reports each PDF's structural facts (existing-template matches, page count, shared tokens) so *you* can reason about how many templates the batch needs. Authoring one template per PDF when a batch shares one structure is a top failure.

If the scope is genuinely unclear, this is a valid moment for your one question.

## The workflow (run in order)

Classification decides where you enter. Full procedure, routing, and gates: [references/workflow.md](references/workflow.md).

0. **Survey** (batch only) â€” profile the corpus (matches, page counts, shared tokens); reason about how many templates it needs.
1. **Classify** â€” score the PDF against every stored template; route on the returned `recommendation`. Always first; never skip.
2. **Inspect** â€” read the engine's text from every page (when no template matches).
3. **Test** â€” prove each regex against the engine's flattened text.
4. **Build** â€” author the template JSON, then verify classification quality and schema.
5. **Dry-run** â€” extract end-to-end without producing output; confirm completeness.
6. **Execute** â€” full run producing CSV or JSON.
7. **Store** â€” persist the template and confirm it ranks correctly.

## Running scripts

Every script runs as `dotnet script scripts/<name>.csx -- --<flag> <value>` from the skill root. The `--` separator is mandatory; positional arguments are rejected. Pass `--help` to any script for its flags. Full flag, output, and exit-code reference: [references/scripts.md](references/scripts.md).

Store-aware scripts (`classify`, `evaluate-match`, `list-templates`, `load-template`, `save-template`, `survey`, `regression-check`, `batch-execute`) read a template store. The store MUST live in the **user's working directory** (e.g. `<project>/templates`) â€” NEVER inside the skill install directory (`.claude/skills/docuoria/...`). Scripts often run with the skill directory as cwd, so the bare default `./templates` would silently create the store inside the skill folder, where a skill update or reinstall wipes it. **Always pass an absolute `--store-path`** anchored at the user's project root.

| Script | Invocation |
| --- | --- |
| `inspect` | `inspect.csx -- --pdf <file>` |
| `test-pattern` | `test-pattern.csx -- --pdf <file> --pattern "<regex>"` |
| `test-groups` | `test-groups.csx -- --pdf <file> --pattern "<regex>"` |
| `validate-template` | `validate-template.csx -- --template <file>` |
| `dry-run` | `dry-run.csx -- --pdf <file> --template <file>` |
| `execute` | `execute.csx -- --pdf <file> --template <file> --format csv\|json` (add `--output <ledger> --append` for a recurring single-PDF export) |
| `batch-execute` | `batch-execute.csx -- --corpus <dir> --store-path <abs-store-dir> --output <merged.csv>` (classify-routes each PDF, merges all rows into one ledger; `--append` extends it idempotently on later runs; `--format json` for envelope output) |
| `regression-check` | `regression-check.csx -- --modified <file> --baseline <file> --corpus <dir>` |
| `classify` | `classify.csx -- --pdf <file> --store-path <abs-store-dir>` |
| `evaluate-match` | `evaluate-match.csx -- --pdf <file> --template <file> --store-path <abs-store-dir>` |
| `list-templates` | `list-templates.csx -- --store-path <abs-store-dir>` |
| `load-template` | `load-template.csx -- --id <id> --store-path <abs-store-dir>` |
| `save-template` | `save-template.csx -- --template <file> --store-path <abs-store-dir>` |
| `survey` | `survey.csx -- --corpus <dir> --store-path <abs-store-dir>` |
| `schema-info` | `schema-info.csx` (dumps every `$kind`, mode, and enum) |

## Template vocabulary (the menu)

The tables below are a quick menu for orientation. Before writing any template JSON you **MUST** still load the authoritative specs â€” [references/template-reference.md](references/template-reference.md) (shape + enums) and [references/extraction-sources.md](references/extraction-sources.md) (source selection). `schema-info.csx` prints the live discriminator set at any time.

**Extraction sources** â€” one row per `$kind`:

| `$kind` | Mode | Use for |
| --- | --- | --- |
| `TextPatternExtractionSource` | `Token` | A literal token (one value) |
| `TextPatternExtractionSource` | `Pattern` | A regex (one value, first match) |
| `TextPatternExtractionSource` | `AllMatches` | A regex over a repeating list (â†’ collection) |
| `TextAnchorExtractionSource` | â€” | A value next to a spatially-anchored label |
| `TableCellExtractionSource` | `Ordinal` / `ByHeader` | One cell by coordinate or header |
| `TableRowsExtractionSource` | `Ordinal` / `ByHeader` | All data rows of a detected table |
| `MetadataFieldExtractionSource` | `Standard` / `Raw` | PDF metadata (Title, Author, â€¦) |
| `FallbackExtractionSource` | â€” | Try a primary source, fall back to another |

**Discriminators** â€” any other value fails deserialization; `validate-template.csx` reports the valid set:

| Position | Valid `$kind` |
| --- | --- |
| `rootMatchRule` / composite child | `TextPatternMatchRule`, `FileNameMatchRule`, `TextAnchorMatchRule`, `MetadataMatchRule`, `PageGeometryMatchRule`, `TableMatchRule`, `CompositeMatchRule` |
| `requirements[*]` | `MinMatches`, `MinRows`, `RequiredFields`, `MustBeAbsent` |

**`fieldType` is an integer 0â€“5**, never a string: 0 String, 1 Number, 2 Integer, 3 Boolean, 4 Date, 5 Timestamp.

## References (load on demand)

Each concern has one authoritative owner. Consult it instead of relying on memory.

| When you need toâ€¦ | Load |
| --- | --- |
| Follow the pipeline step-by-step, or phrase the user question | [references/workflow.md](references/workflow.md) |
| **Author or change a template** â€” pick the `ExtractionSource` for a field | [references/extraction-sources.md](references/extraction-sources.md) |
| **Author or change a template** â€” look up a property, `$kind`, enum, or shape | [references/template-reference.md](references/template-reference.md) |
| Design a discriminating `rootMatchRule`, declare requirements, or resolve a partial match | [references/classification.md](references/classification.md) |
| Write, adapt, or debug a regex pattern | [references/patterns.md](references/patterns.md) |
| Diagnose any failure â€” a symptom, error code, `RejectedResult`, `FailedResult`, or incomplete run | [references/troubleshooting.md](references/troubleshooting.md) |
| Look up a script's flags, output envelope, or exit codes | [references/scripts.md](references/scripts.md) |
| Confirm PDF processing is local/private | [references/privacy.md](references/privacy.md) |

Worked end-to-end walkthroughs live in [examples/](examples/): extract to CSV, classify an unknown PDF, diagnose a `FailedResult`, survey a batch, and keep a recurring ledger across monthly runs.

## Gotchas

These are the mistakes that cause silent, hard-to-spot failures.

- **Never infer anything from a file name.** A name like `MICROSOFT_2025-05-09.pdf` is not evidence of a date format, document type, or any value. Ground every assumption in text the engine extracted; confirm with `test-pattern.csx`.
- **`DryRunSucceeded` is not "complete".** It means the pipeline ran. You **MUST** check `completeness.isComplete`, `completeness.missingRequiredFields`, and `completeness.emptyDeclaredCollections`; exit code 2 means "ran but incomplete" â€” a failure to fix, not a success.
- **Adapt every regex to the actual PDF.** The engine's flattened text differs from the visual layout (whitespace, line breaks, encoding). Validate with `test-pattern.csx`; never paste a library pattern verbatim.
- **Discriminators MUST be structural, not values.** Build `rootMatchRule` tokens from section headers, labels, and column names â€” never from extracted field values. `validate-template.csx` warns when a discriminator token overlaps an extraction pattern.
- **`MinRows: 1` disqualifies every header-only PDF of that type.** Only declare it when the document type cannot exist without that collection; otherwise use `MustBeAbsent` on the sibling template.
- **`requirements[*].mapping`/`collection`/`fields` reference declared mapping names**, not DataModel field names. `validate-template.csx` reports `TPL_FIELD_REFERENCE` for unknown references.
- **`classify.csx` hides templates with `requirementsSatisfied: false`.** If an expected template is missing from results, run `evaluate-match.csx` against it directly to see the per-requirement breakdown.
- **A collection row = the unit the user asked for, never the summary that totals it.** Count the repeating units in the haystack; your collection must match that many times (`workflow.md` § Row-granularity contract). Falling back to a one-row summary because a per-unit pattern attempt failed is a silent data loss — iterate on the pattern instead.
- **Default the output to CSV/spreadsheet** unless the user asked for JSON or the shape forces JSON (two or more repeating collections).
- **Recurring exports get `--append`, never a rebuild.** "Add this month's invoices" / "same spreadsheet as last time" → `--append` on `execute`/`batch-execute`: it creates the ledger in month 1, extends it in month N, and skips already-recorded PDFs (idempotent — sweeping the whole folder again is safe and cheap). Respect the safety refusals (`existing-ledger`, `would-drop-sources`, `not-a-ledger`): they mean you picked the wrong mode or path, not that you should force with `--overwrite`. Templates feeding one ledger MUST share a canonical field vocabulary (same meaning → same field name; `tax` vs `hst` splits the user's column). Details: `workflow.md` § Recurring exports.

## Layout

- `SKILL.md` â€” this router, loaded at activation.
- `references/` â€” authoritative guides loaded on demand (see References table).
- `examples/` â€” end-to-end walkthroughs.
- `scripts/` â€” the `dotnet-script` CLI surface (`_common.csx` plus the verb scripts).
- `assets/lib/Docuoria.dll` â€” bundled SDK assembly.
- `assets/schemas/template-schema.json` â€” JSON Schema for template validation.

This directory is scaffolded and updated by the Docuoria CLI (`docuoria init` / `docuoria update`); see `docs/cli.md` in the Docuoria repository for the command reference.

