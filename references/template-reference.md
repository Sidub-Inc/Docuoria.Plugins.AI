# Template JSON Reference

Complete reference for authoring `Docuoria` template JSON files. Every property name, `$kind` discriminator, and enum value in this document matches the SDK's actual serialization output — copy them verbatim.

> **Important:** `fieldType` serializes as an **integer** (0–5), not a string. **Match rule** enums (`TextPatternMatchRule.mode`, `FileNameMatchRule.mode`, `CompositeMatchRule.operator`) also serialize as **integers**. **Extraction source** enums (`TextPatternExtractionSource.mode`, `TableRowsExtractionSource.mode`, `MetadataFieldExtractionSource.standardField`) serialize as **strings** (handled by a custom converter). All field mappings require a `$kind` discriminator (`"FieldMapping"` or `"RepeatingFieldMapping"`). See the [Enum Reference](#enum-reference) section for the canonical values.

---

## Minimal template (single scalar field)

```json
{
  "identifier": "simple-invoice",
  "rootMatchRule": {
    "$kind": "TextPatternMatchRule",
    "tokens": ["Invoice"],
    "mode": 0,
    "threshold": 0.5
  },
  "dataModel": {
    "schema": {
      "name": "Invoice",
      "fields": [
        {
          "$kind": "PrimitiveFieldDefinition",
          "name": "invoiceNumber",
          "fieldType": 0
        }
      ]
    }
  },
  "extractionStep": {
    "mappings": [
      {
        "$kind": "FieldMapping",
        "fieldName": "invoiceNumber",
        "fieldType": 0,
        "source": {
          "$kind": "TextPatternExtractionSource",
          "mode": "Pattern",
          "regexPattern": "Invoice\\s*#?:?\\s*(?<value>\\S+)"
        }
      }
    ]
  },
  "intermediateSteps": [],
  "publishStep": {}
}
```

## Complex template (scalar fields + repeating collection)

```json
{
  "identifier": "invoice-with-line-items",
  "rootMatchRule": {
    "$kind": "CompositeMatchRule",
    "operator": 0,
    "children": [
      {
        "rule": {
          "$kind": "TextPatternMatchRule",
          "tokens": ["Invoice", "Total"],
          "mode": 1,
          "threshold": 1.0
        },
        "weight": 1.0
      },
      {
        "rule": {
          "$kind": "FileNameMatchRule",
          "pattern": "*.pdf",
          "mode": 0,
          "threshold": 0.5
        },
        "weight": 0.5
      }
    ],
    "threshold": 0.8
  },
  "dataModel": {
    "schema": {
      "name": "InvoiceData",
      "fields": [
        {
          "$kind": "PrimitiveFieldDefinition",
          "name": "invoiceNumber",
          "fieldType": 0
        },
        {
          "$kind": "PrimitiveFieldDefinition",
          "name": "invoiceDate",
          "fieldType": 4
        },
        {
          "$kind": "PrimitiveFieldDefinition",
          "name": "totalAmount",
          "fieldType": 1
        },
        {
          "$kind": "RecordFieldDefinition",
          "name": "lineItems",
          "isCollection": true,
          "record": {
            "name": "LineItem",
            "fields": [
              {
                "$kind": "PrimitiveFieldDefinition",
                "name": "productCode",
                "fieldType": 0
              },
              {
                "$kind": "PrimitiveFieldDefinition",
                "name": "description",
                "fieldType": 0
              },
              {
                "$kind": "PrimitiveFieldDefinition",
                "name": "qty",
                "fieldType": 1
              },
              {
                "$kind": "PrimitiveFieldDefinition",
                "name": "unitPrice",
                "fieldType": 1
              },
              {
                "$kind": "PrimitiveFieldDefinition",
                "name": "amount",
                "fieldType": 1
              }
            ]
          }
        }
      ]
    }
  },
  "extractionStep": {
    "mappings": [
      {
        "$kind": "FieldMapping",
        "fieldName": "invoiceNumber",
        "fieldType": 0,
        "source": {
          "$kind": "TextPatternExtractionSource",
          "mode": "Pattern",
          "regexPattern": "Invoice\\s*#:?\\s*(?<value>\\d+)"
        }
      },
      {
        "$kind": "FieldMapping",
        "fieldName": "invoiceDate",
        "fieldType": 4,
        "parseFormat": "MM/dd/yyyy",
        "source": {
          "$kind": "TextPatternExtractionSource",
          "mode": "Pattern",
          "regexPattern": "Date:?\\s*(?<value>\\d{2}/\\d{2}/\\d{4})"
        }
      },
      {
        "$kind": "FieldMapping",
        "fieldName": "totalAmount",
        "fieldType": 1,
        "source": {
          "$kind": "TextPatternExtractionSource",
          "mode": "Pattern",
          "regexPattern": "Total:?\\s*\\$?(?<value>[\\d,]+\\.\\d{2})"
        }
      },
      {
        "$kind": "RepeatingFieldMapping",
        "collectionFieldName": "lineItems",
        "elementDefinition": {
          "name": "LineItem",
          "fields": [
            { "$kind": "PrimitiveFieldDefinition", "name": "productCode", "fieldType": 0 },
            { "$kind": "PrimitiveFieldDefinition", "name": "description", "fieldType": 0 },
            { "$kind": "PrimitiveFieldDefinition", "name": "qty", "fieldType": 1 },
            { "$kind": "PrimitiveFieldDefinition", "name": "unitPrice", "fieldType": 1 },
            { "$kind": "PrimitiveFieldDefinition", "name": "amount", "fieldType": 1 }
          ]
        },
        "source": {
          "$kind": "TextPatternExtractionSource",
          "mode": "AllMatches",
          "regexPattern": "(?<productCode>\\S+)\\s+(?<description>[^\\n]+?)\\s+(?<qty>[\\d.]+)\\s+(?<unitPrice>[\\d,.]+)\\s+(?<amount>[\\d,.]+)"
        },
        "subFields": [
          { "$kind": "NamedGroupSubFieldMapping", "fieldName": "productCode", "fieldType": 0, "groupName": "productCode" },
          { "$kind": "NamedGroupSubFieldMapping", "fieldName": "description", "fieldType": 0, "groupName": "description" },
          { "$kind": "NamedGroupSubFieldMapping", "fieldName": "qty", "fieldType": 1, "groupName": "qty" },
          { "$kind": "NamedGroupSubFieldMapping", "fieldName": "unitPrice", "fieldType": 1, "groupName": "unitPrice" },
          { "$kind": "NamedGroupSubFieldMapping", "fieldName": "amount", "fieldType": 1, "groupName": "amount" }
        ]
      }
    ]
  },
  "intermediateSteps": [],
  "publishStep": {}
}
```

---

## Top-level template structure

| Property | Type | Required | Description |
|---|---|---|---|
| `identifier` | string | yes | Unique template ID (min 1 char) |
| `rootMatchRule` | object | yes | Gates template execution; uses `$kind` discriminator |
| `dataModel` | object | yes | Output schema definition with `schema` property |
| `extractionStep` | object | yes | Contains `mappings` array of field extraction declarations |
| `intermediateSteps` | array | no | Ordered transformation steps (may be empty `[]`) |
| `publishStep` | object | yes | Publish step configuration (can be empty `{}`) |

---

## Extraction source types (`$kind` discriminator)

| `$kind` value | Variant | Description |
|---|---|---|
| `TextPatternExtractionSource` | `mode: "Token"` | Literal token match (returns first occurrence) |
| `TextPatternExtractionSource` | `mode: "Pattern"` | Regex match with capture group (returns first match) |
| `TextPatternExtractionSource` | `mode: "AllMatches"` | Regex match (returns all matches → collection) |
| `TextAnchorExtractionSource` | — | Spatial region + token/regex within that region |
| `TableCellExtractionSource` | — | Single table cell by row/column |
| `TableRowsExtractionSource` | — | All data rows from a PDF table → collection |
| `MetadataFieldExtractionSource` | — | PDF metadata field (Title, Author, etc.) |
| `FallbackExtractionSource` | — | Tries primary source, then fallback |

### `TextPatternExtractionSource` properties

| Property | Type | Required | Default | Description |
|---|---|---|---|---|
| `$kind` | `"TextPatternExtractionSource"` | yes | — | Discriminator |
| `mode` | string | yes | — | `"Token"`, `"Pattern"`, or `"AllMatches"` |
| `literalToken` | string | when mode = `"Token"` | — | Exact text to find |
| `regexPattern` | string | when mode = `"Pattern"` or `"AllMatches"` | — | .NET regex pattern (must include a capture group) |
| `pageNumber` | integer (1-based) | no | `null` (all pages) | Restrict extraction to a single page |
| `caseSensitive` | boolean | no | `false` | Case-sensitive matching |
| `blockSeparator` | string | no | `"\n"` | Separator between text blocks during flattening |
| `startAnchorPattern` | string | no | `null` | Regex bounding start of search region (AllMatches only) |
| `endAnchorPattern` | string | no | `null` | Regex bounding end of search region (AllMatches only) |

> **Anti-pattern:** Do NOT use `regexPattern` with `"Token"` mode or `literalToken` with `"Pattern"` / `"AllMatches"` mode. Each mode requires its specific property — mixing them will fail validation.

### `TextAnchorExtractionSource` properties

| Property | Type | Required | Default | Description |
|---|---|---|---|---|
| `$kind` | `"TextAnchorExtractionSource"` | yes | — | Discriminator |
| `region` | PdfBounds | yes | — | Spatial bounding box (see PdfBounds below) |
| `literalToken` | string | one of these | — | Exact text to find within region |
| `regexPattern` | string | one of these | — | Regex to match within region |
| `pageNumber` | integer (1-based) | no | `null` | Restrict to a single page |
| `caseSensitive` | boolean | no | `false` | Case-sensitive matching |

**PdfBounds** (nested object):

| Property | Type | Description |
|---|---|---|
| `left` | number | Left edge in PDF points |
| `top` | number | Top edge in PDF points |
| `width` | number | Width in PDF points |
| `height` | number | Height in PDF points |

### `TableCellExtractionSource` properties

| Property | Type | Required | Default | Description |
|---|---|---|---|---|
| `$kind` | `"TableCellExtractionSource"` | yes | — | Discriminator |
| `rowIndex` | integer (0-based) | yes | — | Data row index |
| `columnIndex` | integer (0-based) | when no `headerToken` | — | Column by ordinal position |
| `headerToken` | string | when no `columnIndex` | — | Column by header text match |
| `pageNumber` | integer (1-based) | no | `null` | Restrict to a single page |
| `tableIndex` | integer (0-based) | no | `0` | Which table on the page |
| `caseSensitiveHeader` | boolean | no | `false` | Case-sensitive header matching |

> **Mutual exclusivity:** Provide `columnIndex` OR `headerToken`, not both.

### `TableRowsExtractionSource` properties

| Property | Type | Required | Default | Description |
|---|---|---|---|---|
| `$kind` | `"TableRowsExtractionSource"` | yes | — | Discriminator |
| `mode` | string | yes | — | `"ByHeader"` or `"Ordinal"` |
| `pageNumber` | integer (1-based) | no | `null` | Restrict to a single page |
| `tableIndex` | integer (0-based) | no | `0` | Which table on the page |
| `headerRowIndex` | integer (0-based) | no (ByHeader only) | `0` | Row containing column headers |
| `skipRows` | integer | no (Ordinal only) | `0` | Number of header/non-data rows to skip |
| `caseSensitiveHeader` | boolean | no | `false` | Case-sensitive header matching |

### `MetadataFieldExtractionSource` properties

| Property | Type | Required | Default | Description |
|---|---|---|---|---|
| `$kind` | `"MetadataFieldExtractionSource"` | yes | — | Discriminator |
| `standardField` | string | one of these | — | Standard PDF field: `"Title"`, `"Author"`, `"Subject"`, `"Keywords"`, `"Creator"`, `"Producer"`, `"CreationDate"`, `"ModifiedDate"` |
| `rawKey` | string | one of these | — | Arbitrary PDF metadata key name |

> **Mutual exclusivity:** Provide `standardField` OR `rawKey`, not both.

### `FallbackExtractionSource` properties

| Property | Type | Required | Default | Description |
|---|---|---|---|---|
| `$kind` | `"FallbackExtractionSource"` | yes | — | Discriminator |
| `primary` | ExtractionSource | yes | — | Primary extraction source (nested object with `$kind`) |
| `fallback` | ExtractionSource | yes | — | Fallback if primary yields no result |

---

## Field mapping types

### Scalar field mapping (`FieldMapping`)

Used for single-value extractions.

| Property | Type | Required | Description |
|---|---|---|---|
| `fieldName` | string | yes | Schema field name (must match `dataModel` field) |
| `fieldType` | integer | yes | Target type (see [FieldType enum](#fieldtype)) |
| `source` | ExtractionSource | yes | Extraction source object with `$kind` |
| `parseFormat` | string | no | .NET format string for Date/Timestamp coercion |
| `cultureName` | string | no | Culture name for locale-aware coercion (e.g. `"en-US"`) |

### Repeating field mapping (`RepeatingFieldMapping`)

Used for collection extractions (line items, rows).

| Property | Type | Required | Description |
|---|---|---|---|
| `collectionFieldName` | string | yes | Schema collection field name |
| `elementDefinition` | RecordDefinition | yes | Inline schema for each element (see below) |
| `source` | ExtractionSource | yes | Collection-capable source (`AllMatches` or `TableRows`) |
| `subFields` | SubFieldMapping[] | yes | How to project source output into element fields |

### `elementDefinition` structure

```json
{
  "name": "ElementName",
  "fields": [
    { "$kind": "PrimitiveFieldDefinition", "name": "fieldA", "fieldType": 0 },
    { "$kind": "PrimitiveFieldDefinition", "name": "fieldB", "fieldType": 1 }
  ]
}
```

---

## Sub-field mapping types (`$kind` discriminator)

| `$kind` value | Key property | Use with | Description |
|---|---|---|---|
| `NamedGroupSubFieldMapping` | `groupName` (string) | `TextPatternExtractionSource` (AllMatches) | Maps a named regex capture group `(?<name>...)` |
| `RegexGroupSubFieldMapping` | `groupIndex` (integer, 1-based) | `TextPatternExtractionSource` (AllMatches) | Maps a numbered regex capture group |
| `HeaderSubFieldMapping` | `headerToken` (string) | `TableRowsExtractionSource` (ByHeader) | Maps a table column by header text match |
| `OrdinalSubFieldMapping` | `columnIndex` (integer, 0-based) | `TableRowsExtractionSource` (Ordinal) | Maps a table column by index |

All sub-field mappings share:

| Property | Type | Required | Description |
|---|---|---|---|
| `fieldName` | string | yes | Element-level field name |
| `fieldType` | integer | yes | Target primitive type (see [FieldType enum](#fieldtype)) |

---

## Match rule types (`$kind` discriminator)

All match rules share: `threshold` (number, 0–1, default 0.5).

### `TextPatternMatchRule` properties

| Property | Type | Required | Default | Description |
|---|---|---|---|---|
| `$kind` | `"TextPatternMatchRule"` | yes | — | Discriminator |
| `tokens` | string[] | one of these | — | Text tokens to search for |
| `regexPattern` | string | one of these | — | Regex pattern (alternative to tokens) |
| `mode` | integer | no | `0` (AnyToken) | `0` (AnyToken) or `1` (AllTokens); ignored when using `regexPattern` |
| `caseSensitive` | boolean | no | `false` | Case-sensitive matching |
| `pageNumber` | integer (1-based) | no | `null` | Restrict to a single page |
| `threshold` | number | no | `0.5` | Confidence threshold |

> **Mutual exclusivity:** Provide `tokens` OR `regexPattern`, not both.

### `FileNameMatchRule` properties

| Property | Type | Required | Default | Description |
|---|---|---|---|---|
| `$kind` | `"FileNameMatchRule"` | yes | — | Discriminator |
| `pattern` | string | yes | — | Glob or regex pattern |
| `mode` | integer | no | `0` (Glob) | `0` (Glob) or `1` (Regex) |
| `caseSensitive` | boolean | no | `false` | Case-sensitive matching |
| `threshold` | number | no | `0.5` | Confidence threshold |

### `TextAnchorMatchRule` properties

| Property | Type | Required | Default | Description |
|---|---|---|---|---|
| `$kind` | `"TextAnchorMatchRule"` | yes | — | Discriminator |
| `expectedContent` | string | yes | — | Expected text substring |
| `region` | PdfBounds | yes | — | Spatial bounding box |
| `pageNumber` | integer (1-based) | no | `null` | Restrict to a single page |
| `caseSensitive` | boolean | no | `false` | Case-sensitive matching |
| `threshold` | number | no | `0.5` | Confidence threshold |

### `MetadataMatchRule` properties

| Property | Type | Required | Default | Description |
|---|---|---|---|---|
| `$kind` | `"MetadataMatchRule"` | yes | — | Discriminator |
| `expectedProperties` | object | yes | — | Key-value pairs of expected metadata (e.g. `{ "Author": "Acme Corp" }`) |
| `caseSensitive` | boolean | no | `false` | Case-sensitive value matching |
| `threshold` | number | no | `0.5` | Confidence = matched properties / total expected |

### `PageGeometryMatchRule` properties

| Property | Type | Required | Default | Description |
|---|---|---|---|---|
| `$kind` | `"PageGeometryMatchRule"` | yes | — | Discriminator |
| `expectedWidth` | number | no | `null` | Expected page width in PDF points |
| `expectedHeight` | number | no | `null` | Expected page height in PDF points |
| `expectedPageCount` | integer | no | `null` | Expected total page count |
| `expectedOrientation` | integer | no | `null` | `0` (Portrait) or `1` (Landscape) |
| `toleranceInPoints` | number | no | `0.0` | Tolerance for width/height comparison |
| `threshold` | number | no | `0.5` | Confidence threshold |

### `TableMatchRule` properties

| Property | Type | Required | Default | Description |
|---|---|---|---|---|
| `$kind` | `"TableMatchRule"` | yes | — | Discriminator |
| `minRows` | integer | no | `null` | Minimum required row count |
| `minColumns` | integer | no | `null` | Minimum required column count |
| `requiredHeaderTokens` | string[] | no | `null` | Headers that must be present |
| `cellContentTokens` | string[] | no | `null` | Text that must appear in any cell |
| `caseSensitive` | boolean | no | `false` | Case-sensitive matching |
| `pageNumber` | integer (1-based) | no | `null` | Restrict to a single page |
| `threshold` | number | no | `0.5` | Confidence threshold |

### `CompositeMatchRule` properties

| Property | Type | Required | Default | Description |
|---|---|---|---|---|
| `$kind` | `"CompositeMatchRule"` | yes | — | Discriminator |
| `operator` | integer | no | `0` (And) | `0` (And), `1` (Or), or `2` (Not) |
| `children` | CompositeChildEntry[] | yes | — | Array of child entries (min 1; exactly 1 for `2` / Not) |
| `threshold` | number | no | `0.5` | Confidence threshold |

**Each `children` entry:**

| Property | Type | Required | Default | Description |
|---|---|---|---|---|
| `rule` | MatchRule | yes | — | Nested match rule object (with `$kind`) |
| `weight` | number | no | `1.0` | Relative weight in confidence calculation |

---

## Enum reference

### `FieldType`

**Serializes as integer.** Use the integer value in JSON, not the string name.

| Integer | Name | Description |
|---|---|---|
| `0` | String | Text (default) |
| `1` | Number | Decimal / floating-point |
| `2` | Integer | Whole number |
| `3` | Boolean | true / false |
| `4` | Date | Calendar date (use `parseFormat` for non-ISO formats, e.g. `"MM/dd/yyyy"`) |
| `5` | Timestamp | Date + time (UTC, use `parseFormat` if needed) |

> **Common mistake:** Writing `"fieldType": "String"` instead of `"fieldType": 0`. The string form will fail with a `JsonException` during `Template.FromJson()`.

### `TextPatternExtractionSource.mode`

Serializes as **string**.

| Value | Required property | Description |
|---|---|---|
| `"Token"` | `literalToken` | Exact literal match — no regex |
| `"Pattern"` | `regexPattern` | First regex match with a capture group named `value` |
| `"AllMatches"` | `regexPattern` | All regex matches — use for collections with named groups |

### `TextMatchMode` (for `TextPatternMatchRule.mode`)

**Serializes as integer.**

| Integer | Name | Description |
|---|---|---|
| `0` | AnyToken | Confidence = matched tokens / total tokens |
| `1` | AllTokens | Confidence = 1.0 only if all tokens match |

### `PatternMode` (for `FileNameMatchRule.mode`)

**Serializes as integer.**

| Integer | Name | Description |
|---|---|---|
| `0` | Glob | Glob pattern (`*`, `?`, character classes) |
| `1` | Regex | .NET regex pattern |

### `CompositeOperator` (for `CompositeMatchRule.operator`)

**Serializes as integer.**

| Integer | Name | Description |
|---|---|---|
| `0` | And | Weighted average of children's confidences |
| `1` | Or | Max-weighted child confidence |
| `2` | Not | Negation: 1 − child.Confidence (exactly 1 child required) |

### `PageOrientation` (for `PageGeometryMatchRule.expectedOrientation`)

**Serializes as integer.**

| Integer | Name | Description |
|---|---|---|
| `0` | Portrait | Height ≥ Width |
| `1` | Landscape | Width > Height |

### `MetadataField` (for `MetadataFieldExtractionSource.standardField`)

| Value | Description |
|---|---|
| `"Title"` | PDF title |
| `"Author"` | PDF author |
| `"Subject"` | PDF subject |
| `"Keywords"` | PDF keywords |
| `"Creator"` | Creator application |
| `"Producer"` | PDF producer |
| `"CreationDate"` | Creation date |
| `"ModifiedDate"` | Last modified date |

### `ValidationSeverity` (in validation errors)

| Value | Description |
|---|---|
| `"Info"` | Informational only |
| `"Warning"` | Non-blocking concern |
| `"Error"` | Blocking issue |

---

## Field definition types (`$kind` discriminator)

### `PrimitiveFieldDefinition`

| Property | Type | Required | Default | Description |
|---|---|---|---|---|
| `$kind` | `"PrimitiveFieldDefinition"` | yes | — | Discriminator |
| `name` | string | yes | — | Field name |
| `fieldType` | integer | no | `0` (String) | See [FieldType enum](#fieldtype) |
| `isRequired` | boolean | no | `false` | Whether the field must have a value |
| `isCollection` | boolean | no | `false` | Whether this is a collection of primitives |

> **Note:** `parseFormat` and `cultureName` belong on the **field mapping** (in `extractionStep.mappings`), not on the field definition (in `dataModel.schema.fields`). See [Scalar field mapping](#scalar-field-mapping-fieldmapping).

### `RecordFieldDefinition`

| Property | Type | Required | Default | Description |
|---|---|---|---|---|
| `$kind` | `"RecordFieldDefinition"` | yes | — | Discriminator |
| `name` | string | yes | — | Field name |
| `isRequired` | boolean | no | `false` | Whether the record must be present |
| `isCollection` | boolean | no | `false` | Set `true` for repeating data (e.g. line items) |
| `record` | RecordDefinition | yes | — | Nested record schema (with `name` and `fields`) |

---

## FieldType coercion behavior

When the engine extracts a string value and the `fieldType` specifies a non-String type, the engine coerces the value:

| FieldType | Coercion | `parseFormat` needed? | Example input → output |
|---|---|---|---|
| `0` (String) | None (passthrough) | No | `"ABC-123"` → `"ABC-123"` |
| `1` (Number) | Parse as decimal | No (invariant culture) | `"1,234.56"` → `1234.56` |
| `2` (Integer) | Parse as long | No | `"42"` → `42` |
| `3` (Boolean) | Parse true/false | No | `"true"` → `true` |
| `4` (Date) | Parse as DateOnly | Yes, if non-ISO | `"05/26/2026"` with `"parseFormat": "MM/dd/yyyy"` → `2026-05-26` |
| `5` (Timestamp) | Parse as DateTime (UTC) | Yes, if non-ISO | `"2026-05-26T14:30:00Z"` → `2026-05-26T14:30:00.0000000Z` |

When coercion fails, the engine produces a `FailedResult` with `Step = "FieldCoercion"` and structured diagnostics including `FieldPath`, `SourceText`, and `TargetTypeName`.

---

## CSV output behavior

When executing with `--format csv`:

- **Denormalization**: If the template has one collection field, the CSV is fully denormalized — one row per collection element, with scalar values repeated on every row.
- **Header naming**: Nested fields use dot-notation (e.g. `lineItems.productCode`).
- **Single collection only**: Multiple collections in one template are rejected (`RejectionReason.GeneratorRejected`). Split into separate templates.
- **Empty collection**: Produces header row only, zero data rows.
- **Null values**: Rendered as empty cells.

---

## JSON Schema

The SDK ships a JSON Schema (Draft 2020-12) accessible via `Template.GetJsonSchema()` or the bundled file `assets/schemas/template-schema.json`. Use it for editor autocompletion and pre-validation. Note: the schema is permissive with `additionalProperties: true` — always run `validate-template.csx` for full validation.
