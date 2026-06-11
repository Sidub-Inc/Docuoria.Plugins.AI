# Example 4 — Survey and extract a batch of PDFs

## Scenario

A folder holds many PDFs of mixed types — say a quarter of invoices from one vendor, plus a few credit notes and statements. The user asks to "pull all of these into a spreadsheet." This is **batch** scope, so the run starts at Step 0 (Survey), not Step 1 (Classify). Authoring one template per PDF — or building for the first PDF and missing a structural variant later — is the failure this flow prevents.

## Steps

1. **Survey the corpus** to gather structural facts:

   ```powershell
   dotnet script scripts/survey.csx -- --corpus ./inbox --store-path ./templates --strict
   ```

   Example output:
   ```json
   {
     "pdfCount": 6,
     "matchedGroups": [
       { "template": "vendor-invoice", "pdfs": ["inv-001.pdf", "inv-002.pdf", "inv-003.pdf"], "representative": "inv-001.pdf" }
     ],
     "unmatched": [
       { "pdf": "cn-101.pdf", "pageCount": 2, "structuralTokens": ["credit", "memo", "refund", "return"] },
       { "pdf": "cn-102.pdf", "pageCount": 2, "structuralTokens": ["credit", "memo", "refund", "return"] },
       { "pdf": "stmt-9.pdf", "pageCount": 1, "structuralTokens": ["statement", "opening", "closing", "balance"] }
     ],
     "guidance": "This survey reports structural facts, not a final template count. ... Do not author one template per PDF."
   }
   ```

2. **Read the facts — survey does not decide for you.** It reports what is reliable and leaves the grouping judgment to you (just like an `inspect` result):
   - `matchedGroups`: `inv-001..003` already classify to `vendor-invoice`. **Reuse it** — no authoring needed.
   - `unmatched`: three PDFs need templates. **Reason about how many:**
     - `cn-101` and `cn-102` share the same `structuralTokens` (`credit, memo, refund, return`) and the same `pageCount` → almost certainly **one** credit-note template. Confirm by inspecting both.
     - `stmt-9` has entirely different tokens (`statement, balance …`) → a **separate** statement template.
     - If two unmatched PDFs differed only in `pageCount` but shared tokens, that is usually the **same** template with extra line-item pages — inspect across all pages (Rule 1) before splitting.
   - Conclusion here: **2 new templates** (credit note, statement), not 3.

3. **Settle the outcome with the user if it is ambiguous (Rule 2).** The corpus mixes invoices, credit notes, and a statement. If the request does not already say, ask one outcome question, e.g.:
   > Your folder has 3 invoices, 2 credit notes, and 1 statement. What should I put in your spreadsheet?
   > - Everything, each type on its own sheet/section *(Recommended)*
   > - Just the invoices
   > - Just the credit notes

4. **Author one template per structure you concluded exists**, using a representative PDF. For each, follow Steps 1–7 of [`../references/workflow.md`](../references/workflow.md):
   - `vendor-invoice` already classifies — confirm with `classify.csx` on `inv-001.pdf`, then dry-run the whole group.
   - Credit note — inspect `cn-101.pdf`, test patterns, author one template covering both `cn-101`/`cn-102` (Steps 2–4), reading [`../references/extraction-sources.md`](../references/extraction-sources.md) and [`../references/template-reference.md`](../references/template-reference.md) first.
   - Statement — author from `stmt-9.pdf`.

5. **Use the structures as cross-validation sets (negative validation is a blocking gate).** PDFs you grouped together are positive examples for that group's template and **negative** examples for the others:

   ```powershell
   # positive: a credit note against the new credit-note template
   dotnet script scripts/evaluate-match.csx -- --pdf cn-101.pdf --template credit-note.json   # expect recommendation "strong"
   # negative: an invoice against the credit-note template
   dotnet script scripts/evaluate-match.csx -- --pdf inv-001.pdf --template credit-note.json   # expect recommendation "no-match"
   ```

   If an invoice returns anything other than `"no-match"` against the credit-note template, strengthen the discriminator or declare a `requirements` gate (e.g. `MustBeAbsent` on a mapping unique to invoices) before storing. See [`../references/classification.md` § Declaring requirements](../references/classification.md#declaring-requirements).

6. **Store each new template, then re-verify ranking across the corpus:**

   ```powershell
   dotnet script scripts/save-template.csx -- --template credit-note.json --store-path ./templates
   dotnet script scripts/classify.csx -- --pdf cn-101.pdf --store-path ./templates   # new template ranks #1
   dotnet script scripts/classify.csx -- --pdf inv-001.pdf --store-path ./templates  # invoices still rank to vendor-invoice
   ```

   If you **modified** an existing template (e.g. tightened `vendor-invoice`), run a regression check before saving over it:

   ```powershell
   dotnet script scripts/regression-check.csx -- --modified vendor-invoice.json --baseline-id vendor-invoice --corpus ./inbox
   ```

   Exit code `2` means a PDF regressed — fix it before storing. **Prefer splitting over loosening** a shared template; silent mutation is the highest-risk action.

7. **Extract the whole corpus in one call** once each structure has a verified template:

   ```powershell
   dotnet script scripts/batch-execute.csx -- --corpus ./inbox --store-path ./templates --output ./out/expenses.csv
   ```

   It classify-routes every PDF and merges all rows into one ledger CSV with leading `sourceFile`/`templateId` columns. Check the per-PDF entries: resolve any `"skipped"` (partial/no-match) per [`../references/classification.md`](../references/classification.md) and re-run; exit code `2` (something skipped/incomplete/failed) is a state to fix, not a success. If the user wants one file per type instead, use `--output "./out/{templateId}.csv"`. If they will add documents next month, see [`05-recurring-ledger.md`](05-recurring-ledger.md).

## Expected outcome

One template per *structure* (not per PDF) — new ones stored and rank-verified — and one merged ledger CSV (or one per structure). No PDF is left unaccounted for, and no stored template regressed.

## See also

- [`../references/workflow.md`](../references/workflow.md) Step 0 — survey is the batch entry point; it reports facts, you decide the grouping, then run Steps 1–7 per representative.
- [`../references/classification.md`](../references/classification.md) — discriminators and requirements for keeping sibling templates mutually exclusive.
- [`../references/troubleshooting.md`](../references/troubleshooting.md) — routing for incomplete runs, empty collections, and misclassification across a batch.
- [`../references/scripts.md`](../references/scripts.md) — `survey.csx` and `regression-check.csx` flags, output, and exit codes.
