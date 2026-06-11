# Example 2 — Classify an unknown PDF

## Scenario

A PDF document arrives. You do not know whether any stored template matches it. The templates are in a local store directory (`./templates`). The goal is to route the PDF to a matching template, identify a partial match to refine, or recognise it needs a new template authored.

## How classification works

The `classify.csx` script (via `ClassifyRankedAsync`) evaluates every stored template's `rootMatchRule` against the PDF and returns the top-N templates ranked by `classificationScore` (descending). The score is composite: `requirementsSatisfied ? ruleConfidence × (0.5·specificity + 0.3·quantity + 0.2·coverage) : 0` — use it to **order** candidates only, never as an absolute gate. Each match also carries an engine-computed `recommendation` (`"strong"` / `"partial"` / `"no-match"`) that distinguishes strong matches, partial matches worth refining, and complete misses.

## Example match rules — weak vs. strong

### Weak — vendor tokens only (poor discrimination)

```json
{
  "rootMatchRule": {
    "$kind": "TextPatternMatchRule",
    "tokens": ["Microsoft", "Invoice", "Bill To"],
    "mode": 1,
    "threshold": 0.5
  }
}
```

Every Microsoft invoice (subscription, Azure, support) contains these tokens. All would score `classificationScore` near the maximum — no discrimination.

### Strong — composite with discriminator (produces clean separation)

```json
{
  "rootMatchRule": {
    "$kind": "CompositeMatchRule",
    "operator": 0,
    "threshold": 0.8,
    "children": [
      {
        "rule": {
          "$kind": "TextPatternMatchRule",
          "tokens": ["Microsoft", "Invoice"],
          "mode": 1,
          "threshold": 0.5
        },
        "weight": 1.0
      },
      {
        "rule": {
          "$kind": "TextPatternMatchRule",
          "tokens": ["Subscription", "License Qty", "Seats"],
          "mode": 0,
          "threshold": 0.6
        },
        "weight": 2.0
      }
    ]
  }
}
```

The first child is a broad gate (must be a Microsoft Invoice). The second child — weighted at 2.0 — is the **discriminator** that targets subscription invoices specifically. A subscription invoice passes both children, so the composite rule confidence is high and its `classificationScore` ranks at the top. For an Azure usage invoice the discriminator contributes 0, so the composite rule confidence is `(1.0×1 + 0.0×2) / 3 = 0.33` — below the `0.8` rule threshold, so the rule does not match and the template's `recommendation` is `"no-match"`. The recommendation cleanly separates the two.

## Steps

1. **Classify:** `dotnet script scripts/classify.csx -- --pdf <pdf> --store-path ./templates`

   Example output:
   ```json
   {
     "matches": [
       { "templateId": "ms-subscription-invoice", "classificationScore": 0.62, "recommendation": "strong", "requirementsSatisfied": true, "ambiguity": null },
       { "templateId": "ms-azure-invoice", "classificationScore": 0.21, "recommendation": "no-match", "requirementsSatisfied": true, "ambiguity": null }
     ]
   }
   ```

2. **Interpret the results:** map the top match's `recommendation` to an action using the canonical table in [`../references/classification.md` § Interpreting the recommendation](../references/classification.md#interpreting-the-recommendation). An empty `matches` array means no templates are stored (or all candidates have `requirementsSatisfied: false`) — author from scratch.

3. **For a strong match** — verify correctness:
   - Run `dotnet script scripts/dry-run.csx -- --pdf <pdf> --template <matched-id-or-path>`
   - If extraction produces expected data → done.
   - If extraction produces empty collections or nonsensical data → **misclassification**. See [`../references/troubleshooting.md` § Branch C](../references/troubleshooting.md#branch-c--classification-failure).

4. **For a partial match** — try extraction, then iterate:
   - Run dry-run with the top-scoring template. Even if some fields are incomplete, others may transfer.
   - Load the template (`load-template.csx -- --id <matched-id> --store-path ./templates`), adjust the match rules and extraction sources for the new document type.
   - Validate the updated template with positive + negative testing before storing.

5. **When authoring a new template** — validate classification before storing:
   - **Positive:** `dotnet script scripts/evaluate-match.csx -- --pdf <target.pdf> --template <new-template.json>` → `recommendation: "strong"`.
   - **Negative:** repeat with PDFs from the same vendor that should NOT match → `recommendation: "no-match"`.
   - **Ranked:** `dotnet script scripts/classify.csx -- --pdf <target.pdf> --store-path ./templates` → new template must rank #1 with a clear gap over siblings.

## Expected outcome

Either: a high-confidence match → proceed to extraction. Or: a partial match → refine an existing template. Or: no meaningful match → author a new template with clear diagnostic insight into why existing templates scored low.

## See also

- [`../references/classification.md`](../references/classification.md) — full guide to designing discriminating match rules and interpreting the `recommendation`.
- [`../references/workflow.md`](../references/workflow.md) Step 1 — classify is the entry point; the `recommendation` determines the next step.
- [`../references/troubleshooting.md` § Branch C](../references/troubleshooting.md#branch-c--classification-failure) — diagnosing classification issues with ranked output.
