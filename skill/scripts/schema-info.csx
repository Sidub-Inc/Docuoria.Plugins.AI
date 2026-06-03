#load "_common.csx"

#nullable enable

// SCR-12 — Dump all type/enum/mode information for the SDK.
// Args: (none)
// stdout: JSON object with fieldTypes, extractionSources, modes, matchRules, subFieldMappings.

using System.Reflection;
using Docuoria.Models;

try
{
    Cli.Help(Args, "schema-info.csx", "List all SDK types, enums, modes, and valid values");

    var fieldTypes = new Dictionary<int, string>();
    foreach (var val in Enum.GetValues<FieldType>())
    {
        fieldTypes[(int)val] = val.ToString();
    }

    var extractionSources = new[]
    {
        "TextPatternExtractionSource",
        "TextAnchorExtractionSource",
        "TableCellExtractionSource",
        "TableRowsExtractionSource",
        "MetadataFieldExtractionSource",
        "FallbackExtractionSource"
    };

    var modes = new Dictionary<string, object>
    {
        ["TextPatternExtractionSource"] = new { mode = new[] { "Token", "Pattern", "AllMatches" }, notes = new { Token = "requires 'literalToken'", Pattern = "requires 'regexPattern'", AllMatches = "requires 'regexPattern'" } },
        ["TableRowsExtractionSource"] = new { mode = new[] { "ByHeader", "Ordinal" } },
        ["TextPatternMatchRule"] = new { mode = new[] { "AnyToken", "AllTokens" } },
        ["FileNameMatchRule"] = new { mode = new[] { "Glob", "Regex" } },
        ["CompositeMatchRule"] = new { @operator = new[] { "And", "Or", "Not" } }
    };

    var matchRules = new[]
    {
        "TextPatternMatchRule",
        "FileNameMatchRule",
        "TextAnchorMatchRule",
        "MetadataMatchRule",
        "PageGeometryMatchRule",
        "TableMatchRule",
        "CompositeMatchRule"
    };

    var subFieldMappings = new[]
    {
        "NamedGroupSubFieldMapping",
        "RegexGroupSubFieldMapping",
        "HeaderSubFieldMapping",
        "OrdinalSubFieldMapping"
    };

    var metadataFields = new[]
    {
        "Title", "Author", "Subject", "Keywords",
        "Creator", "Producer", "CreationDate", "ModifiedDate"
    };

    JsonOut.Write(new
    {
        fieldTypes,
        extractionSources,
        modes,
        matchRules,
        subFieldMappings,
        metadataFields,
        notes = new
        {
            fieldType = "fieldType serializes as INTEGER (0-5), not string. Use the integer value in template JSON.",
            kind = "All polymorphic types use '$kind' discriminator with the CLR class name."
        }
    });
}
catch (Exception ex)
{
    JsonOut.Error("unhandled", ex.Message, ex.ToString(), 1);
}
