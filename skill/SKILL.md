---
name: docuoria
description: Use this skill when working with Docuoria to extract structured data from PDFs, author or validate a template, design match rules for classification, diagnose a FailedResult or RejectedResult, select an ExtractionSource type, write or debug a regex pattern, or verify that PDF processing is local and private. Apply even when the user does not say "Docuoria" — any task involving the Docuoria CLI scripts, template JSON, or the IDocuoriaEngine API qualifies.
license: MIT
compatibility: Requires .NET 10 SDK and the `dotnet-script` global tool. SDK assembly (`Docuoria.dll`) is bundled under `assets/lib/`; transitive NuGet dependencies (PdfPig, Tabula, CsvHelper, pythonnet, Microsoft.Extensions.*) are resolved by `dotnet-script` at first run.
---

# Docuoria Skill

## Installing this skill

This skill directory was scaffolded by the Docuoria CLI. To install or update:

```bash
# npm (Node.js ≥ 20)
npm install -g @sidub/docuoria
docuoria init

# .NET global tool
dotnet tool install -g Docuoria.Cli
docuoria init

# Update an existing installation
docuoria update

# Check status / drift
docuoria list-tools
docuoria doctor
```

See `docs/cli.md` in the Docuoria repository for the full command reference.

---

## Invocation

All scripts follow `dotnet script scripts/<name>.csx -- --<flag> <value>`, run from the skill root. The `--` separator is mandatory — without it, dotnet-script consumes the flags as its own. Positional arguments are rejected; pass `--help` to any script for its full flag list.

Scripts divide into two groups:

| Group | Scripts | Store flag |
| --- | --- | --- |
| **Store-aware** — read from or write to a template store | `classify`, `evaluate-match`, `list-templates`, `load-template`, `save-template` | `--store-path <dir>` or `--store-url <url>` |
| **Standalone** — operate on individual PDF and/or template files | `inspect`, `test-pattern`, `test-groups`, `dry-run`, `execute`, `validate-template` | — |

Store-aware scripts accept `--store-path <dir>` (local directory) or `--store-url <url>` (API endpoint) to locate templates; these flags are mutually exclusive. When omitted, `--store-path` defaults to `./templates` relative to the process working directory — since the CWD varies by environment, always pass the store location explicitly.

## Workflow

The pipeline runs in order; classification determines the entry point. Load `references/workflow.md` for the full step-by-step guide.

1. **Classify** — match the PDF against all stored templates
2. **Inspect** — read the engine's text extraction (when no template matches)
3. **Test** — prove regex patterns against the engine's haystack
4. **Build** — author the template JSON, validate classification rules and schema
5. **Dry-run** — end-to-end extraction without output generation
6. **Execute** — full pipeline producing CSV or JSON output
7. **Store** — persist the template and verify it ranks correctly

## Routing

Consult the canonical reference before relying on memory. Each concern has a single owner.

| If the agent needs to… | Load |
| --- | --- |
| Follow the full pipeline step-by-step | `references/workflow.md` |
| Pick an `ExtractionSource` subtype for a field (`TextPattern`, `TableRows`, `TextAnchor`, `MetadataField`, `Fallback`) | `references/decision-tree.md` |
| Design a discriminating `rootMatchRule` (token selection, composite architecture, structural rules, weights, thresholds) | `references/classification.md` |
| Diagnose a `RejectedResult`, `FailedResult`, classification failure, or empty/incomplete `DryRunSucceeded` | `references/failure-tree.md` |
| Map a stderr `error.code` to a remediation branch | `references/failure-tree.md` § Stderr error.code → Branch routing |
| Copy a regex pattern from the library or adapt one to a specific PDF | `references/patterns.md` then `references/pattern-authoring.md` |
| Look up a CLI script's flags, output envelope, or error codes | `references/scripts.md` |
| Look up a template JSON property, `$kind` discriminator, enum value, or shape | `references/template-reference.md` |
| Answer whether PDF processing is local/private | `references/privacy.md` |

## Skill layout

- `SKILL.md` — this router; loaded at skill activation.
- `references/` — deep guides loaded on demand (see Routing table).
- `scripts/` — `dotnet-script` CLI surface (`_common.csx` plus 11 verb scripts).
- `assets/lib/Docuoria.dll` — bundled SDK assembly.
- `assets/schemas/template-schema.json` — JSON Schema for template authoring and validation.
- `examples/` — three worked end-to-end walkthroughs.

## Gotchas

- **`fieldType` in template JSON must be an integer (0–5), never a string.** The engine rejects string values with `RejectionReason.MalformedTemplate`. Enum: 0 String, 1 Number, 2 Integer, 3 Boolean, 4 Date, 5 Timestamp. Run `validate-template.csx` to catch this before dry-run.
- **Adapt every regex to the actual PDF.** The engine's flattened text differs from the visual layout — whitespace, line breaks, and character encoding may not match what you see. Validate with `test-pattern.csx` and `inspect.csx` rather than pasting library patterns verbatim.
