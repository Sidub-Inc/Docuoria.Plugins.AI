# Example 5 — Keep one running ledger across monthly runs

## Scenario

The user gets invoices every month and wants **one accumulating spreadsheet**: "add this
month's invoices to my expenses file." Maybe they also re-run over the same folder twice,
or aren't sure which files they already exported. This is the **recurring export** flow:
the output file is a *ledger* that must grow safely — never duplicated rows, never
clobbered by a forgotten flag, never corrupted by a crash.

A Docuoria ledger is self-describing: the CSV leads with `sourceFile,templateId`
provenance columns (JSON ledgers are arrays of `{ sourceFile, templateId, data }`
envelopes). The file itself records which documents it already contains — that is what
makes re-runs safe.

## Steps

1. **Month 1 — same command as month N.** With templates already stored (see
   [`04-survey-batch.md`](04-survey-batch.md) for the cold start), point batch-execute at
   the folder **with `--append`**:

   ```powershell
   dotnet script scripts/batch-execute.csx -- --corpus ./invoices --store-path C:/work/templates --output ./expenses-ledger.csv --append
   ```

   The ledger does not exist yet, so it is created. Example summary:

   ```json
   { "summary": { "pdfCount": 3, "succeeded": 3, "duplicates": 0, "rowsWritten": 17, "totalRows": 17, "outputPath": "...expenses-ledger.csv" } }
   ```

2. **Month 2 — sweep the whole folder again.** Do NOT try to work out which files are
   new; the ledger already knows. Run the identical command after the user drops
   `april-invoice.pdf` into the folder:

   ```json
   { "pdfs": [
       { "pdf": "april-invoice.pdf", "status": "ok", "rows": 6, "action": "appended" },
       { "pdf": "feb-invoice.pdf",  "status": "duplicate", "reason": "already-in-output" },
       { "pdf": "jan-invoice.pdf",  "status": "duplicate", "reason": "already-in-output" },
       { "pdf": "mar-invoice.pdf",  "status": "duplicate", "reason": "already-in-output" } ],
     "summary": { "pdfCount": 4, "succeeded": 1, "duplicates": 3, "rowsWritten": 6, "totalRows": 23 } }
   ```

   Duplicates are detected **before** classification — the three recorded PDFs cost no
   engine work — and exit code is `0`: a duplicate skip is the steady state, not an error.
   Re-running the exact same command again would add nothing (`rowsWritten: 0`).

3. **Tell the user what happened in their language.** Report from the envelope, including
   idempotent skips — never claim rows were added when they were already there:

   > Added April's invoice (6 charges) to `expenses-ledger.csv` — it now covers 4 invoices,
   > 23 lines. January–March were already in the file, so I left them untouched.

4. **Corrections use `replace`.** If a template was fixed and one month's rows must be
   refreshed, replace just that document — its rows are swapped in place, everything else
   untouched:

   ```powershell
   dotnet script scripts/execute.csx -- --pdf ./invoices/feb-invoice.pdf --template C:/work/templates/vendor-invoice.json --format csv --output ./expenses-ledger.csv --append --on-duplicate replace
   ```

5. **Respect the safety refusals — they are routing signals, not obstacles:**

   - `existing-ledger` (plain `--output` aimed at a ledger): you forgot `--append`. Add it.
   - `would-drop-sources` (plain rebuild over a ledger recording PDFs not in the corpus):
     the rebuild would lose rows that cannot be regenerated — almost always you wanted
     `--append`. Only pass `--overwrite` if the user explicitly accepts losing those rows.
   - `not-a-ledger` (`--append` aimed at some other file): wrong path, or the wrong
     `--format` for an existing ledger. Never "fix" this by editing the file.
   - A new structural variant adding columns is reported via `columnsAdded` (old rows are
     empty in the new columns) — disclose it. The existing ledger defines the output grain
     and column shape; keep authoring consistent with it (canonical field vocabulary,
     [`../references/workflow.md`](../references/workflow.md) § Step 0).

## Expected outcome

One ledger file that is correct after every run regardless of how often it is re-run:
each source PDF's rows appear exactly once, monthly sweeps only pay for new files, and
nothing the user accumulated can be silently destroyed.

## See also

- [`../references/workflow.md`](../references/workflow.md) Step 0 — Recurring exports
  (ledgers) and the canonical field vocabulary rule.
- [`../references/scripts.md`](../references/scripts.md) — `execute.csx` /
  `batch-execute.csx` ledger flags, envelopes, error codes, and exit codes.
- [`04-survey-batch.md`](04-survey-batch.md) — the cold-start batch flow that authors the
  templates a recurring ledger routes through.
