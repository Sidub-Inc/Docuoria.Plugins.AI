# Classification rule design

The `rootMatchRule` determines whether a template is eligible for a given PDF. A weak rule produces false positives -- the template classifies documents it cannot extract from, causing silent failures (empty collections, wrong data). This guide shows how to design discriminating rules that match ONLY the documents the template can actually handle.

## Contents

- [How classification scoring works](#how-classification-scoring-works) — rule confidence, the composite score, the recommendation, the proportional-confidence trap
- [Principles](#principles) — the three imperatives for every `rootMatchRule`
- [Token selection strategy](#token-selection-strategy) — bad vs good vs best tokens; how to find discriminators
- [Composite rule architecture](#composite-rule-architecture) — layering rule types with weights
- [Structural discriminators](#structural-discriminators) — `TableMatchRule`, `PageGeometryMatchRule`, `TextAnchorMatchRule`
- [Threshold strategy](#threshold-strategy) — choosing modes and thresholds per scenario
- [Declaring requirements](#declaring-requirements) — the hard gate for sibling exclusivity (canonical owner)
- [Validation checklist](#validation-checklist) — positive, negative, ranked, boundary
- [Common mistakes](#common-mistakes)
- [Worked example: splitting Microsoft invoices](#worked-example-splitting-microsoft-invoices)
- [Diagnosing classification with per-rule scores](#diagnosing-classification-with-per-rule-scores)
- [Partial-match decision (using the Ambiguity block)](#partial-match-decision-using-the-ambiguity-block) — canonical owner of extend / split / new

## How classification scoring works

Understanding the scoring model prevents accidental over-matching.

### Rule-level confidence

Each match rule returns a **confidence** in [0, 1] and compares it to its **threshold** in [0, 1]:

| Rule type | Confidence formula |
|---|---|
| `TextPatternMatchRule` (regex) | Binary: 1.0 if match, 0.0 if not |
| `TextPatternMatchRule` (AnyToken) | **Proportional:** `matched_tokens / total_tokens` |
| `TextPatternMatchRule` (AllTokens) | Binary: 1.0 if ALL match, 0.0 if not |
| `TextAnchorMatchRule` | Binary: 1.0 if text found in region |
| `TableMatchRule` | Proportional: `satisfied_criteria / specified_criteria` |
| `PageGeometryMatchRule` | Proportional: `met_criteria / specified_criteria` |
| `MetadataMatchRule` | Proportional: `matched_fields / expected_fields` |
| `CompositeMatchRule` (And) | Weighted average: `sum(confidence x weight) / sum(weight)` |
| `CompositeMatchRule` (Or) | Max weighted: `max(confidence x weight) / max(weight)` |
| `CompositeMatchRule` (Not) | Inverted: `1 - child_confidence` |

A rule **matches** when `confidence >= threshold`. Default threshold is `0.5`.

### Classification output

The engine evaluates each template and produces a **composite `classificationScore`** from three components:

| Component | Weight | Description |
|---|---|---|
| `specificityScore` | 0.5 | Fraction of declared extraction mappings with >= 1 match |
| `matchQuantityScore` | 0.3 | Volume of data extracted per collection mapping (log-scaled, or expected-row-scaled when `expectedRows` is set) |
| `coverageScore` | 0.2 | Fraction of PDF text blocks consumed by any mapping |

```
classificationScore = ruleConfidence x (0.5*specificity + 0.3*quantity + 0.2*coverage)
```

When a template declares `requirements` (see [Declaring requirements](#declaring-requirements) below) and any requirement is unsatisfied, `classificationScore` is forced to `0` and `requirementsSatisfied` is `false`. Such templates are excluded from ranked results.

The `classify.csx` script evaluates every stored template against a PDF and returns them ranked by `classificationScore` (descending), then by the six-key total ordering: `requirementsSatisfied desc`, `classificationScore desc`, `specificityScore desc`, `matchQuantityScore desc`, `coverageScore desc`, `templateIdentifier asc`.

### Interpreting the recommendation

Each match carries an engine-computed `recommendation`. Route on it -- it is the verdict:

| `recommendation` | Meaning | Agent action |
|---|---|---|
| `strong` | Root rule matched, all requirements satisfied, every declared mapping produced >= 1 match | Extract directly -- safe to dry-run |
| `partial` | Rule matched, but >= 1 declared mapping found nothing | Diagnose via the `ambiguity` block ([Partial-match decision](#partial-match-decision-using-the-ambiguity-block)); iterate on this template rather than authoring from scratch |
| `no-match` | Root rule failed, or a requirement is unsatisfied | Not a candidate. If every template returns `no-match`, author from scratch -- check `requirementsSatisfied` and the `requirements[]` breakdown first |

**Warning -- `classificationScore` is for RANKING ONLY.** Use it to order candidates and to spot near-ties (top two within ~0.1); never route on its absolute value. The coverage component counts boilerplate text, so a perfect template commonly self-scores only 0.6-0.7.

**Key insight:** two templates from the same vendor may both return `partial` for a new variant that neither fully covers. The ranked list surfaces this -- pick the closest match (highest-ranked) as a starting point for refinement.

### The proportional confidence trap

With `TextPatternMatchRule` in `AnyToken` mode (integer `0`):

- 4 tokens + threshold 0.5 -> only **2 tokens** need to match.
- 6 tokens + threshold 0.5 -> only **3 tokens** need to match.

Generic vendor tokens like `["Microsoft", "Invoice"]` will match many unrelated documents from the same vendor. This is the #1 cause of misclassification.

## Principles

When designing a `rootMatchRule`, follow these three imperatives:

1. **Match the document TYPE, not just the vendor.** Author one template per document type (invoice, credit note, statement, usage report) with type-specific markers -- never let a single template cover multiple document types from the same vendor.
2. **Give every template at least one token or feature that no other template shares.** If two templates can match the same PDF, treat classification as broken and tighten the discriminators before iterating on extraction quality.
3. **Validate every template against negative examples.** A template that matches its target PDF is necessary but not sufficient -- confirm with `evaluate-match.csx` that the template *rejects* same-vendor PDFs with different layouts before saving it.

## Token selection strategy

### Bad tokens (vendor-level -- too broad)

```json
{
  "tokens": ["Microsoft", "Invoice", "Bill To", "Total"],
  "mode": 0,
  "threshold": 0.5
}
```

Every Microsoft invoice, credit note, and statement contains these tokens. This matches everything.

### Good tokens (document-type-level -- discriminating)

```json
{
  "tokens": ["Microsoft", "Invoice", "Subscription", "Office 365", "License Qty"],
  "mode": 0,
  "threshold": 0.8
}
```

The tokens `"Subscription"`, `"Office 365"`, `"License Qty"` are specific to subscription invoices. A consumption/usage invoice won't contain them.

### Best approach -- AllTokens with discriminators

```json
{
  "tokens": ["Microsoft", "Invoice", "Subscription"],
  "mode": 1,
  "threshold": 0.5
}
```

`AllTokens` (mode `1`) requires **every** token to be present (binary 1.0 or 0.0). Add tokens that are unique to the document type. If even one discriminating token is absent, the rule fails entirely.

### How to find discriminating tokens

1. Run `inspect.csx` on the target PDF -- note distinctive section headers, product names, column headers, and billing terminology.
2. Run `inspect.csx` on other PDFs from the same vendor -- identify tokens that appear ONLY in your target.
3. Good discriminators: section headers (`"Usage Charges"`, `"Subscription Details"`), product identifiers, unique column headers, regulatory text specific to one document type.
4. Bad discriminators: vendor name, generic labels (`"Amount"`, `"Date"`, `"Page"`), boilerplate legal text shared across document types.

## Composite rule architecture

For maximum discrimination, compose multiple rule types with appropriate weights:

```json
{
  "$kind": "CompositeMatchRule",
  "operator": 0,
  "threshold": 0.85,
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
    },
    {
      "rule": {
        "$kind": "PageGeometryMatchRule",
        "expectedOrientation": 0,
        "expectedPageCount": 2,
        "threshold": 0.5
      },
      "weight": 0.5
    }
  ]
}
```

**Architecture breakdown:**

| Child | Purpose | Weight | Rationale |
|---|---|---|---|
| TextPattern (AllTokens) | Vendor gate -- broad necessary condition | 1.0 | Baseline: must be a Microsoft Invoice |
| TextPattern (AnyToken, high threshold) | Type discriminator -- narrows to subscription invoices | **2.0** | Emphasized: these tokens distinguish this layout from usage invoices |
| PageGeometry | Structural hint | 0.5 | De-emphasized: helpful but not definitive alone |

**Weighting strategy:**

- Weight `1.0` -- baseline signals (necessary but not sufficient).
- Weight `> 1.0` -- discriminators (these separate your template from siblings).
- Weight `< 1.0` -- supporting hints (adds confidence but shouldn't gate alone).

The composite `And` calculates `sum(confidence x weight) / sum(weight)`. With the example above, if the discriminator fails, the weighted average drops below the composite threshold of 0.85 even when the other rules pass.

## Structural discriminators

Text tokens alone are often insufficient for same-vendor discrimination. Use structural rules.

### TableMatchRule -- for documents with distinctive table layout

```json
{
  "$kind": "TableMatchRule",
  "minRows": 5,
  "minColumns": 4,
  "requiredHeaderTokens": ["Service Name", "Quantity", "Unit Price"],
  "threshold": 0.75
}
```

Use when: the target document has a table with specific headers that sibling documents do not share. Verify with `inspect.csx` -- check `pages[*].tables[*].headerPreview` and `pages[*].tables[*].totalRowCount`.

### PageGeometryMatchRule -- for documents with distinctive page structure

```json
{
  "$kind": "PageGeometryMatchRule",
  "expectedPageCount": 4,
  "expectedOrientation": 1,
  "threshold": 0.5
}
```

Use when: the target document has a consistent page count or orientation that distinguishes it. Landscape-only reports vs. portrait invoices are easy to discriminate.

### TextAnchorMatchRule -- for documents with distinctive spatial layout

```json
{
  "$kind": "TextAnchorMatchRule",
  "expectedContent": "Azure Usage Detail",
  "region": { "x": 50, "y": 30, "width": 300, "height": 40 },
  "pageNumber": 1,
  "threshold": 0.5
}
```

Use when: a specific text appears in a known location on the page. This is stronger than plain token matching because it validates both content AND position. Obtain coordinates from `inspect.csx` -> `pages[*].blocks[*].bounds`.

## Threshold strategy

| Scenario | Recommended approach |
|---|---|
| Single document type from a vendor | `AllTokens` mode, threshold `0.5` -- binary pass/fail is sufficient |
| Multiple document types from same vendor | Composite with discriminator weight `>= 2.0`, composite threshold `>= 0.8` |
| Documents with variable content (optional sections) | `AnyToken` mode with enough tokens that threshold still requires the discriminating ones |
| Filename-gated workflows | Add `FileNameMatchRule` child with low weight (`0.3`) as a tiebreaker, never as sole gate |

**Key rule:** raise the composite threshold when siblings are close. The composite threshold gates the entire root rule -- if your weighted average can reach 0.85 even without the discriminator matching, your threshold is too low.

## Declaring requirements

Token-based match rules cannot always distinguish sibling templates from the same vendor when both share the same text layout. **Requirements** let a template declare structural expectations that the engine enforces *after* extraction -- bypassing classification entirely when the document cannot satisfy them. This section is the canonical owner of requirements rationale; [`extraction-sources.md` § Declaring requirements](extraction-sources.md#declaring-requirements) holds the quick kind-selector.

Use requirements when:

- Two templates share all match tokens (same-vendor, same layout type).
- One template requires a collection that the other must not have (or vice versa).
- You cannot find tokens that are unique to one layout without the other.

### Requirement kinds

| `$kind` | Property | Meaning |
|---|---|---|
| `MinMatches` | `mapping`, `count` | A named mapping must have >= `count` extraction matches |
| `MinRows` | `collection`, `count` | A `RepeatingFieldMapping` collection must have >= `count` rows |
| `RequiredFields` | `fields[]` | Each listed mapping must have at least 1 match |
| `MustBeAbsent` | `mapping` | The named mapping must have 0 matches (presence disqualifies) |

A template can declare multiple requirements of different kinds; each entry uses exactly one `$kind`.

### Worked example: splitting Microsoft invoices with requirements

**Problem:** a header-only invoice and a detailed invoice share the same tokens (`["Microsoft", "Invoice"]`). The detailed template needs line items; the header-only template must never produce an empty line-item collection.

**Solution:** declare structural requirements on each template, not on the match rules.

Header-only template -- disqualify if `lineItems` appears:

```json
{
  "extractionStep": {
    "mappings": [ "...header scalar mappings..." ],
    "requirements": [
      { "$kind": "MustBeAbsent", "mapping": "lineItems" }
    ]
  }
}
```

Detailed template -- require at least one line item row:

```json
{
  "extractionStep": {
    "mappings": [ "...header scalars + RepeatingFieldMapping for lineItems..." ],
    "requirements": [
      { "$kind": "MinRows", "collection": "lineItems", "count": 1 }
    ]
  }
}
```

**Result:** when `classify.csx` runs against the header-only PDF, the detailed template's `requirementsSatisfied` is `false` (no line items extracted) and its `classificationScore` is `0` -- so the header-only template wins. Against the detailed PDF, the header-only template's `MustBeAbsent` is violated, and the detailed template wins.

**Verify with `evaluate-match.csx`** -- check `requirementsSatisfied` and the `requirements[]` array:

```powershell
dotnet script scripts/evaluate-match.csx -- --pdf header-only.pdf --template detailed.json
# expect: requirementsSatisfied: false, requirements[0].satisfied: false
dotnet script scripts/evaluate-match.csx -- --pdf detailed.pdf --template header-only.json
# expect: requirementsSatisfied: false, requirements[0].satisfied: false
```

### When NOT to use requirements

- When token discrimination already works -- requirements couple template structure to classification; fix the match rules first.
- `MinRows: 1` on a collection that could validly be empty -- this disqualifies the template for all header-only PDFs of the same type. Only use `MinRows` when the document type cannot logically exist without that collection.
- `MustBeAbsent` on a mapping whose pattern is too broad -- if the pattern matches incidental text, false-absence failures will reject valid PDFs.

## Validation checklist

Before storing a template, validate classification quality:

1. **Positive validation** -- run `evaluate-match.csx` against the target PDF:
   ```
   dotnet script scripts/evaluate-match.csx -- --pdf <target.pdf> --template <template.json>
   ```
   Must return `isMatch: true` and `recommendation: "strong"`.

2. **Negative validation** -- run against PDFs from the same vendor that should NOT match:
   ```
   dotnet script scripts/evaluate-match.csx -- --pdf <sibling.pdf> --template <template.json>
   ```
   Must return `isMatch: false` (`recommendation: "no-match"`). A `"partial"` on a sibling means the rules are not discriminating enough. If `requirementsSatisfied: false`, the requirements are correctly blocking the template.

3. **Ranked classification** -- if multiple templates are stored, verify correct ranking:
   ```
   dotnet script scripts/classify.csx -- --pdf <target.pdf> --store-path <templates-dir>
   ```
   The correct template must appear at the top of the ranked list with the highest `classificationScore`. Check the gap between the top match and the next -- a wide gap indicates strong discrimination, a narrow gap indicates ambiguity.

4. **Boundary cases** -- test with the most similar document from another vendor/type to ensure the discriminators are doing their job.

If negative validation fails, strengthen the discriminator child (add more type-specific tokens, increase its weight, or add a structural rule like `TableMatchRule`).

## Common mistakes

| Mistake | Consequence | Fix |
|---|---|---|
| Vendor tokens only | Every document from that vendor classifies at 1.0 | Add document-type-specific tokens |
| AnyToken with low threshold | Too few tokens needed to pass | Use AllTokens for critical gates, or raise threshold |
| No negative validation | Template matches sibling documents | Always test against same-vendor PDFs that should NOT match |
| Equal weights on all children | Discriminator failure doesn't drop below threshold | Weight discriminators at 2.0+ |
| Single `TextPatternMatchRule` as root | No layered defense against similar documents | Use `CompositeMatchRule` with multiple signal types |
| Testing only the target PDF | False confidence in rule quality | Test against 2-3 negative examples from same vendor |

## Worked example: splitting Microsoft invoices

**Problem:** one template uses `["Microsoft", "Invoice", "Bill To"]` in `AllTokens` mode. Both a subscription invoice and an Azure usage invoice contain all three tokens. Both classify at 1.0.

**Solution -- two templates with mutual exclusivity:**

Template A (subscription):

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
          "tokens": ["Subscription", "License", "Seats", "Renewal"],
          "mode": 0,
          "threshold": 0.5
        },
        "weight": 2.0
      }
    ]
  }
}
```

Template B (Azure usage):

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
          "tokens": ["Azure", "Usage", "Consumption", "Resource Group"],
          "mode": 0,
          "threshold": 0.5
        },
        "weight": 2.0
      }
    ]
  }
}
```

**Why this works:**

- Both require "Microsoft" + "Invoice" (baseline gate, weight 1.0).
- Template A requires subscription-specific tokens (weight 2.0) -- absent in Azure usage invoices.
- Template B requires Azure-specific tokens (weight 2.0) -- absent in subscription invoices.
- With weights [1.0, 2.0] and composite threshold 0.8: if the discriminator (weight 2.0) scores 0.0, the weighted average is `(1.0x1 + 0.0x2) / (1+2) = 0.33` -- well below 0.8.

**Validation:**

- Subscription PDF against Template A -> passes (both children match).
- Subscription PDF against Template B -> fails (Azure tokens absent, avg drops below 0.8).
- Usage PDF against Template A -> fails (subscription tokens absent).
- Usage PDF against Template B -> passes (both children match).

## Diagnosing classification with per-rule scores

When `evaluate-match.csx` returns its result, the `matchedRules` array includes a summary for every rule in the tree -- not just the root. Each summary carries the individual rule's `confidence` score.

For a composite root with two children, the output looks like:

```json
{
  "confidence": 0.89,
  "matchedRules": [
    { "ruleType": "CompositeMatchRule",    "matched": true,  "confidence": 0.89, "detail": null },
    { "ruleType": "TextPatternMatchRule",  "matched": true,  "confidence": 1.0,  "detail": null },
    { "ruleType": "TextPatternMatchRule",  "matched": true,  "confidence": 0.75, "detail": null }
  ]
}
```

### How to read the diagnostic

1. **Root `confidence` = 1.0 for both templates?** The individual children reveal the issue -- look for children where confidence is 1.0 on BOTH invoice types. Those rules are not discriminating.
2. **Child has `confidence: 1.0`?** That rule matches fully -- all tokens/criteria are present. It provides no differentiation signal.
3. **Child has `confidence: 0.0`?** That rule found nothing -- either the tokens are absent or the structural criteria (table, geometry) don't match. This is the child that creates differentiation.
4. **Child has fractional confidence?** Some but not all criteria matched. This is the gradient at work -- the rule is partially relevant.

### Troubleshooting 1.0 across sibling documents

If two templates both return `confidence: 1.0` against the same PDF, every child rule scored 1.0. The fix is to add a discriminator child with type-specific tokens or structural criteria that would score < 1.0 (or 0.0) for the wrong document type. Use the per-child breakdown to verify the new discriminator actually creates separation.

---

## Partial-match decision (using the Ambiguity block)

This is the canonical owner of partial-match handling — [`workflow.md`](workflow.md) and [`troubleshooting.md`](troubleshooting.md) route here.

When `classify.csx` or `evaluate-match.csx` returns `recommendation: "partial"`, or a near-tie (`|score_top - score_second| < 0.1`), the response includes an `ambiguity` block. Use the fields in that block to decide which action to take. **You MUST stop and ask the user (Rule 2) when the evidence does not clearly point to one action — silently mutating a shared template is the highest-risk action in this skill.**

### Reading the Ambiguity block

| Field | Meaning |
| --- | --- |
| `discriminatorTokensAbsent` | Tokens in the template's `rootMatchRule` that did NOT fire against this PDF. The more tokens absent, the larger the structural gap. |
| `rootRuleChildren` | Per-child outcomes: which sub-rules fired and which didn't, with individual confidence values. |
| `extractionMappings` | Match counts for each field/collection mapping against this PDF. |
| `requirements` | Any declared requirements that are near-threshold (unsatisfied). |

### Four-action decision tree

```
Are the absent discriminator tokens structurally significant?
  |
  +- YES (section headers, document-type identifiers absent)
  |     +- Does the PDF have a different page structure?
  |     |    +- YES -> SPLIT: author a new template for this structural variant
  |     |    +- NO  -> NEW: this is a different document type, author from scratch
  |     |
  |     +- Are there at least 2 other PDFs with the same gap?
  |          +- YES -> SPLIT: structural variant confirmed; new template required
  |          +- NO  -> Investigate: may be a one-off; use --strict survey to confirm
  |
  +- NO (a single optional token absent, or minor formatting difference)
        +- EXTEND with tolerance: add the current PDF to the template's scope
             +- If missingRequiredFields is empty -> safe to extend
             +- If missingRequiredFields is non-empty -> extend + fix extraction patterns first
```

### Actions

| Action | What to do |
| --- | --- |
| **Extend** | The existing template already covers this PDF. Dry-run it; fix any completeness issues. |
| **Extend-with-tolerance** | Lower the `rootMatchRule` threshold slightly (e.g. from 0.9 to 0.8 for an AnyToken rule), or remove one over-specific token that isn't structurally distinctive. |
| **Split** | Create a sibling template for the structural variant. Use `survey.csx` to confirm how many PDFs share this variant before authoring. |
| **New** | This is a different document type. Author a new template from scratch. |
