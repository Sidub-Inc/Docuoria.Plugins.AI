---
name: docuoria
description: Use for Docuoria: extract PDF data, author or classify a template, pick an ExtractionSource, write a regex, diagnose a FailedResult or RejectedResult, or check local-processing posture.
license: MIT
compatibility: Requires .NET 10 SDK and the `dotnet-script` global tool. SDK assembly (`Docuoria.dll`) is bundled under `assets/lib/`; transitive NuGet dependencies (PdfPig, Tabula, CsvHelper, pythonnet, Microsoft.Extensions.*) are resolved by `dotnet-script` at first run.
---

# Docuoria Skill

## Invocation

Invoke every script as `dotnet script scripts/<name>.csx -- --<flag> <value>` from the skill root. Flagged form is mandatory — positional arguments are rejected by `_common.csx`. Pass `--help` to any script for full flag list.

## Routing

| If the agent needs to… | Load |
| --- | --- |
| Decide which pipeline step to run next (classify → inspect → test → build → dry-run → execute → store) | `references/workflow.md` |
| Pick an `ExtractionSource` subtype for a field (`TextPattern`, `TableRows`, `TextAnchor`, `MetadataField`, `Fallback`) | `references/decision-tree.md` |
| Design a discriminating `rootMatchRule` (token selection, composite architecture, structural rules, weights, thresholds) | `references/classification.md` |
| Diagnose a `RejectedResult`, `FailedResult`, classification failure, or empty/incomplete `DryRunSucceeded` | `references/failure-tree.md` |
| Map a stderr `error.code` (`pdf-not-found`, `bad-format`, `rejected`, `failed`, `empty-result`, …) to a remediation branch | `references/failure-tree.md` § Stderr error.code → Branch routing |
| Copy a regex pattern from the library or adapt one to a specific PDF | `references/patterns.md` then `references/pattern-authoring.md` |
| Look up a CLI script's flags, output envelope, error codes, or environment variables | `references/scripts.md` |
| Look up a template JSON property, `$kind` discriminator, enum value, or shape | `references/template-reference.md` |
| Answer whether PDF processing is local/private | `references/privacy.md` |

## Package layout

This skill is packaged as a Claude Code plugin. When installed, the package lays out as:

- `.claude-plugin/plugin.json` — plugin manifest (name, version, author, keywords).
- `SKILL.md` — this router (index + invocation rule); auto-discovered as a single-skill plugin.
- `references/` — deep guides loaded on demand.
- `scripts/` — `dotnet-script` CLI surface (`_common.csx` plus 11 verb scripts; `references/scripts.md` is the canonical CLI reference).
- `assets/lib/Docuoria.dll` — bundled SDK assembly referenced by `scripts/_common.csx`.
- `assets/schemas/template-schema.json` — JSON Schema for template authoring/validation.
- `examples/` — three worked end-to-end walkthroughs.
- `MANIFEST.json` — package version + per-file SHA-256 (integrity check).

## Conventions

- Read the canonical reference for any topic before relying on memory; the routing table above identifies the single owner.
- Adapt every regex and rule to the actual PDF using `scripts/test-pattern.csx` and `scripts/inspect.csx` rather than pasting library patterns verbatim.
- Resolve engine API names (`IDocuoriaEngine`, `ExtractionSource`, `MatchRule`, `FieldMapping`) against the bundled `assets/lib/Docuoria.dll`.
