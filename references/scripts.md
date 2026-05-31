# Agent Scripts

This directory hosts the **agent-facing CLI surface** for `Docuoria`. Each script
is a [`dotnet-script`](https://github.com/dotnet-script/dotnet-script) `.csx` file that
binds a single SDK verb to a deterministic JSON contract. The scripts are designed for
non-interactive automation (LLM agents, CI jobs, shell pipelines) and uphold a strict
output contract:

- **Successful runs** emit a single line of UTF-8 JSON to **stdout**, exit code `0`.
- **Errors** emit a single `{"error":{"code","message","detail"}}` line to **stderr**,
  non-zero exit code.
- All payloads serialize via `DocuoriaJsonOptions.Default` (camelCase, discriminator
  `$type` for polymorphic results, `WhenWritingNull` ignore policy — see Classify for
  the explicit-null exception).

Every script `#load "_common.csx"` to share host bootstrap, argument parsing
(`Cli.Require` / `Cli.Get` / `Cli.Has`), template-store registration, PDF stream
loading, and JSON writers.

> v1.4 invariants: confidence is binary (`1.0` / `0.0`), `ClassifyAsync` returns
> `null` when no template matches (CLS-02), and throws `InvalidOperationException`
> when no store is registered.

> **Distribution:** this directory is the **source** for the AI plugin's `scripts/`
> folder. `skills/build.ps1` copies these `.csx` files into `dist/docuoria/scripts/`
> and rewrites the SDK `#r` line in `_common.csx` to point at the bundled
> `assets/lib/Docuoria.dll`. In-repo development uses the relative
> `bin/Release/...dll` path; downstream consumers receive the bundled DLL.

## Installation

```powershell
dotnet tool install -g dotnet-script
# build the SDK once so _common.csx can reference the local DLL
dotnet build src/libs/Docuoria/Docuoria.csproj -c Debug
```

Run any script with:

```powershell
dotnet script scripts/<name>.csx -- --pdf path\to\file.pdf
```

The `--` separator forwards subsequent tokens as script arguments (exposed as the
`Args` global, an `IList<string>`).

## Environment Variables

| Variable                       | Default      | Used by              | Description                                                                                                                  |
| ------------------------------ | ------------ | -------------------- | ---------------------------------------------------------------------------------------------------------------------------- |
| `DOCUORIA_STORE`            | `local`      | store-backed scripts | `local` → file-system store; `api` → HTTP API store (`AddApiTemplateStore`).                                                 |
| `DOCUORIA_STORE_LOCAL_PATH` | `./templates`| `local` store        | Filesystem directory holding `*.json` template files.                                                                        |
| `DOCUORIA_STORE_API_URL`    | _(required)_ | `api` store          | Base URL of the templates HTTP API.                                                                                          |
| `DOCUORIA_STORE_API_KEY`    | _(optional)_ | `api` store          | Function key sent as `x-functions-key`. Maps to `ApiTemplateStoreCredentials.FunctionKey`.                                   |

## Error JSON Shape

```json
{
  "error": {
    "code": "kebab-case-identifier",
    "message": "Human-readable summary.",
    "detail": "Optional stack trace or full exception text."
  }
}
```

Common `code` values: `pdf-not-found`, `parse-error`, `already-exists`, `no-store`,
`unhandled`, `bad-format`.

---

## inspect.csx

**Synopsis.** Report low-level PDF structure (page count, text blocks, candidate
patterns) for a PDF — primary discovery step before authoring a template.

| Arg       | Required | Description                                  |
| --------- | -------- | -------------------------------------------- |
| `--pdf`   | yes      | Path to the source PDF.                      |
| `--page`  | no       | 1-based page index (default: all pages).     |

**Output schema.** `PdfInspection` payload (`pageCount`, `pages[].blocks[]`, …).

**Exit codes.** `0` success · `1` unhandled · `pdf-not-found` on missing input.

**Example.**

```powershell
dotnet script scripts/inspect.csx -- --pdf invoice.pdf --page 1
```

---

## test-pattern.csx

**Synopsis.** Evaluate a single extraction pattern against a PDF and report whether
it matched, with the captured value(s).

| Arg                  | Required | Description                                                |
| -------------------- | -------- | ---------------------------------------------------------- |
| `--pattern`          | yes      | Inline pattern source (regex or DSL block).                |
| `--pdf`              | yes      | Path to the source PDF.                                    |
| `--block-separator`  | no       | Override the block-separator regex used during extraction. |

**Output schema.** `{ hasMatches: bool, matches: [...] }`.

**Exit codes.** `0` success · `1` unhandled / parse-error · `pdf-not-found`.

**Example.**

```powershell
dotnet script scripts/test-pattern.csx -- --pattern 'Invoice #(\d+)' --pdf invoice.pdf
```

---

## test-groups.csx

**Synopsis.** Evaluate a multi-group pattern and emit each named capture group's
match set — used when authoring repeating-row extractions.

| Arg         | Required | Description                  |
| ----------- | -------- | ---------------------------- |
| `--pattern` | yes      | Multi-group pattern source.  |
| `--pdf`     | yes      | Path to the source PDF.      |

**Output schema.** `{ groups: { <name>: [matches...] } }`.

**Exit codes.** `0` success · `1` on extraction failure.

**Example.**

```powershell
dotnet script scripts/test-groups.csx -- --pattern @rows.txt --pdf invoice.pdf
```

---

## validate-template.csx

**Synopsis.** Parse a `Template` JSON file and report schema / semantic validation
results without executing it.

| Arg          | Required | Description                          |
| ------------ | -------- | ------------------------------------ |
| `--template` | yes      | Path to the template JSON file.      |

**Output schema.** `{ valid: bool, errors: [string] }`.

**Exit codes.** `0` always (even on `valid:false`) · `1` for `parse-error`.

**Example.**

```powershell
dotnet script scripts/validate-template.csx -- --template templates/invoice.json
```

---

## dry-run.csx

**Synopsis.** Execute extraction + publish steps against a PDF **without** producing a
serialized output payload — useful for end-to-end pipeline validation.

| Arg          | Required | Description                       |
| ------------ | -------- | --------------------------------- |
| `--pdf`      | yes      | Path to the source PDF.           |
| `--template` | yes      | Path to the template JSON file.   |

**Output schema.** `{ kind: "SucceededResult"|"FailedResult"|"RejectedResult", result }`.

**Exit codes.** `0` success · `1` unhandled.

**Example.**

```powershell
dotnet script scripts/dry-run.csx -- --pdf invoice.pdf --template templates/invoice.json
```

---

## execute.csx

**Synopsis.** Full pipeline run with output generation. Writes a CSV or JSON payload
either to stdout or to `--output`.

| Arg          | Required | Description                                                    |
| ------------ | -------- | -------------------------------------------------------------- |
| `--pdf`      | yes      | Path to the source PDF.                                        |
| `--template` | yes      | Path to the template JSON file.                                |
| `--format`   | yes      | `csv` or `json`.                                               |
| `--output`   | no       | Write binary payload to this path. If omitted, stdout text.    |

**Output schema.** Success: `{ status: "ok", format, output? }` (output is base64 / text).
Failure: `{ status: "rejected"|"failed", result }`.

**Exit codes.** `0` success · `1` rejected/failed · `2` `bad-format`.

**Example.**

```powershell
dotnet script scripts/execute.csx -- --pdf invoice.pdf --template templates/invoice.json --format json --output out.json
```

---

## evaluate-match.csx

**Synopsis.** Compute the aggregated match confidence between a PDF and a single
template. Confidence is `ruleConfidence × extractionProbeScore` (0.0 when either
fails, 1.0 when both are perfect). Template argument may be a file path **or** a
template identifier resolved through the configured store.

| Arg          | Required | Description                                                                       |
| ------------ | -------- | --------------------------------------------------------------------------------- |
| `--pdf`      | yes      | Path to the source PDF.                                                           |
| `--template` | yes      | File path (`.json` / contains path separator) **or** template ID for store lookup. |

**Output schema.** `{ confidence, matchedRules }`.

**Exit codes.** `0` success · `1` template not found / unhandled.

**Example.**

```powershell
dotnet script scripts/evaluate-match.csx -- --pdf invoice.pdf --template invoice
```

---

## classify.csx

**Synopsis.** Run ranked classification across **all** registered templates and return
the top matches sorted by confidence (descending).

| Arg     | Required | Description                                       |
| ------- | -------- | ------------------------------------------------- |
| `--pdf` | yes      | Path to the source PDF.                           |
| `--top` | no       | Maximum number of results to return (default: 5). |

**Output schema.** `{ matches: [{ templateId, confidence }, ...] }`. Only functional
matches are included (root rule passes AND extraction probe > 0).

**Exit codes.** `0` success (including `match:null`) · `1` `no-store` if no template
store is registered · `1` unhandled.

**Example.**

```powershell
dotnet script scripts/classify.csx -- --pdf invoice.pdf
```

---

## list-templates.csx

**Synopsis.** Enumerate template identifiers from the configured store.

**Args.** _(none)_

**Output schema.** `{ templates: [id, ...] }`.

**Exit codes.** `0` success · `1` unhandled.

**Example.**

```powershell
$env:DOCUORIA_STORE_LOCAL_PATH = "./templates"
dotnet script scripts/list-templates.csx
```

---

## load-template.csx

**Synopsis.** Resolve a template by identifier and emit its JSON representation.

| Arg        | Required | Description                                                              |
| ---------- | -------- | ------------------------------------------------------------------------ |
| `--id`     | yes      | Template identifier.                                                     |
| `--output` | no       | Write JSON to this file path instead of stdout.                          |

**Output schema.** Without `--output`: full template JSON. With `--output`:
`{ status: "ok", path }`.

**Exit codes.** `0` success · `1` not-found / unhandled.

**Example.**

```powershell
dotnet script scripts/load-template.csx -- --id invoice --output templates/invoice.json
```

---

## save-template.csx

**Synopsis.** Persist a template JSON file to the configured store. Fails with
`already-exists` unless `--overwrite` is supplied.

| Arg           | Required | Description                                                                 |
| ------------- | -------- | --------------------------------------------------------------------------- |
| `--file`      | yes      | Path to the template JSON file to persist.                                  |
| `--overwrite` | no       | Boolean switch — overwrite an existing template with the same identifier.   |

**Output schema.** `{ status: "saved", id }`.

**Exit codes.** `0` success · `1` `already-exists` / parse-error / unhandled.

**Example.**

```powershell
dotnet script scripts/save-template.csx -- --file templates/invoice.json --overwrite
```

---

## Internals — `_common.csx`

`_common.csx` is the **single bootstrap** used by every script. It:

1. References the locally-built `Docuoria.dll` and required NuGet packages
   (`PdfPig`, `Tabula`, `CsvHelper`, `pythonnet`, `Microsoft.Extensions.Hosting` /
   `DependencyInjection` / `Http`).
2. Exposes `Cli.Require / Cli.Get / Cli.Has` for argument parsing (renamed from
   `Args` to avoid shadowing the `dotnet-script` global of the same name).
3. Builds a Generic Host via `ScriptHost.CreateHost(args, includeStore: bool)` which
   wires `AddDocuoriaEngine`, `AddBuiltInMatchRules`, the CSV/JSON output
   generators, and (optionally) the template store selected by
   `DOCUORIA_STORE`.
4. Provides `JsonOut.Write` / `JsonOut.Error` writers backed by
   `DocuoriaJsonOptions.Default` and a `LoadPdf(path)` helper that exits with
   `pdf-not-found` when the input is missing.

Scripts must declare `#nullable enable` after `#load "_common.csx"` because the
nullable context does not propagate across `#load` boundaries.
