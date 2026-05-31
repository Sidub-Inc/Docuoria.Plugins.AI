# Docuoria AI Plugin

> Deterministic, local-first PDF data extraction for AI agents.

This repository distributes the **Docuoria AI Plugin** (`docuoria`), a skill
package that enables AI agents to extract structured data from PDFs using
template-driven pipelines.

**Current version: 1.0.7**

---

## Quick Start

### Claude Code

Clone and load as a local plugin:

```bash
git clone https://github.com/Sidub-Inc/Docuoria.Plugins.AI.git .plugins/docuoria
claude --plugin-dir .plugins/docuoria
```

### VS Code (GitHub Copilot)

Add as a git submodule in your workspace:

```bash
git submodule add https://github.com/Sidub-Inc/Docuoria.Plugins.AI.git .github/skills/docuoria
```

Or clone directly:

```bash
git clone https://github.com/Sidub-Inc/Docuoria.Plugins.AI.git .github/skills/docuoria
```

### Pin to a specific version

```bash
git clone --branch v1.0.7 --depth 1 \
  https://github.com/Sidub-Inc/Docuoria.Plugins.AI.git .github/skills/docuoria
```

---

## Prerequisites

| Requirement | Install |
|---|---|
| .NET 10 SDK | [dotnet.microsoft.com/download](https://dotnet.microsoft.com/download) |
| `dotnet-script` global tool | `dotnet tool install -g dotnet-script` |

Transitive NuGet dependencies (PdfPig, Tabula, CsvHelper, etc.) are resolved
automatically by `dotnet-script` on first run.

---

## Package Contents

| Path | Description |
|---|---|
| `SKILL.md` | Skill router — entry point auto-discovered by AI clients |
| `.claude-plugin/plugin.json` | Claude Code plugin manifest |
| `references/` | Deep-dive guides: workflow, patterns, classification, diagnostics |
| `scripts/` | `dotnet-script` CLI surface (14 verb scripts) |
| `assets/lib/Docuoria.dll` | Bundled SDK assembly |
| `assets/schemas/template-schema.json` | JSON Schema for template authoring and validation |
| `examples/` | Three worked end-to-end walkthroughs |
| `MANIFEST.json` | Per-file SHA-256 integrity manifest |

---

## Capabilities

- **Extract** structured data from PDFs using template-driven pipelines
- **Classify** unknown PDFs against a template library
- **Inspect** PDF structure (text blocks, tables, metadata)
- **Author** extraction templates with regex patterns and field mappings
- **Validate** templates against the JSON Schema
- **Dry-run** extractions before committing results
- **Diagnose** failed or rejected extraction results

---

## Integrity Verification

Every release includes a `MANIFEST.json` with SHA-256 hashes for each file.
Verify package integrity after cloning:

```powershell
# PowerShell — compare hashes against MANIFEST.json
$manifest = Get-Content MANIFEST.json | ConvertFrom-Json
foreach ($entry in $manifest.files) {
    $actual = (Get-FileHash -LiteralPath $entry.path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $entry.sha256) { Write-Warning "DRIFT: $($entry.path)" }
}
```

---

## Versioning

This plugin is assembled and published automatically from the
[Docuoria](https://github.com/Sidub-Inc/Docuoria) source repository using
[GitVersion](https://gitversion.net/) (Mainline mode). Version tags in this
repo mirror the source release tags.

See [Releases](https://github.com/Sidub-Inc/Docuoria.Plugins.AI/releases)
for version history.

## License

MIT — see the [source repository](https://github.com/Sidub-Inc/Docuoria) for
full license text.
